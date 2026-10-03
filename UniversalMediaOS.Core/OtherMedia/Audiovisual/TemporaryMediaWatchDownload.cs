using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed record TemporaryMediaWatchResult(string FilePath, IDisposable Lease, string AudioNotice)
{
    public IReadOnlyList<string> CaptionPaths { get; init; } = [];
}

public sealed partial class AuthorizedMediaDownloadService
{
    private readonly string _temporaryRoot;
    private readonly Func<string, CancellationToken, Task<string>> _probe;
    private readonly SemaphoreSlim _temporaryTransferGate = new(1, 1);
    private readonly object _temporarySync = new();
    private readonly Dictionary<string, TemporaryMediaEntry> _temporaryFiles = new(StringComparer.Ordinal);
    private const long TemporaryReserveBytes = 1024L * 1024 * 1024;

    /// <summary>Downloads native media for playback; the last player lease deletes it.</summary>
    public async Task<TemporaryMediaWatchResult> DownloadTemporaryAsync(
        AudiovisualSource source, AudiovisualPlaybackContext context,
        IProgress<AuthorizedMediaDownloadProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        if (source.AccessMode != AudiovisualSourceAccessMode.DirectMedia)
            throw new InvalidOperationException("Choose a native media source for Watch via download.");

        // Keep remakes, episodes and source versions separate without retaining credentials in cache keys.
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            context.WorkKey, context.UnitKey, context.Unit, context.Identity.Kind, source.ProviderId,
            Location = source.Location.AbsoluteUri, source.UserAgent, source.Cookie, source.Referer,
            Headers = source.RequestHeaders.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray(),
            source.ValidatedHlsVariant, source.Subtitles
        }))));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        try { await _temporaryTransferGate.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Watch via download exceeded two hours. Retry or use Stream."); }
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            lock (_temporarySync)
            {
                if (_temporaryFiles.TryGetValue(key, out var cached))
                {
                    if (File.Exists(cached.Path))
                    {
                        cached.Owners++;
                        return CreateTemporaryResult(key, cached);
                    }
                    _temporaryFiles.Remove(key);
                    cached.Lock.Dispose();
                    DeleteTemporaryDirectory(cached.Directory);
                }
            }

            string directory = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            FileStream? lockHandle = null;
            bool handedOff = false;
            try
            {
                lockHandle = new FileStream(Path.Combine(directory, ".active"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                string title = context.Unit.IsFeature ? "feature"
                    : $"S{context.Unit.SeasonNumber:00}E{context.Unit.EpisodeNumber:00}";
                string path = IsStreamingPlaylist(source.Location, source.ContentType)
                    ? await _playlistDownload.DownloadAsync(source, directory, GetMaximumBytes(), progress, deadline.Token)
                        .ConfigureAwait(false)
                    : await DownloadCoreAsync(source.Location, context.Identity.Kind, title,
                        Path.GetFileName(source.Location.AbsolutePath), source, progress, deadline.Token, directory)
                        .ConfigureAwait(false);
                string[] captions = await _playlistDownload.DownloadCaptionsAsync(source, directory, deadline.Token)
                    .ConfigureAwait(false);
                string audioNotice = await _probe(path, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                var entry = new TemporaryMediaEntry(directory, path, captions, audioNotice, lockHandle);
                lock (_temporarySync) _temporaryFiles.Add(key, entry);
                lockHandle = null;
                handedOff = true;
                return CreateTemporaryResult(key, entry);
            }
            finally
            {
                if (!handedOff)
                {
                    lockHandle?.Dispose();
                    DeleteTemporaryDirectory(directory);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Watch via download exceeded two hours. Retry or use Stream.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("The video source did not respond in time. Retry or use Stream."); }
        finally { _temporaryTransferGate.Release(); }
    }

    private TemporaryMediaWatchResult CreateTemporaryResult(string key, TemporaryMediaEntry entry) =>
        new(entry.Path, new TemporaryEpisodeLease(() => ReleaseTemporaryFile(key, entry)), entry.AudioNotice)
        { CaptionPaths = Array.AsReadOnly(entry.Captions) };

    private void ReleaseTemporaryFile(string key, TemporaryMediaEntry entry)
    {
        lock (_temporarySync)
        {
            if (--entry.Owners > 0) return;
            if (_temporaryFiles.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                _temporaryFiles.Remove(key);
            entry.Lock.Dispose();
        }
        DeleteTemporaryDirectory(entry.Directory);
    }

    private void CleanupTemporaryOrphans()
    {
        if ((File.GetAttributes(_temporaryRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Temporary playback storage cannot be a directory link.");
        foreach (string directory in Directory.EnumerateDirectories(_temporaryRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                using (var handle = new FileStream(Path.Combine(directory, ".active"), FileMode.OpenOrCreate,
                           FileAccess.ReadWrite, FileShare.None)) { }
                DeleteTemporaryDirectory(directory);
            }
            catch (IOException) { /* Another process still owns this transfer/player. */ }
            catch (UnauthorizedAccessException) { /* Leave inaccessible data for a later cleanup. */ }
        }
    }

    private void DeleteTemporaryDirectory(string directory)
    {
        try
        {
            string full = Path.GetFullPath(directory);
            if (!string.Equals(Path.GetDirectoryName(full), _temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(full), "N", out _) ||
                !Directory.Exists(full) ||
                (File.GetAttributes(_temporaryRoot) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
            Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLogger.Log($"Temporary video cleanup will retry on restart: {ex.Message}", "WARNING"); }
    }

    internal static void CheckTemporarySpace(string directory, long remainingBytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(directory)!);
        if (drive.AvailableFreeSpace - remainingBytes < TemporaryReserveBytes)
            throw new IOException("Not enough free space for temporary playback. Free space or use Stream.");
    }

    internal static async Task<string> ProbeDownloadedMediaAsync(string path, CancellationToken token)
    {
        string managed = Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "Services", "ffprobe.exe");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        PreparationProcessResult result;
        try
        {
            result = await new PreparationProcessRunner().RunAsync(File.Exists(managed) ? managed : "ffprobe",
                ["-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height:stream_tags=language",
                    "-of", "json", path], deadline.Token).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new InvalidOperationException("Repair FFmpeg services before using Watch via download.", ex); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("The downloaded video could not be checked within 30 seconds."); }
        if (result.ExitCode != 0) throw new InvalidDataException("The downloaded file is not readable video.");
        using var json = JsonDocument.Parse(result.Output);
        if (!json.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The downloaded file has no readable media tracks.");
        var tracks = streams.EnumerateArray().ToArray();
        bool HasCodec(JsonElement track) => track.TryGetProperty("codec_name", out var codec) &&
            codec.GetString() is { Length: > 0 } name && name != "unknown";
        bool IsType(JsonElement track, string type) => track.TryGetProperty("codec_type", out var value) && value.GetString() == type;
        if (!tracks.Any(track => IsType(track, "video") && HasCodec(track) &&
                track.TryGetProperty("width", out var width) && width.GetInt32() > 0 &&
                track.TryGetProperty("height", out var height) && height.GetInt32() > 0) ||
            !tracks.Any(track => IsType(track, "audio") && HasCodec(track)))
            throw new InvalidDataException("The downloaded file must contain readable video and audio tracks.");
        string[] languages = tracks.Where(track => IsType(track, "audio"))
            .Select(track => track.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var language)
                ? language.GetString() : null)
            .Where(language => !string.IsNullOrWhiteSpace(language) && language != "und")
            .Select(language => language!.ToUpperInvariant()).Distinct().ToArray();
        return languages.Length == 0 ? "Audio language unverified; check Audio in the player."
            : $"Audio tracks tagged {string.Join(", ", languages)}; check Audio in the player.";
    }

    private sealed class TemporaryMediaEntry(string directory, string path, string[] captions, string audioNotice, FileStream activeLock)
    {
        public string Directory { get; } = directory;
        public string Path { get; } = path;
        public string[] Captions { get; } = captions;
        public string AudioNotice { get; } = audioNotice;
        public FileStream Lock { get; } = activeLock;
        public int Owners { get; set; } = 1;
    }
}
