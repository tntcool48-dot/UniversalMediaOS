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
    private const string EpisodeMarkerPattern = @"\bS\d{1,2}E(\d{1,4})(?!\d)|(?<![A-Za-z0-9])(?:episode|ep|e|#)\s*0*(\d{1,4})(?!\d)|\s[-–—]\s*0*(\d{1,4})(?!\d)";
    private readonly Func<string, Action<string>?, CancellationToken, Task<List<TorrentResult>>> _search;
    private readonly string _root;
    private readonly Func<string, long> _availableSpace;
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _completed = new(StringComparer.Ordinal);

    public TemporaryEpisodeWatchService(DomainHotSwapper config)
        : this(new DualTrackerRssParser(config),
            Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "TemporaryWatch"))
    {
    }

    internal TemporaryEpisodeWatchService(DualTrackerRssParser rss, string root)
        : this(rss.SearchAsync, root)
    {
    }

    internal TemporaryEpisodeWatchService(
        Func<string, Action<string>?, CancellationToken, Task<List<TorrentResult>>> search, string root,
        Func<string, long>? availableSpace = null)
    {
        _search = search;
        _availableSpace = availableSpace ?? (directory => new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        CleanupOrphans();
    }

    public async Task<TemporaryEpisodeWatchResult> DownloadAsync(
        int catalogId, string title, IEnumerable<string> aliases, int episode, string audioPreference,
        Action<string> log, CancellationToken token = default)
    {
        if (catalogId <= 0 || string.IsNullOrWhiteSpace(title) || episode <= 0)
            throw new ArgumentException("A catalog title and numbered episode are required.");

        string audio = audioPreference.Equals("Dub", StringComparison.OrdinalIgnoreCase) ? "Dub" : "Sub";
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

            var candidates = await FindCandidatesAsync(title, aliases, episode, audio, log, token);
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
                    var selection = await TransferCandidateAsync(candidate, title, episode, audio, directory, log, token);
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
        string title, IEnumerable<string> aliases, int episode, string audio, Action<string> log, CancellationToken token)
    {
        string[] titles = new[] { title }.Concat(aliases ?? []).Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
        var results = new List<TorrentResult>();
        List<TorrentResult> Matches() => results.Where(result => !string.IsNullOrWhiteSpace(result.MagnetLink) &&
                !SeasonConflicts(title, result.Title) &&
                titles.Any(t => SeasonDownloader.TitleLooksLikeMatch(result.Title, StripSeasonMarker(t))) &&
                !Regex.IsMatch(result.Title, @"\b(movie|ova|ona|special)\b", RegexOptions.IgnoreCase) &&
                (!Regex.IsMatch(result.Title, EpisodeMarkerPattern, RegexOptions.IgnoreCase) || EpisodeMatches(result.Title, episode)) &&
                (audio != "Dub" || HasDubLabel(result.Title)))
            .GroupBy(result => string.IsNullOrWhiteSpace(result.InfoHash) ? result.MagnetLink : result.InfoHash,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Seeders).First()).ToList();
        foreach (string searchTitle in titles)
        {
            string episodeText = episode.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            if (audio == "Dub")
            {
                results.AddRange(await _search($"{searchTitle} {episodeText} Dub", log, token));
                // Dub and dual-audio feeds contain different releases. Search both
                // for the catalog title before ranking available seeder evidence.
                if (searchTitle == titles[0] || Matches().Count < 5)
                    results.AddRange(await _search($"{searchTitle} Dual Audio", log, token));
                if (Matches().Count < 5)
                    results.AddRange(await _search($"{searchTitle} English Audio", log, token));
            }
            else
            {
                results.AddRange(await _search($"{searchTitle} {episodeText}", log, token));
                if (Matches().Count < 5) results.AddRange(await _search(searchTitle, log, token));
            }
            token.ThrowIfCancellationRequested();
            if (Matches().Count >= 20) break;
        }

        return Matches()
            .OrderByDescending(result => EpisodeMatches(result.Title, episode))
            .ThenByDescending(result => result.Seeders)
            .Take(12).ToList();
    }

    private async Task<(string Path, string AudioNotice, IReadOnlyList<string> CaptionPaths)?> TransferCandidateAsync(
        TorrentResult candidate, string title, int episode, string audio, string directory,
        Action<string> log, CancellationToken token)
    {
        var settings = new EngineSettingsBuilder
        {
            AllowPortForwarding = true,
            DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Any, 0) },
            CacheDirectory = Path.Combine(directory, "engine")
        };
        using var engine = new ClientEngine(settings.ToSettings());
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
            if (SeasonConflicts(title, manager.Torrent?.Name ?? string.Empty)) return null;
            ITorrentManagerFile? video = SelectEpisodeFile(manager.Files, episode);
            if (video == null)
            {
                log("Torrent did not contain one unambiguous file for this episode.");
                return null;
            }
            if (SeasonConflicts(title, video.FullPath) || !IsInside(directory, video.FullPath))
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
            if (manager.State != TorrentState.Stopped) await manager.StopAsync(TimeSpan.FromSeconds(2));
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

    internal static ITorrentManagerFile? SelectEpisodeFile(IEnumerable<ITorrentManagerFile> files, int episode)
    {
        var matches = files.Where(file => VideoExtensions.Contains(Path.GetExtension(file.FullPath), StringComparer.OrdinalIgnoreCase) &&
            EpisodeMatches(Path.GetFileName(file.FullPath), episode)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static bool EpisodeMatches(string name, int episode)
    {
        if (episode <= 0) return false;
        string text = Path.GetFileNameWithoutExtension(name);
        var numbers = new List<int>();
        foreach (Match match in Regex.Matches(text,
            EpisodeMarkerPattern,
            RegexOptions.IgnoreCase))
        {
            string number = match.Groups[1].Success ? match.Groups[1].Value :
                match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (int.TryParse(number, out int found)) numbers.Add(found);
        }
        return numbers.Count == 1 && numbers[0] == episode &&
            !Regex.IsMatch(text, @"\b(?:episode|ep|e)\s*\d+\s*[-~]\s*\d+|\s[-–—]\s*\d+\s*[-~]\s*\d+", RegexOptions.IgnoreCase);
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
