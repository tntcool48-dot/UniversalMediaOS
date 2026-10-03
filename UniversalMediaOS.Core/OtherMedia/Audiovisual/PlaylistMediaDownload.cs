using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Streaming;

namespace UniversalMediaOS.Core.OtherMedia;

/// <summary>Fetches a finite playlist into one owned job before a local-only remux.</summary>
internal sealed partial class PlaylistMediaDownload
{
    private static readonly HttpClient PublicClient = new(
        OtherMediaHttpClientFactory.CreatePublicNetworkHandler(allowAutoRedirect: false))
        { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Regex Attributes = new("(?:^|,)([A-Z0-9-]+)=(?:\"([^\"]*)\"|([^,]*))",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex UriAttribute = new("(?<prefix>(?:^|[:,])URI=)\"(?<uri>[^\"]+)\"",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly HttpClient _http;
    private readonly IPreparationProcessRunner _runner;

    public PlaylistMediaDownload(HttpClient? http = null, IPreparationProcessRunner? runner = null)
    { _http = http ?? PublicClient; _runner = runner ?? new PreparationProcessRunner(); }

    internal static bool IsDash(AudiovisualSource source) =>
        Path.GetExtension(source.Location.AbsolutePath).Equals(".mpd", StringComparison.OrdinalIgnoreCase) ||
        source.ContentType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase);

    public async Task<string> DownloadAsync(AudiovisualSource source, string directory, long maximumBytes,
        IProgress<AuthorizedMediaDownloadProgress>? progress, CancellationToken token)
    {
        bool dash = IsDash(source);
        long previousBytes = 0;
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        AudiovisualSource selectedSource = source;
        PlaylistJob job;
        (string Path, double Duration) manifest;
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            job = new PlaylistJob(this, selectedSource, directory, maximumBytes - previousBytes,
                progress == null ? null : new OffsetProgress(progress, previousBytes), token);
            try
            {
                manifest = dash ? await LocalizeDashAsync(selectedSource, directory, job, token).ConfigureAwait(false)
                    : await job.LocalizeAsync(source.Location, 0).ConfigureAwait(false);
                break;
            }
            catch (EndOfStreamException) when (!dash && attempt < 2 && !token.IsCancellationRequested)
            {
                attempted.Add(job.SelectedRendition);
                string? next = job.AlternateRenditions.FirstOrDefault(uri => !attempted.Contains(uri));
                if (next == null) throw;
                previousBytes += job.BytesReceived;
                if (previousBytes >= maximumBytes) throw new IOException("The playlist exceeded the configured download limit.");
                foreach (string owned in job.CreatedFiles) File.Delete(owned);
                AppLogger.Log($"Playlist download trying another advertised rendition after an incomplete resource; attempt {attempt + 2}/3.", "WARNING");
                selectedSource = source with { ValidatedHlsVariant = next };
            }
        }
        AuthorizedMediaDownloadService.CheckTemporarySpace(directory, job.BytesReceived);
        string partial = Path.Combine(directory, "media.partial");
        string output = Path.Combine(directory, "media.mkv");
        string managed = Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "Services", "ffmpeg.exe");
        using var remuxToken = CancellationTokenSource.CreateLinkedTokenSource(token);
        remuxToken.CancelAfter(TimeSpan.FromMinutes(10));
        Task<PreparationProcessResult> remux;
        try
        {
            var arguments = new List<string> { "-nostdin", "-hide_banner", "-v", "error", "-xerror",
                "-protocol_whitelist", "file,crypto", "-allowed_extensions", "ALL" };
            if (!dash) arguments.AddRange(["-extension_picky", "0"]);
            arguments.AddRange(["-i", manifest.Path, "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-c", "copy",
                "-fs", (maximumBytes + 1).ToString(CultureInfo.InvariantCulture), "-f", "matroska", partial]);
            remux = _runner.RunAsync(File.Exists(managed) ? managed : "ffmpeg", arguments, remuxToken.Token);
        }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new InvalidOperationException("Repair FFmpeg services before using Watch via download.", ex); }
        var clock = Stopwatch.StartNew();
        TimeSpan lastChange = clock.Elapsed;
        long lastSize = -1;
        try
        {
            while (!remux.IsCompleted)
            {
                await Task.WhenAny(remux, Task.Delay(250, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                long size = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (size > maximumBytes) throw new IOException("The assembled video exceeded the configured download limit.");
                AuthorizedMediaDownloadService.CheckTemporarySpace(directory, 0);
                if (size != lastSize) { lastSize = size; lastChange = clock.Elapsed; }
                if (clock.Elapsed - lastChange > TimeSpan.FromMinutes(2))
                    throw new TimeoutException("Video assembly stopped making progress. Retry or use Stream.");
            }
            var result = await remux.ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length is <= 0 ||
                new FileInfo(partial).Length > maximumBytes)
                throw new InvalidDataException("The downloaded segments could not be assembled into a complete video.");
        }
        catch
        {
            await remuxToken.CancelAsync().ConfigureAwait(false);
            try { await remux.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
            throw;
        }
        await CheckDurationAsync(partial, manifest.Duration, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        File.Move(partial, output);
        // Each path here was created by this job; no recursive or profile-wide cleanup.
        foreach (string path in job.CreatedFiles) File.Delete(path);
        progress?.Report(new(previousBytes + job.BytesReceived, previousBytes + job.BytesReceived, 100));
        return output;
    }

    private sealed class OffsetProgress(IProgress<AuthorizedMediaDownloadProgress> target, long offset)
        : IProgress<AuthorizedMediaDownloadProgress>
    {
        public void Report(AuthorizedMediaDownloadProgress value) => target.Report(value with { BytesReceived = offset + value.BytesReceived });
    }

    private async Task CheckDurationAsync(string path, double expected, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        string managed = Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "Services", "ffprobe.exe");
        var result = await _runner.RunAsync(File.Exists(managed) ? managed : "ffprobe",
            ["-v", "error", "-show_entries", "format=duration", "-of", "json", path], deadline.Token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(result.Output);
        if (result.ExitCode != 0 || !json.RootElement.TryGetProperty("format", out var format) ||
            !format.TryGetProperty("duration", out var duration) ||
            !double.TryParse(duration.GetString(), CultureInfo.InvariantCulture, out double actual) ||
            !double.IsFinite(actual) || Math.Abs(actual - expected) > Math.Max(2, expected * .002))
            throw new InvalidDataException("The assembled video is shorter or longer than the requested playlist. Retry or use Stream.");
    }

    public async Task<string[]> DownloadCaptionsAsync(AudiovisualSource source, string directory, CancellationToken token)
    {
        var paths = new List<string>();
        foreach (var track in MediaSubtitleTrack.Copy(source.Subtitles))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                byte[] bytes;
                if (track.InlineVtt.Length > 0) bytes = Encoding.UTF8.GetBytes(track.InlineVtt);
                else
                {
                    var context = new ProxySession(track.Url, track.UserAgent, track.Cookie, null, track.Referer,
                        DateTime.UtcNow, track.RequestHeaders);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    using var response = await SendAsync(new Uri(track.Url), context, deadline.Token).ConfigureAwait(false);
                    bytes = await ReadSmallAsync(response, 4 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
                }
                if (!HlsLoopbackProxy.IsSubtitlePayload(bytes)) throw new InvalidDataException("Caption file has no timed text.");
                string text = Encoding.UTF8.GetString(bytes).TrimStart('\ufeff', ' ', '\r', '\n', '\t');
                string extension = text.StartsWith("WEBVTT", StringComparison.Ordinal) ? ".vtt"
                    : text.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) ? ".ass" : ".srt";
                string language = track.Language.ToLowerInvariant() switch
                { "en" or "eng" => ".en", "ar" or "ara" => ".ar", _ => "" };
                string path = Path.Combine(directory, $"caption-{paths.Count + 1:00}{language}{extension}");
                AuthorizedMediaDownloadService.CheckTemporarySpace(directory, bytes.Length);
                await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
                paths.Add(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // A missing optional caption must not turn a valid video into false failure.
                // The handoff carries only successfully saved choices.
                AppLogger.Log($"Temporary caption {paths.Count + 1} unavailable ({ex.GetType().Name}).", "WARNING");
            }
        }
        return paths.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, ProxySession context, CancellationToken token)
    {
        // Long finite playlists contain many requests. A transient header failure must
        // not discard an otherwise healthy transfer; retry this same resource only.
        for (int attempt = 0; ; attempt++)
        {
            try { return await SendOnceAsync(uri, context, token).ConfigureAwait(false); }
            catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested &&
                (ex is OperationCanceledException || ex is HttpRequestException { StatusCode: null } ||
                 ex is HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or
                     HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or
                     HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout }))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Uri uri, ProxySession context, CancellationToken token)
    {
        for (int hop = 0; hop <= 5; hop++)
        {
            if (!HlsLoopbackProxy.IsAllowedRemoteUri(uri.AbsoluteUri))
                throw new InvalidDataException("A playlist resource is not a public HTTP(S) address.");
            await AuthorizedMediaDownloadService.ValidateResolvedHostAsync(uri, token).ConfigureAwait(false);
            using var request = HlsLoopbackProxy.BuildRequest(HttpMethod.Get, uri.AbsoluteUri, context,
                HlsLoopbackProxy.ShouldReplayCapturedHeaders(context, uri));
            using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersDeadline.CancelAfter(TimeSpan.FromSeconds(20));
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersDeadline.Token)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new HttpRequestException("The playlist resource redirect has no location.");
                uri = new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException("A playlist resource is unavailable.", null, status);
            }
            return response;
        }
        throw new HttpRequestException("A playlist resource exceeded the redirect limit.");
    }

    private static async Task<byte[]> ReadSmallAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The playlist or caption exceeded its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + read > limit) throw new InvalidDataException("The playlist or caption exceeded its size limit.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    private static Dictionary<string, string> ReadAttributes(string line) => Attributes.Matches(line[(line.IndexOf(':') + 1)..])
        .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);

    private sealed class PlaylistJob(PlaylistMediaDownload downloader, AudiovisualSource source, string directory,
        long maximumBytes, IProgress<AuthorizedMediaDownloadProgress>? progress, CancellationToken token)
    {
        private readonly ProxySession _context = new(source.Location.AbsoluteUri, source.UserAgent, source.Cookie,
            null, source.Referer, DateTime.UtcNow, source.RequestHeaders);
        private readonly Dictionary<string, (string Path, double Duration)> _manifests = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _assets = new(StringComparer.Ordinal);
        private readonly Stopwatch _reportClock = Stopwatch.StartNew();
        public List<string> CreatedFiles { get; } = [];
        public long BytesReceived { get; private set; }
        public string SelectedRendition { get; private set; } = string.Empty;
        public IReadOnlyList<string> AlternateRenditions { get; private set; } = [];

        public async Task<(string Path, double Duration)> LocalizeAsync(Uri uri, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (_manifests.TryGetValue(uri.AbsoluteUri, out var cached)) return cached;
            if (depth > 4 || _manifests.Count + _visiting.Count >= 32 || !_visiting.Add(uri.AbsoluteUri))
                throw new InvalidDataException("The playlist nesting is excessive or cyclic.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            string manifest;
            Uri effective;
            using (var response = await downloader.SendAsync(uri, _context, deadline.Token).ConfigureAwait(false))
            {
                manifest = Encoding.UTF8.GetString(await ReadSmallAsync(response, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
                effective = response.RequestMessage?.RequestUri ?? uri;
            }
            string[] lines = manifest.Replace("\r", "").TrimStart('\ufeff').Split('\n');
            if (lines[0].Trim() != "#EXTM3U") throw new InvalidDataException("The source did not return an HLS playlist.");
            bool master = lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
            if (master)
            {
                lines = SelectRendition(lines, effective, depth == 0 ? source.ValidatedHlsVariant : "", out var selected, out var alternatives);
                if (depth == 0) { SelectedRendition = selected; AlternateRenditions = alternatives; }
            }
            else if (!lines.Any(line => line.Trim() == "#EXT-X-ENDLIST"))
                throw new InvalidOperationException("This is a live or unfinished playlist. Use Stream until the complete video is available.");
            string path = Path.Combine(directory, $"playlist-{_manifests.Count + _visiting.Count:00}.m3u8");
            var output = new StringBuilder();
            double duration = 0;
            int segments = 0;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#EXT-X-DEFINE", StringComparison.Ordinal) || line.Contains("{$", StringComparison.Ordinal))
                    throw new InvalidDataException("Playlist variables are not supported for downloads.");
                if (line.StartsWith("#EXT-X-I-FRAME-STREAM-INF", StringComparison.Ordinal) ||
                    line.StartsWith("#EXT-X-SESSION-DATA", StringComparison.Ordinal) || line.StartsWith("#EXT-X-START", StringComparison.Ordinal)) continue;
                if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    if (!double.TryParse(line[8..].Split(',')[0], CultureInfo.InvariantCulture, out double length) ||
                        !double.IsFinite(length) || length <= 0 || ++segments > 20000)
                        throw new InvalidDataException("The playlist has invalid or excessive segments.");
                    duration += length;
                }
                if (!line.StartsWith('#'))
                {
                    var remote = new Uri(effective, line);
                    string local;
                    if (master)
                    {
                        var child = await LocalizeAsync(remote, depth + 1).ConfigureAwait(false);
                        local = child.Path; duration = Math.Max(duration, child.Duration);
                    }
                    else local = await AssetAsync(remote, false).ConfigureAwait(false);
                    output.AppendLine(Path.GetFileName(local));
                    continue;
                }
                var match = UriAttribute.Match(line);
                if (match.Success)
                {
                    var remote = new Uri(effective, match.Groups["uri"].Value);
                    string local;
                    if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                    {
                        var child = await LocalizeAsync(remote, depth + 1).ConfigureAwait(false);
                        local = child.Path;
                    }
                    else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal)) local = await AssetAsync(remote, false).ConfigureAwait(false);
                    else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) || line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
                    {
                        var attributes = ReadAttributes(line);
                        if (attributes.GetValueOrDefault("METHOD") != "AES-128" ||
                            attributes.GetValueOrDefault("KEYFORMAT", "identity") != "identity")
                            throw new InvalidOperationException("This playlist uses unsupported protected media. Use the provider's playback option.");
                        local = await AssetAsync(remote, true).ConfigureAwait(false);
                    }
                    else throw new InvalidDataException("The playlist has an unsupported media reference.");
                    line = line[..match.Groups["uri"].Index] + Path.GetFileName(local) + line[(match.Groups["uri"].Index + match.Groups["uri"].Length)..];
                }
                output.AppendLine(line);
            }
            if (!double.IsFinite(duration) || duration <= 0 || (!master && segments == 0))
                throw new InvalidDataException("The playlist has no finite video duration.");
            await File.WriteAllTextAsync(path, output.ToString(), new UTF8Encoding(false), token).ConfigureAwait(false);
            CreatedFiles.Add(path);
            _visiting.Remove(uri.AbsoluteUri);
            return _manifests[uri.AbsoluteUri] = (path, duration);
        }

        private static string[] SelectRendition(string[] lines, Uri root, string validated,
            out string selectedUri, out IReadOnlyList<string> alternatives)
        {
            var variants = new List<(string Attributes, string Uri, long Bandwidth)>();
            for (int i = 0; i + 1 < lines.Length; i++)
                if (lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    var attributes = ReadAttributes(lines[i]);
                    long.TryParse(attributes.GetValueOrDefault("BANDWIDTH"), out long bandwidth);
                    variants.Add((lines[i], new Uri(root, lines[i + 1].Trim()).AbsoluteUri, bandwidth));
                }
            if (variants.Count == 0) throw new InvalidDataException("The master playlist has no usable rendition.");
            var selected = validated.Length > 0 ? variants.FirstOrDefault(variant => variant.Uri == validated)
                : variants.OrderByDescending(variant => variant.Bandwidth).First();
            if (selected.Uri == null) throw new InvalidDataException("The checked rendition is no longer advertised by this playlist.");
            var selectedAttributes = ReadAttributes(selected.Attributes);
            selectedUri = selected.Uri;
            alternatives = variants.Where(variant => variant.Uri != selected.Uri &&
                    new[] { "AUDIO", "SUBTITLES", "CLOSED-CAPTIONS", "VIDEO" }.All(group =>
                        ReadAttributes(variant.Attributes).GetValueOrDefault(group) == selectedAttributes.GetValueOrDefault(group)))
                .OrderByDescending(variant => variant.Bandwidth).Select(variant => variant.Uri).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            var output = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) { i++; continue; }
                if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                {
                    var attributes = ReadAttributes(line);
                    string type = attributes.GetValueOrDefault("TYPE", "");
                    if (type is not ("AUDIO" or "SUBTITLES" or "CLOSED-CAPTIONS") ||
                        attributes.GetValueOrDefault("GROUP-ID") != selectedAttributes.GetValueOrDefault(type)) continue;
                }
                output.Add(line);
            }
            output.Add(selected.Attributes); output.Add(selected.Uri);
            return output.ToArray();
        }

        public async Task<string> AssetAsync(Uri uri, bool key, bool requireMp4 = false)
        {
            string cacheKey = uri.AbsoluteUri + (key ? "#key" : "#media");
            if (_assets.TryGetValue(cacheKey, out string? existing)) return existing;
            if (_assets.Count >= 20000) throw new InvalidDataException("The playlist contains too many resources.");
            string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (key) extension = ".key";
            else if (extension is not (".ts" or ".m4s" or ".mp4" or ".aac" or ".vtt")) extension = ".ts";
            string path = Path.Combine(directory, $"asset-{_assets.Count + 1:00000}{extension}");
            CreatedFiles.Add(path);
            for (int attempt = 0; ; attempt++)
            {
                // Header retries have their own bound. Retry only a body failure
                // here, after closing both streams and removing this owned file.
                using var response = await downloader.SendAsync(uri, _context, token).ConfigureAwait(false);
                try
                {
                    await CopyAssetBodyAsync(response, path, key, requireMp4).ConfigureAwait(false);
                    return _assets[cacheKey] = path;
                }
                catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested &&
                    ex is EndOfStreamException or HttpIOException or HttpRequestException or TimeoutException)
                {
                    File.Delete(path);
                    AppLogger.Log($"Playlist body retry {attempt + 1}/2: {Path.GetFileName(path)} ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
                }
            }
        }

        private async Task CopyAssetBodyAsync(HttpResponseMessage response, string path, bool key, bool requireMp4)
        {
            long? expected = response.Content.Headers.ContentLength;
            if (expected > maximumBytes - BytesReceived || key && expected > 16)
                throw new IOException("A playlist resource exceeds the download limit.");
            AuthorizedMediaDownloadService.CheckTemporarySpace(directory, expected ?? 0);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[128 * 1024];
            byte[] mp4Prefix = new byte[8];
            int prefixLength = 0;
            long received = 0;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromMinutes(2));
                int read;
                try { read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("A video segment stopped receiving data. Retry or use Stream."); }
                if (read == 0) break;
                if (requireMp4 && prefixLength < mp4Prefix.Length)
                {
                    int copy = Math.Min(read, mp4Prefix.Length - prefixLength);
                    buffer.AsSpan(0, copy).CopyTo(mp4Prefix.AsSpan(prefixLength));
                    prefixLength += copy;
                    if (prefixLength == mp4Prefix.Length && !IsDashMp4Prefix(mp4Prefix))
                        throw new InvalidDataException("A DASH segment returned a page or other non-MP4 payload.");
                }
                if (received == 0 && !key && !HlsLoopbackProxy.IsSupportedSegmentPayload(
                        response.Content.Headers.ContentType?.MediaType ?? "", buffer.AsSpan(0, read)))
                    throw new InvalidDataException("A playlist segment returned a page or image instead of media.");
                received += read; BytesReceived += read;
                if (BytesReceived > maximumBytes || key && received > 16)
                    throw new IOException("The playlist exceeded the configured download limit.");
                AuthorizedMediaDownloadService.CheckTemporarySpace(directory, 0);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                if (_reportClock.ElapsedMilliseconds >= 200)
                { progress?.Report(new(BytesReceived, null, null)); _reportClock.Restart(); }
            }
            if (received == 0 || expected is { } length && received != length)
                throw new EndOfStreamException($"A playlist resource is empty or incomplete (received {received} bytes; expected {expected?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}).");
            if (key && received != 16 || requireMp4 && prefixLength < mp4Prefix.Length)
                throw new InvalidDataException("A playlist key or MP4 prefix is incomplete.");
        }
    }
}
