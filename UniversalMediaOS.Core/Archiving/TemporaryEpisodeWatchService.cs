using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MonoTorrent;
using MonoTorrent.Client;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Routing;

namespace UniversalMediaOS.Core.Archiving;

/// <summary>Downloads one verified episode into app-owned temporary storage for native playback.</summary>
public sealed class TemporaryEpisodeWatchService
{
    private const long MaximumEpisodeBytes = 6L * 1024 * 1024 * 1024;
    private const long ReservedFreeBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(8);
    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".webm", ".avi", ".m4v"];
    private static readonly string[] CaptionExtensions = [".srt", ".ass", ".ssa", ".vtt"];
    private const string EpisodeUnitPattern = @"\d{1,4}(?:[.,_~-]\d{1,4})*(?:[a-uw-z])?";
    private const string EpisodeMarkerPattern = @"\bS\d{1,2}E(" + EpisodeUnitPattern + @")(?:v\d+)?(?!\w|[.,_-]\d)|(?<![A-Za-z0-9])(?:episode|ep|e|#)\s*(" + EpisodeUnitPattern + @")(?:v\d+)?(?!\w|[.,_-]\d)|\s[-–—]\s*(" + EpisodeUnitPattern + @")(?:v\d+)?(?!\w|[.,_-]\d)";
    private readonly Func<string, Action<string>?, CancellationToken, Task<List<TorrentResult>>> _search;
    private readonly string _root;
    private readonly Func<string, long> _availableSpace;
    private readonly Func<EngineSettings, ClientEngine> _createEngine;
    private readonly Func<int, string, CancellationToken, Task<bool>>? _catalogTitleIsUnique;
    private static readonly HttpClient AniListClient = CreateAniListClient();
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _completed = new(StringComparer.Ordinal);

    public TemporaryEpisodeWatchService(DomainHotSwapper config)
        : this(new DualTrackerRssParser(config).SearchAsync,
            Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "TemporaryWatch"),
            catalogTitleIsUnique: (catalogId, title, token) => AniListTitleIsUniqueAsync(AniListClient,
                string.IsNullOrWhiteSpace(config.GetSetting("AniListUrl")) ? "https://graphql.anilist.co" : config.GetSetting("AniListUrl"),
                catalogId, title, token))
    {
    }

    internal TemporaryEpisodeWatchService(DualTrackerRssParser rss, string root)
        : this(rss.SearchAsync, root)
    {
    }

    internal TemporaryEpisodeWatchService(
        Func<string, Action<string>?, CancellationToken, Task<List<TorrentResult>>> search, string root,
        Func<string, long>? availableSpace = null,
        Func<EngineSettings, ClientEngine>? createEngine = null,
        Func<int, string, CancellationToken, Task<bool>>? catalogTitleIsUnique = null)
    {
        _search = search;
        _availableSpace = availableSpace ?? (directory => new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace);
        _createEngine = createEngine ?? (settings => new ClientEngine(settings));
        _catalogTitleIsUnique = catalogTitleIsUnique;
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        CleanupOrphans();
    }

    public async Task<TemporaryEpisodeWatchResult> DownloadAsync(
        int catalogId, string title, IEnumerable<string> aliases, int episode, string audioPreference,
        Action<string> log, CancellationToken token = default, string catalogFormat = "", int catalogEpisodeCount = 0)
    {
        if (catalogId <= 0 || string.IsNullOrWhiteSpace(title) || episode <= 0)
            throw new ArgumentException("A catalog title and numbered episode are required.");

        string audio = audioPreference.Equals("Dub", StringComparison.OrdinalIgnoreCase) ? "Dub" : "Sub";
        aliases ??= [];
        bool singleSpecial = IsSingleSpecial(catalogFormat, catalogEpisodeCount, episode);
        bool numberedSpecial = IsNumberedMultiSpecial(catalogFormat, catalogEpisodeCount, episode);
        if (IsRestrictedSpecialFormat(catalogFormat) && !singleSpecial && !numberedSpecial)
            throw new ArgumentException("This special needs a supported catalog form and a known in-range episode.");
        string[]? catalogSpecialTitles = singleSpecial || numberedSpecial
            ? new[] { title }.Concat(aliases).Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : null;
        string[]? specialTitles = singleSpecial ? catalogSpecialTitles : null;
        string[]? numberedSpecialTitles = numberedSpecial ? catalogSpecialTitles : null;
        string key = $"{catalogId}:{episode}:{audio}";
        await _transferGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_completed.TryGetValue(key, out var cached))
                {
                    if (File.Exists(cached.FilePath))
                    {
                        cached.Owners++;
                        log("Reusing the completed temporary episode.");
                        return new TemporaryEpisodeWatchResult(cached.FilePath, new TemporaryEpisodeLease(() => Release(key, cached)),
                            cached.AudioNotice, cached.CaptionPaths);
                    }
                    _completed.Remove(key);
                    cached.LockHandle.Dispose();
                    DeleteOwnedDirectory(cached.Directory);
                }
            }

            var provenUnlabelledTitles = new HashSet<string>(StringComparer.Ordinal);
            var candidates = await FindCandidatesAsync(title, aliases, episode, audio, log, token,
                catalogFormat, catalogEpisodeCount, catalogId, provenUnlabelledTitles);
            if (candidates.Count == 0)
                throw new InvalidOperationException("No matching episode torrent was found. Try Stream or another episode.");

            foreach (TorrentResult candidate in candidates.Take(4))
            {
                token.ThrowIfCancellationRequested();
                string directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, ".temporary-watch"), "UniversalMediaOS temporary episode");
                FileStream? lockHandle = null;
                bool transferred = false;
                try
                {
                    lockHandle = new FileStream(Path.Combine(directory, ".active"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    var selection = await TransferCandidateAsync(candidate, title, episode, audio, directory, log, token,
                        specialTitles, numberedSpecialTitles, provenUnlabelledTitles);
                    if (selection == null) continue;
                    token.ThrowIfCancellationRequested();
                    var entry = new CacheEntry(directory, selection.Value.Path, selection.Value.AudioNotice,
                        selection.Value.CaptionPaths, lockHandle);
                    lock (_sync) _completed.Add(key, entry);
                    lockHandle = null;
                    transferred = true;
                    return new TemporaryEpisodeWatchResult(entry.FilePath, new TemporaryEpisodeLease(() => Release(key, entry)),
                        entry.AudioNotice, entry.CaptionPaths);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (InsufficientDownloadSpaceException) { throw; }
                catch (NativeTorrentStorageException) { throw; }
                catch (Exception ex)
                {
                    log($"Torrent candidate failed: {ex.Message}");
                }
                finally
                {
                    if (!transferred)
                    {
                        lockHandle?.Dispose();
                        DeleteOwnedDirectory(directory);
                    }
                }
            }

            throw new InvalidOperationException("No matching episode could be downloaded and verified. Try Stream or another episode.");
        }
        finally { _transferGate.Release(); }
    }

    internal async Task<List<TorrentResult>> FindCandidatesAsync(
        string title, IEnumerable<string> aliases, int episode, string audio, Action<string> log, CancellationToken token,
        string catalogFormat = "", int catalogEpisodeCount = 0, int catalogId = 0, HashSet<string>? provenUnlabelledTitles = null)
    {
        string[] titles = new[] { title }.Concat(aliases ?? []).Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool singleSpecial = IsSingleSpecial(catalogFormat, catalogEpisodeCount, episode);
        bool numberedSpecial = IsNumberedMultiSpecial(catalogFormat, catalogEpisodeCount, episode);
        if (IsRestrictedSpecialFormat(catalogFormat) && !singleSpecial && !numberedSpecial) return [];
        var results = new List<TorrentResult>();
        var proven = provenUnlabelledTitles ?? new HashSet<string>(StringComparer.Ordinal);
        bool incompleteSearch = false;
        async Task Search(string query)
        {
            try { results.AddRange(await _search(query, log, token)); }
            catch (TorrentSearchIncompleteException ex)
            {
                incompleteSearch = true;
                log(ex.Message);
            }
        }
        List<TorrentResult> Matches() => results.Where(result => !string.IsNullOrWhiteSpace(result.MagnetLink) &&
                (singleSpecial
                    ? FileMatchesCatalogEpisode(result.Title, episode, titles) || TryMapSingleSpecialRelease(result.Title, titles) != null
                    : numberedSpecial ? FileMatchesNumberedSpecial(result.Title, episode, titles, proven)
                    : !SeasonConflicts(title, result.Title) &&
                      titles.Any(t => SeasonDownloader.TitleLooksLikeMatch(result.Title, StripSeasonMarker(t))) &&
                      !Regex.IsMatch(result.Title, @"\b(movies?|ovas?|onas?|oads?|specials?)\b", RegexOptions.IgnoreCase) &&
                      (!Regex.IsMatch(result.Title, EpisodeMarkerPattern, RegexOptions.IgnoreCase) || EpisodeMatches(result.Title, episode))) &&
                (audio == "Dub" ? HasDubLabel(result.Title) : !HasDubLabel(result.Title) ||
                    Regex.IsMatch(result.Title, @"\bdual[\s._-]*audio\b", RegexOptions.IgnoreCase)))
            .GroupBy(result => string.IsNullOrWhiteSpace(result.InfoHash) ? result.MagnetLink : result.InfoHash,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Seeders).First()).ToList();
        if (singleSpecial && title.Contains(':'))
        {
            string franchise = title.Split(':', 2)[0].Trim();
            if (Regex.Matches(franchise, @"[a-z]{3,}", RegexOptions.IgnoreCase).Count >= 2)
                await Search($"{franchise} OVA");
        }
        foreach (string searchTitle in titles.Take(3))
        {
            string episodeText = episode.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            if (audio == "Dub")
            {
                await Search($"{searchTitle} {episodeText} Dub");
                // Dub and dual-audio feeds contain different releases. Search both
                // for the catalog title before ranking available seeder evidence.
                if (searchTitle == titles[0] || Matches().Count < 5)
                    await Search($"{searchTitle} Dual Audio");
                if (Matches().Count < 5)
                    await Search($"{searchTitle} English Audio");
            }
            else
            {
                await Search($"{searchTitle} {episodeText}");
                if (Matches().Count < 5) await Search(searchTitle);
            }
            token.ThrowIfCancellationRequested();
            if (Matches().Count >= 20) break;
        }

        if (numberedSpecial && catalogId > 0 && _catalogTitleIsUnique != null)
        {
            // A release without an OVA/Special label may name a TV work with the same title.
            // Only a whole title that AniList gives to this catalog entry alone can stand in.
            var unlabelled = results.Where(result => !string.IsNullOrWhiteSpace(result.MagnetLink) &&
                    NumberedSpecialTitle(result.Title, episode, titles, requireLabel: true) == null)
                .Select(result => NumberedSpecialTitle(result.Title, episode, titles, requireLabel: false))
                .OfType<string>().Distinct(StringComparer.Ordinal).Take(2).ToArray();
            foreach (string candidateTitle in unlabelled.Where(t => !proven.Contains(t)))
            {
                if (await _catalogTitleIsUnique(catalogId, candidateTitle, token))
                {
                    proven.Add(candidateTitle);
                    log($"AniList gives the unlabelled release title '{candidateTitle}' to this catalog entry alone.");
                }
                else
                    log($"The unlabelled release title '{candidateTitle}' was not proven unique to this catalog entry; it stays unmatched.");
            }
        }

        var matches = Matches();
        if (matches.Count == 0 && incompleteSearch)
            throw new TorrentSearchIncompleteException("No verified episode was found; torrent discovery is incomplete because a source could not be checked. Retry or repair the download sources.");
        return matches
            .OrderByDescending(result => EpisodeMatches(result.Title, episode))
            .ThenByDescending(result => result.Seeders)
            .Take(12).ToList();
    }

    private async Task<(string Path, string AudioNotice, IReadOnlyList<string> CaptionPaths)?> TransferCandidateAsync(
        TorrentResult candidate, string title, int episode, string audio, string directory,
        Action<string> log, CancellationToken token, string[]? specialTitles, string[]? numberedSpecialTitles,
        IReadOnlyCollection<string>? unlabelledNumberedTitles = null)
    {
        var settings = new EngineSettingsBuilder
        {
            AllowPortForwarding = true,
            DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Any, 0) },
            CacheDirectory = Path.Combine(directory, "engine")
        };
        using var engine = _createEngine(settings.ToSettings());
        SpecialEpisodeMapping? mapping = specialTitles == null ? null : TryMapSingleSpecialRelease(candidate.Title, specialTitles);
        try
        {
            log($"Checking torrent files: {candidate.Title}");
            // Magnet StartAsync switches directly from metadata discovery to payload
            // transfer. Get metadata separately so no batch file starts before selection.
            ReadOnlyMemory<byte> metadata;
            using (var metadataLimit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                metadataLimit.CancelAfter(MetadataTimeout);
                try { metadata = await engine.DownloadMetadataAsync(MagnetLink.Parse(candidate.MagnetLink), metadataLimit.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    log("Torrent metadata timed out; trying another result.");
                    return null;
                }
            }
            var torrent = Torrent.Load(metadata.Span);
            var manager = await engine.AddAsync(torrent, directory);
            try
            {
                token.ThrowIfCancellationRequested();
                if (TorrentFileContainment.FindOutside(manager.Files, directory) != null)
                {
                    log("Torrent lists a file outside its download folder; trying another result.");
                    return null;
                }
                string torrentName = manager.Torrent?.Name ?? string.Empty;
                if (numberedSpecialTitles != null &&
                    !FileMatchesNumberedSpecial(torrentName, episode, numberedSpecialTitles, unlabelledNumberedTitles))
                {
                    log("Torrent metadata does not identify the selected numbered catalog special.");
                    return null;
                }
                if (mapping == null && SeasonConflicts(title, torrentName)) return null;
                if (mapping != null ? !FileMatchesSpecialMapping(torrentName, mapping) :
                    specialTitles != null && !specialTitles.Any(t => TorrentMatchesSeries(torrentName, t)))
                {
                    log("Torrent metadata does not identify the selected catalog special.");
                    return null;
                }
                ITorrentManagerFile? video = SelectEpisodeFile(manager.Files, episode, specialTitles, mapping, numberedSpecialTitles,
                    unlabelledNumberedTitles);
                if (video == null)
                {
                    log("Torrent did not contain one unambiguous file for this episode.");
                    return null;
                }
                if ((mapping == null && SeasonConflicts(title, video.FullPath)) || !IsInside(directory, video.FullPath))
                {
                    log("Episode file has a conflicting season or unsafe path.");
                    return null;
                }
                if (video.Length < 5 * 1024 * 1024 || video.Length > MaximumEpisodeBytes)
                {
                    log("Episode file is outside the supported size range.");
                    return null;
                }
                string stem = Path.GetFileNameWithoutExtension(video.FullPath);
                if (mapping != null)
                    log($"Matched the selected special episode {episode}; this release labels it {mapping.ProviderLabel}.");
                var sidecars = manager.Files.Where(file => CaptionExtensions.Contains(Path.GetExtension(file.FullPath), StringComparer.OrdinalIgnoreCase) &&
                    Path.GetDirectoryName(file.FullPath)?.Equals(Path.GetDirectoryName(video.FullPath), StringComparison.OrdinalIgnoreCase) == true &&
                    Path.GetFileNameWithoutExtension(file.FullPath).StartsWith(stem, StringComparison.OrdinalIgnoreCase) &&
                    file.Length <= 20 * 1024 * 1024 && IsInside(directory, file.FullPath)).ToArray();
                CheckAvailableSpace(directory, video.Length + sidecars.Sum(file => file.Length));
                foreach (ITorrentManagerFile file in manager.Files)
                    await manager.SetFilePriorityAsync(file, ReferenceEquals(file, video) || sidecars.Contains(file)
                        ? Priority.Normal : Priority.DoNotDownload);

                log($"Downloading only episode {episode} ({video.Length / 1024 / 1024} MiB).");
                token.ThrowIfCancellationRequested();
                await manager.StartAsync();
                var deadline = DateTime.UtcNow + TransferTimeout;
                var lastProgressAt = DateTime.UtcNow;
                double lastProgress = manager.PartialProgress;
                while (manager.PartialProgress < 99.999 || !video.BitField.AllTrue)
                {
                    token.ThrowIfCancellationRequested();
                    NativeTorrentStorageException.ThrowIfFailed(manager);
                    CheckAvailableSpace(directory, 0);
                    if (DateTime.UtcNow >= deadline || DateTime.UtcNow - lastProgressAt >= StallTimeout)
                        throw new TimeoutException("Temporary episode transfer stalled or exceeded two hours.");
                    if (manager.PartialProgress > lastProgress + 0.05)
                    {
                        lastProgress = manager.PartialProgress;
                        lastProgressAt = DateTime.UtcNow;
                        log($"Episode download {lastProgress:F0}% ({manager.Monitor.DownloadRate / 1024.0 / 1024.0:F1} MiB/s).");
                    }
                    await Task.Delay(2000, token);
                }

                NativeTorrentStorageException.ThrowIfFailed(manager);
                await manager.StopAsync(TimeSpan.FromSeconds(2));
                token.ThrowIfCancellationRequested();
                CheckAvailableSpace(directory, 0);
                string path = File.Exists(video.DownloadCompleteFullPath) ? video.DownloadCompleteFullPath : video.FullPath;
                if (!File.Exists(path) || new FileInfo(path).Length != video.Length)
                    throw new IOException("Torrent reported completion without a complete episode file.");
                string[] captionPaths = sidecars.Select(file => file.DownloadCompleteFullPath).Where(File.Exists).ToArray();
                (path, captionPaths) = PublishEpisodeFiles(directory, path, episode, captionPaths);
                string notice = await VerifyMediaAsync(path, audio, candidate.Title, captionPaths.Length > 0, token);
                log("Episode file verified; opening native player.");
                return (path, notice, captionPaths);
            }
            finally
            {
                try
                {
                    if (manager.State != TorrentState.Stopped) await manager.StopAsync(TimeSpan.FromSeconds(2));
                }
                finally
                {
                    if (manager.State == TorrentState.Stopped)
                        await engine.RemoveAsync(manager, RemoveMode.KeepAllData);
                }
            }
        }
        finally
        {
            // Detach the retired DHT callbacks even if metadata never resolves.
            // Active discovery remains enabled; only this closed engine changes.
            var closedSettings = new EngineSettingsBuilder(engine.Settings) { DhtEndPoint = null };
            await engine.UpdateSettingsAsync(closedSettings.ToSettings());
        }
    }

    private void CheckAvailableSpace(string directory, long remainingBytes)
    {
        if (_availableSpace(directory) - remainingBytes < ReservedFreeBytes)
            throw new InsufficientDownloadSpaceException("Not enough free disk space for a temporary episode and safety reserve.");
    }

    internal static (string Path, string[] Captions) PublishEpisodeFiles(
        string directory, string videoPath, int episode, IReadOnlyList<string> captions)
    {
        // The native Windows file reader cannot open long release/batch paths.
        // Identity was checked against the original torrent names before transfer.
        string stem = Path.GetFileNameWithoutExtension(videoPath);
        if (episode <= 0 || !IsInside(directory, videoPath) ||
            captions.Any(path => !IsInside(directory, path)))
            throw new InvalidDataException("Temporary playback files must remain inside their owned job.");
        string shortStem = $"episode-{episode:D4}";
        string published = Path.Combine(directory, shortStem + Path.GetExtension(videoPath));
        var publishedCaptions = new List<string>();
        if (!published.Equals(videoPath, StringComparison.OrdinalIgnoreCase)) File.Move(videoPath, published);
        for (int index = 0; index < captions.Count; index++)
        {
            string name = Path.GetFileName(captions[index]);
            string suffix = name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) ? name[stem.Length..] : string.Empty;
            // Preserve language suffixes when present; never infer a language from
            // the selected audio mode or invent one for an unusually long suffix.
            string shortName = suffix.Length is > 0 and <= 64
                ? shortStem + suffix : $"caption-{index:D2}{Path.GetExtension(name)}";
            string destination = Path.Combine(directory, shortName);
            if (!destination.Equals(captions[index], StringComparison.OrdinalIgnoreCase)) File.Move(captions[index], destination);
            publishedCaptions.Add(destination);
        }
        return (published, publishedCaptions.ToArray());
    }

    private static async Task<string> VerifyMediaAsync(string path, string audio, string title, bool hasSidecar, CancellationToken token)
    {
        string managed = Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "Services", "ffprobe.exe");
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = File.Exists(managed) ? managed : "ffprobe",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (string arg in new[] { "-v", "error", "-show_streams", "-of", "json", path }) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(limit.Token);
        try { await process.WaitForExitAsync(limit.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await error;
        if (process.ExitCode != 0) throw new InvalidDataException("ffprobe could not read the downloaded episode.");
        using var json = JsonDocument.Parse(await output);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        if (!streams.Any(stream => stream.TryGetProperty("codec_type", out var kind) && kind.GetString() == "video"))
            throw new InvalidDataException("Downloaded file has no readable video track.");
        if (!streams.Any(stream => stream.TryGetProperty("codec_type", out var kind) && kind.GetString() == "audio"))
            throw new InvalidDataException("Downloaded file has no readable audio track.");
        if (audio != "Dub")
        {
            bool embeddedCaptions = streams.Any(stream => stream.TryGetProperty("codec_type", out var kind) && kind.GetString() == "subtitle");
            return embeddedCaptions || hasSidecar ? string.Empty :
                "No selectable subtitle track was detected. Captions may be burned into the video.";
        }
        bool englishTag = streams.Any(stream => stream.TryGetProperty("codec_type", out var kind) && kind.GetString() == "audio" &&
            stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var language) &&
            language.GetString() is "eng" or "en");
        if (englishTag) return string.Empty;
        if (!HasDubLabel(title)) throw new InvalidDataException("Dub was requested but the release has no Dub evidence.");
        return "Release says Dub; audio language is not tagged, so verify it in the player.";
    }

    internal static ITorrentManagerFile? SelectEpisodeFile(IEnumerable<ITorrentManagerFile> files, int episode, string[]? specialTitles = null,
        SpecialEpisodeMapping? mapping = null, string[]? numberedSpecialTitles = null, IReadOnlyCollection<string>? unlabelledNumberedTitles = null)
    {
        var videos = files.Where(file => VideoExtensions.Contains(Path.GetExtension(file.FullPath), StringComparer.OrdinalIgnoreCase)).ToArray();
        if ((mapping != null || numberedSpecialTitles != null) && videos.Length != 1) return null;
        var matches = videos.Where(file => numberedSpecialTitles != null
            ? FileMatchesNumberedSpecial(Path.GetFileName(file.FullPath), episode, numberedSpecialTitles, unlabelledNumberedTitles)
            : mapping != null
            ? FileMatchesSpecialMapping(Path.GetFileName(file.FullPath), mapping)
            : FileMatchesCatalogEpisode(Path.GetFileName(file.FullPath), episode, specialTitles)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal sealed record SpecialEpisodeMapping(string CatalogTitle, string Franchise, decimal ProviderNumber, int? ProviderSeason)
    {
        public string ProviderLabel => (ProviderSeason.HasValue ? $"Season {ProviderSeason} " : "") +
            "Episode " + ProviderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static SpecialEpisodeMapping? TryMapSingleSpecialRelease(string release, string[] titles)
    {
        if (!Regex.IsMatch(release, @"\b(?:ova|special)\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(release, @"\b(?:batch|complete)\b|\b(?:ova|special)\s*\d", RegexOptions.IgnoreCase)) return null;
        var units = Regex.Matches(release, EpisodeMarkerPattern, RegexOptions.IgnoreCase);
        if (units.Count != 1 || !TryReadProviderNumber(units[0], out decimal number)) return null;
        foreach (string catalog in titles)
        {
            int colon = catalog.IndexOf(':');
            int subtitle = catalog.LastIndexOf(" - ", StringComparison.Ordinal);
            if (colon <= 0 || subtitle <= colon || Regex.Matches(catalog[(subtitle + 3)..], @"[a-z]{3,}", RegexOptions.IgnoreCase).Count < 2) continue;
            string franchise = catalog[..colon].Trim();
            string work = Regex.Replace(catalog, @"\b(?:cour|part|season)\s*\d+\b", " ", RegexOptions.IgnoreCase);
            if (SeasonDownloader.TitleLooksLikeMatch(release, work) &&
                Regex.Matches(franchise, @"[a-z]{3,}", RegexOptions.IgnoreCase).Count >= 2)
                return new SpecialEpisodeMapping(catalog, franchise, number, ReadProviderSeason(release));
        }
        return null;
    }

    private static bool TryReadProviderNumber(Match match, out decimal number)
    {
        number = 0;
        string raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        return Regex.IsMatch(raw, @"^\d+(?:[.,]\d+)?$") &&
            decimal.TryParse(raw.Replace(',', '.'), System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    private static int? ReadProviderSeason(string name)
    {
        var match = Regex.Match(name, @"\b(?:season\s*|s)(\d{1,2})(?:\b|e\d)", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    internal static bool FileMatchesSpecialMapping(string name, SpecialEpisodeMapping mapping)
    {
        string file = Path.GetFileName(name);
        if (!SeasonDownloader.TitleLooksLikeMatch(file, mapping.Franchise)) return false;
        var units = Regex.Matches(file, EpisodeMarkerPattern, RegexOptions.IgnoreCase);
        int? season = ReadProviderSeason(file);
        return units.Count == 1 && TryReadProviderNumber(units[0], out decimal number) && number == mapping.ProviderNumber &&
            !(season.HasValue && mapping.ProviderSeason.HasValue && season != mapping.ProviderSeason) &&
            !Regex.IsMatch(file, @"\d+\s*[-~]\s*\d+|\b(?:batch|complete)\b", RegexOptions.IgnoreCase);
    }

    private static bool IsSingleSpecial(string format, int count, int episode) =>
        count == 1 && episode == 1 && (string.Equals(format, "SPECIAL", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(format, "OVA", StringComparison.OrdinalIgnoreCase));

    private static bool IsNumberedMultiSpecial(string format, int count, int episode) =>
        count > 1 && episode >= 1 && episode <= count &&
        (string.Equals(format, "SPECIAL", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(format, "OVA", StringComparison.OrdinalIgnoreCase));

    private static bool IsRestrictedSpecialFormat(string format) =>
        string.Equals(format, "SPECIAL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "OVA", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateAniListClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalMediaOS/1.0");
        return client;
    }

    /// <summary>One bounded AniList search; any failure or ambiguity leaves the title unproven.</summary>
    internal static async Task<bool> AniListTitleIsUniqueAsync(HttpClient client, string endpoint, int catalogId, string title,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        const string query = "query($search:String){Page(page:1,perPage:50){pageInfo{hasNextPage}" +
            "media(search:$search,type:ANIME){id title{romaji english native} synonyms}}}";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { query, variables = new { search = title } }),
                System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content, limit.Token);
            if (!response.IsSuccessStatusCode) return false;
            byte[] body = await response.Content.ReadAsByteArrayAsync(limit.Token);
            return body.Length <= 256 * 1024 && AniListTitleBelongsOnlyTo(System.Text.Encoding.UTF8.GetString(body), catalogId, title);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
    }

    /// <summary>True only for a complete page where exactly one entry, the requested one, carries the whole title.</summary>
    internal static bool AniListTitleBelongsOnlyTo(string json, int catalogId, string title)
    {
        string wanted = TitleKey(title);
        if (catalogId <= 0 || wanted.Length == 0) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("Page", out var page) || page.ValueKind != JsonValueKind.Object ||
                !page.TryGetProperty("pageInfo", out var info) || !info.TryGetProperty("hasNextPage", out var next) ||
                next.ValueKind != JsonValueKind.False ||
                !page.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array) return false;
            var owners = new List<int>();
            foreach (var entry in media.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var id) || !id.TryGetInt32(out int entryId)) return false;
                var names = new List<string>();
                if (entry.TryGetProperty("title", out var titles) && titles.ValueKind == JsonValueKind.Object)
                    names.AddRange(titles.EnumerateObject().Where(name => name.Value.ValueKind == JsonValueKind.String)
                        .Select(name => name.Value.GetString()!));
                if (entry.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
                    names.AddRange(synonyms.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String)
                        .Select(name => name.GetString()!));
                // Catalog qualifiers such as "(TV)" or "(2011)" are absent from release names,
                // so a qualified title still makes the bare title ambiguous.
                if (names.Any(name => TitleKey(name) == wanted ||
                    TitleKey(Regex.Replace(name, @"(?:\s*[(\[][^()\[\]]{1,40}[)\]])+\s*$", "")) == wanted))
                    owners.Add(entryId);
            }
            return owners.Count == 1 && owners[0] == catalogId;
        }
        catch (JsonException) { return false; }
    }

    private static string TitleWords(string value) => string.Join(' ', Regex.Matches(value, @"[\p{L}\p{N}]+")
        .Select(match => match.Value.ToLowerInvariant()));

    private static string TitleKey(string value) => TitleWords((value ?? string.Empty).Normalize());

    internal static bool FileMatchesNumberedSpecial(string name, int episode, string[] titles, IReadOnlyCollection<string>? unlabelledTitles = null) =>
        NumberedSpecialTitle(name, episode, titles, requireLabel: true) != null ||
        (unlabelledTitles is { Count: > 0 } &&
         NumberedSpecialTitle(name, episode, unlabelledTitles.ToArray(), requireLabel: false) != null);

    /// <summary>The catalog title a numbered-special release names, or null. Without the label requirement the
    /// caller must separately prove that title belongs to this catalog entry alone.</summary>
    internal static string? NumberedSpecialTitle(string name, int episode, string[] titles, bool requireLabel)
    {
        string file = Path.GetFileName(name);
        if (VideoExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            file = Path.GetFileNameWithoutExtension(file);
        file = file.Replace('_', ' '); // Older releases separate every word with underscores.
        if ((requireLabel && !Regex.IsMatch(file, @"\b(?:ova|specials?)\b", RegexOptions.IgnoreCase)) ||
            Regex.IsMatch(file, @"\b(?:batch|complete|movie|ona|tv)\b|\b(?:ova|specials?)\s*\d|" +
                @"\b(?:season\s*\d+|s\d{1,2}(?:e\d+)?|\d+(?:st|nd|rd|th)\s+season)\b", RegexOptions.IgnoreCase))
            return null;
        // Release groups write one explicit unit as Episode/Ep/E N or " - N".
        // Season-qualified and "#" units still need a mapping, not a guess.
        var units = Regex.Matches(file, EpisodeMarkerPattern, RegexOptions.IgnoreCase);
        if (units.Count != 1 || !Regex.IsMatch(units[0].Value, @"^(?:(?:episode|ep|e)\s*\d|\s[-–—])", RegexOptions.IgnoreCase) ||
            !EpisodeMatches(file, episode)) return null;
        string withoutUnit = file.Remove(units[0].Index, units[0].Length);
        // A leading release-group label is not a work title. Everything else
        // must be the whole catalog title or a known release decoration.
        withoutUnit = Regex.Replace(withoutUnit, @"^\s*\[[^\]\r\n]{1,80}\]\s*", "");
        string observed = TitleWords(withoutUnit);
        const string decoration = @"ova|specials?|sub|subbed|dub|dubbed|dual audio|english audio|english dub|" +
            @"eng|jpn|japanese|[0-9]{3,4}p|[0-9]{3,4}x[0-9]{3,4}|4k|8bit|10bit|hevc|x264|x265|h264|h265|aac|eac3|flac|opus|[a-f0-9]{8}|" +
            // Source and subtitle tags describe the file, never another work.
            @"bd|bdrip|dvd|dvdrip|web|webrip|web dl|cr|ac3|multi subs?|multisubs?";
        foreach (string title in titles.Where(title => !string.IsNullOrWhiteSpace(title)))
        {
            string literalTitle = title.Normalize().Trim();
            if (Regex.IsMatch(literalTitle, @"[\u3040-\u30ff\u31f0-\u31ff\u3400-\u9fff\uf900-\ufaff\uac00-\ud7af]"))
            {
                // Native punctuation belongs to the title. Only NFC-equivalent
                // whole titles and ASCII release decorations may match it.
                string literalObserved = withoutUnit.Normalize().Trim();
                foreach (string candidate in new[] { literalObserved,
                    Regex.Replace(literalObserved, @"^(?:ova|specials?)\s+", "", RegexOptions.IgnoreCase) })
                {
                    if (!candidate.StartsWith(literalTitle, StringComparison.OrdinalIgnoreCase)) continue;
                    string suffix = candidate[literalTitle.Length..];
                    if (suffix.Length > 0 && !char.IsWhiteSpace(suffix[0]) && !"-:|[(".Contains(suffix[0])) continue;
                    if (suffix.Any(character => !char.IsAscii(character)) ||
                        Regex.IsMatch(suffix, @"[^A-Za-z0-9\s\[\]()|:._-]")) continue;
                    string releaseWords = TitleWords(suffix);
                    if (releaseWords.Length == 0 || Regex.IsMatch(releaseWords,
                        @"^(?:" + decoration + @")(?: (?:" + decoration + @"))*$")) return title;
                }
            }
            else if (Regex.IsMatch(observed,
                @"^(?:(?:ova|specials?) )?" + Regex.Escape(TitleWords(title)) + @"(?: (?:" + decoration + @"))*$"))
                return title;
        }
        return null;
    }

    internal static bool FileMatchesCatalogEpisode(string name, int episode, string[]? specialTitles = null)
    {
        if (specialTitles == null) return EpisodeMatches(name, episode);
        if (episode != 1 || !specialTitles.Any(t => TorrentMatchesSeries(name, t))) return false;
        if (Regex.IsMatch(name, EpisodeMarkerPattern, RegexOptions.IgnoreCase)) return EpisodeMatches(name, episode);
        // Only a catalog-confirmed single special may use its exact work title
        // without a numbered unit. Never reinterpret OVA ordinals or batches.
        return !Regex.IsMatch(name, @"\b(?:ova|special)\s*\d|\b(?:batch|complete)\b", RegexOptions.IgnoreCase);
    }

    internal static bool EpisodeMatches(string name, int episode)
    {
        if (episode <= 0) return false;
        string text = Path.GetFileName(name);
        if (VideoExtensions.Contains(Path.GetExtension(text), StringComparer.OrdinalIgnoreCase) ||
            CaptionExtensions.Contains(Path.GetExtension(text), StringComparer.OrdinalIgnoreCase))
            text = Path.GetFileNameWithoutExtension(text);
        var numbers = new List<int>();
        foreach (Match match in Regex.Matches(text,
            EpisodeMarkerPattern,
            RegexOptions.IgnoreCase))
        {
            string number = match.Groups[1].Success ? match.Groups[1].Value :
                match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (!int.TryParse(number, out int found)) return false;
            numbers.Add(found);
        }
        return numbers.Count == 1 && numbers[0] == episode &&
            !Regex.IsMatch(text, @"\bS\d+E\d+\s*[-~]\s*\d+|\b(?:episode|ep|e)\s*\d+\s*[-~]\s*\d+|\s[-–—]\s*\d+\s*[-~]\s*\d+", RegexOptions.IgnoreCase);
    }

    private static bool IsInside(string directory, string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool HasDubLabel(string title) =>
        Regex.IsMatch(title, @"\b(dub(?:bed)?|dual[\s._-]*audio|english[\s._-]*audio)\b", RegexOptions.IgnoreCase);

    internal static bool TorrentMatchesSeries(string torrentTitle, string requestedTitle)
    {
        if (SeasonConflicts(requestedTitle, torrentTitle)) return false;
        return SeasonDownloader.TitleLooksLikeMatch(torrentTitle, StripSeasonMarker(requestedTitle));
    }

    private static string StripSeasonMarker(string title) =>
        Regex.Replace(title,
            @"\b(?:\d+(?:st|nd|rd|th)\s+season|season\s*\d+|s\d{1,2})\b", " ", RegexOptions.IgnoreCase);

    private static bool SeasonConflicts(string requestedTitle, string candidateTitle)
    {
        static int? Season(string value)
        {
            var match = Regex.Match(value, @"\b(?:season\s*|s)(\d{1,2})(?:\b|e\d)|\b(\d{1,2})(?:st|nd|rd|th)\s+season\b", RegexOptions.IgnoreCase);
            return match.Success ? int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
        }
        int? requested = Season(requestedTitle);
        int? candidate = Season(candidateTitle);
        return candidate.HasValue && candidate.Value != (requested ?? 1);
    }

    private void Release(string key, CacheEntry entry)
    {
        lock (_sync)
        {
            if (--entry.Owners > 0) return;
            if (_completed.TryGetValue(key, out var active) && ReferenceEquals(active, entry)) _completed.Remove(key);
            entry.LockHandle.Dispose();
        }
        DeleteOwnedDirectory(entry.Directory);
    }

    private void CleanupOrphans()
    {
        // A process killed mid-delete may have already removed its marker file.
        // This root is exclusive to temporary watch jobs, so the GUID directory
        // and containment check remain the durable ownership boundary.
        foreach (string directory in Directory.EnumerateDirectories(_root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                using var handle = new FileStream(Path.Combine(directory, ".active"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                handle.Dispose();
                DeleteOwnedDirectory(directory);
            }
            catch (IOException) { /* Another process still owns this playback. */ }
        }
    }

    private void DeleteOwnedDirectory(string directory)
    {
        try
        {
            string full = Path.GetFullPath(directory);
            if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(full), "N", out _) ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
            Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) { AppLogger.Log($"Temporary episode cleanup will retry on restart: {ex.Message}", "WARNING"); }
    }

    private sealed class CacheEntry(string directory, string filePath, string audioNotice,
        IReadOnlyList<string> captionPaths, FileStream lockHandle)
    {
        public string Directory { get; } = directory;
        public string FilePath { get; } = filePath;
        public string AudioNotice { get; } = audioNotice;
        public IReadOnlyList<string> CaptionPaths { get; } = captionPaths;
        public FileStream LockHandle { get; } = lockHandle;
        public int Owners { get; set; } = 1;
    }
}

public sealed record TemporaryEpisodeWatchResult(string FilePath, TemporaryEpisodeLease Lease,
    string AudioNotice, IReadOnlyList<string> CaptionPaths);

public sealed class TemporaryEpisodeLease(Action release) : IDisposable
{
    private Action? _release = release;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
