using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed record LibraryMediaDownloadResult(string FilePath, IReadOnlyList<string> CaptionPaths, string MetadataPath);
public sealed record LibraryMediaPlayback(AudiovisualPlaybackContext Context, IReadOnlyList<string> CaptionPaths, string AudioNotice);

public sealed partial class AuthorizedMediaDownloadService
{
    /// <summary>Restores this app's saved local work/unit without opening remote references from metadata.</summary>
    public static LibraryMediaPlayback? ReadLibraryPlayback(string mediaPath)
    {
        try
        {
            string fullPath = Path.GetFullPath(mediaPath);
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0) return null;
            string directory = Path.GetDirectoryName(fullPath)!;
            string metadataPath = Path.Combine(directory, "library-item.json");
            if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length is <= 0 or > 65536) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = json.RootElement;
            if (root.GetProperty("SchemaVersion").GetInt32() != 1 ||
                !string.Equals(root.GetProperty("MediaFile").GetString(), Path.GetFileName(fullPath), StringComparison.OrdinalIgnoreCase)) return null;
            var identity = root.GetProperty("Identity").Deserialize<AudiovisualIdentity>();
            var unit = root.GetProperty("Unit").Deserialize<AudiovisualUnit>();
            string workKey = root.GetProperty("WorkKey").GetString() ?? "";
            string title = root.GetProperty("Title").GetString() ?? "";
            if (identity == null || unit == null || !Enum.IsDefined(identity.Kind) ||
                AudiovisualIdentityKeys.HasConflictingIds(identity, identity) || workKey.Length is 0 or > 1024 ||
                workKey.Any(char.IsControl) || title.Length > 1024 ||
                root.GetProperty("UnitKey").GetString() != AudiovisualIdentityKeys.CreateUnitKey(identity.ContentForm, unit)) return null;
            var captionNames = root.GetProperty("CaptionFiles").EnumerateArray().Select(element => element.GetString() ?? "").ToArray();
            if (captionNames.Length > 12 || captionNames.Any(name => name.Length == 0 ||
                    name.IndexOfAny(['/', '\\', ':']) >= 0 || Path.GetFileName(name) != name ||
                    !new[] { ".vtt", ".srt", ".ass", ".ssa" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))) return null;
            string[] captions = captionNames.Select(name => Path.Combine(directory, name)).Where(File.Exists).ToArray();
            var source = new AudiovisualSource { Location = new Uri(fullPath), Identity = identity, Unit = unit,
                ProviderId = root.GetProperty("SourceProvider").GetString() ?? "", AccessMode = AudiovisualSourceAccessMode.DirectMedia };
            var context = new AudiovisualPlaybackContext(workKey, identity, unit, title, "", source);
            string notice = root.GetProperty("AudioNotice").GetString() ?? "Audio language unverified.";
            if (notice.Length > 2048) return null;
            return new(context, Array.AsReadOnly(captions), notice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
            InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return null; }
    }

    public async Task<LibraryMediaDownloadResult?> FindLibraryDownloadAsync(AudiovisualPlaybackContext context,
        string? preferredLanguage = null, CancellationToken token = default)
    {
        string parent = GetLibraryParent(context);
        if (!Directory.Exists(parent)) return null;
        string prefix = context.Unit.IsFeature ? "Feature" : $"S{context.Unit.SeasonNumber:00}E{context.Unit.EpisodeNumber:00}";
        foreach (string revision in Directory.EnumerateDirectories(parent, prefix + "-*")
            .OrderByDescending(Directory.GetLastWriteTimeUtc).Take(50))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string metadataPath = Path.Combine(revision, "library-item.json");
                if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length is <= 0 or > 65536) continue;
                using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, token).ConfigureAwait(false));
                string fileName = metadata.RootElement.GetProperty("MediaFile").GetString() ?? "";
                if (fileName.Length == 0 || fileName.IndexOfAny(['/', '\\', ':']) >= 0) continue;
                string path = Path.Combine(revision, fileName);
                var saved = ReadLibraryPlayback(path);
                if (saved == null || saved.Context.WorkKey != context.WorkKey || saved.Context.UnitKey != context.UnitKey) continue;
                var audio = metadata.RootElement.TryGetProperty("AudioEvidence", out var audioValue)
                    ? audioValue.Deserialize<AudioEvidence>() : null;
                var request = new SourceSearchRequest { Identity = context.Identity, Unit = context.Unit, AudioLanguage = preferredLanguage };
                var evidence = new AudiovisualSourceEvidence { Origin = SourceEvidenceOrigin.ProviderItem,
                    Identity = saved.Context.Identity, Unit = saved.Context.Unit, Audio = audio };
                if (ExactAudiovisualMatcher.VerifyEvidence(request, evidence).Status != SourceVerificationStatus.Verified ||
                    metadata.RootElement.TryGetProperty("MediaBytes", out var size) && size.GetInt64() != new FileInfo(path).Length) continue;
                _ = await _probe(path, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new(path, saved.CaptionPaths, metadataPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
                InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { }
        }
        return null;
    }

    /// <summary>Publishes a checked local video and its captions without transferring ownership of temporary files.</summary>
    public async Task<LibraryMediaDownloadResult> DownloadLibraryAsync(AudiovisualSource source,
        AudiovisualPlaybackContext context, string? preferredLanguage = null,
        IProgress<AuthorizedMediaDownloadProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        var request = new SourceSearchRequest { Identity = context.Identity, Unit = context.Unit, AudioLanguage = preferredLanguage };
        if (ExactAudiovisualMatcher.VerifyEvidence(request, source.Evidence).Status != SourceVerificationStatus.Verified)
            throw new InvalidOperationException("Library downloads require an independently matched film or episode and the requested audio language.");
        if (source.AccessMode != AudiovisualSourceAccessMode.DirectMedia)
            throw new InvalidOperationException("Choose a verified native video source for a library download.");

        string parent = GetLibraryParent(context);
        Directory.CreateDirectory(parent);
        string jobId = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, ".partial-" + jobId);
        string destination = Path.Combine(parent, (context.Unit.IsFeature ? "Feature" : $"S{context.Unit.SeasonNumber:00}E{context.Unit.EpisodeNumber:00}") + "-" + jobId[..8]);
        bool published = false;
        Directory.CreateDirectory(staging);
        try
        {
            var watch = await DownloadTemporaryAsync(source, context, progress, token).ConfigureAwait(false);
            using var lease = watch.Lease;
            long requiredBytes = new FileInfo(watch.FilePath).Length + watch.CaptionPaths.Sum(path => new FileInfo(path).Length);
            CheckTemporarySpace(staging, requiredBytes, _availableSpace);
            string mediaName = "media" + Path.GetExtension(watch.FilePath);
            await CopyLibraryFileAsync(watch.FilePath, Path.Combine(staging, mediaName), token).ConfigureAwait(false);
            var captions = new List<string>();
            foreach (string caption in watch.CaptionPaths)
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileName(caption);
                await CopyLibraryFileAsync(caption, Path.Combine(staging, name), token).ConfigureAwait(false);
                captions.Add(name);
            }
            string metadataName = "library-item.json";
            string metadata = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, context.WorkKey, context.UnitKey, context.Identity, context.Unit,
                context.Title, MediaFile = mediaName, CaptionFiles = captions,
                SourceProvider = source.ProviderId, AudioEvidence = source.Evidence?.Audio,
                MediaBytes = new FileInfo(watch.FilePath).Length, watch.AudioNotice, SavedUtc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(staging, metadataName), metadata, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            published = true;
            return new(Path.Combine(destination, mediaName),
                Array.AsReadOnly(captions.Select(name => Path.Combine(destination, name)).ToArray()),
                Path.Combine(destination, metadataName));
        }
        finally
        {
            // Only this GUID staging folder belongs to this operation. Published
            // revisions and user files are never removed by a failure or a lease.
            if (!published && Path.GetFullPath(staging).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) && Path.GetFileName(staging) == ".partial-" + jobId)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (DirectoryNotFoundException) { }
            }
        }
    }

    private string GetLibraryParent(AudiovisualPlaybackContext context)
    {
        string configured = _config.GetSetting("DownloadDirectory");
        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "Downloads") : configured);
        if (root.Equals(_temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(_temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a permanent download folder outside the temporary playback cache.");
        string category = context.Identity.Kind switch
        { AudiovisualMediaKind.Movie => "Movies", AudiovisualMediaKind.Television => "TV Shows", _ => "Cartoons" };
        string workHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.WorkKey)))[..16].ToLowerInvariant();
        string workTitle = SanitizeFileName(context.Title);
        if (workTitle.Length > 48) workTitle = workTitle[..48];
        string parent = Path.Combine(root, category, workTitle + "-" + workHash);
        return context.Unit.IsFeature ? parent : Path.Combine(parent, $"Season {context.Unit.SeasonNumber:00}");
    }

    private static async Task CopyLibraryFileAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }
}
