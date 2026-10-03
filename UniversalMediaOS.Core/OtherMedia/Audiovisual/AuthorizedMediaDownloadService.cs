using System.Net;
using System.Net.Http.Headers;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed record AuthorizedMediaDownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    double? Percentage)
{
    public string DisplayText => Percentage is { } percentage
        ? $"{percentage:0}%"
        : $"{BytesReceived / 1_048_576d:0.0} MB";
}

/// <summary>
/// Downloads a resolved direct media asset. Provider discovery and authorization
/// are intentionally outside this class; callers must only pass sources that the
/// provider marked as direct and downloadable.
/// </summary>
public sealed partial class AuthorizedMediaDownloadService
{
    private const long DefaultMaximumBytes = 25L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions = new(
        new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ogv" },
        StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly DomainHotSwapper _config;
    private readonly PlaylistMediaDownload _playlistDownload;

    public AuthorizedMediaDownloadService(HttpClient httpClient, DomainHotSwapper config)
        : this(httpClient, config,
            Path.Combine(UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory,
                "UniversalMediaOS", "TemporaryMediaWatch"), ProbeDownloadedMediaAsync)
    {
    }

    internal AuthorizedMediaDownloadService(HttpClient httpClient, DomainHotSwapper config,
        string temporaryRoot, Func<string, CancellationToken, Task<string>> probe,
        HttpClient? playlistClient = null, UniversalMediaOS.Core.Services.IPreparationProcessRunner? processRunner = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _probe = probe;
        _playlistDownload = new PlaylistMediaDownload(playlistClient, processRunner);
        Directory.CreateDirectory(_temporaryRoot);
        CleanupTemporaryOrphans();
    }

    public async Task<string> DownloadAsync(
        Uri source,
        AudiovisualMediaKind kind,
        string title,
        string? suggestedFileName = null,
        IProgress<AuthorizedMediaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await DownloadCoreAsync(
            source,
            kind,
            title,
            suggestedFileName,
            sourceContext: null,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DownloadAsync(
        AudiovisualSource source,
        AudiovisualMediaKind kind,
        string title,
        string? suggestedFileName = null,
        IProgress<AuthorizedMediaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return await DownloadCoreAsync(
            source.Location,
            kind,
            title,
            suggestedFileName,
            source,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadCoreAsync(
        Uri source,
        AudiovisualMediaKind kind,
        string title,
        string? suggestedFileName,
        AudiovisualSource? sourceContext,
        IProgress<AuthorizedMediaDownloadProgress>? progress,
        CancellationToken cancellationToken,
        string? temporaryDirectory = null)
    {
        ValidateRemoteUri(source);
        if (IsStreamingPlaylist(source, sourceContext?.ContentType))
        {
            throw new InvalidOperationException(
                "Streaming playlists must be played directly and cannot be saved as a single media file.");
        }

        await ValidateResolvedHostAsync(source, cancellationToken).ConfigureAwait(false);
        string downloadRoot = _config.GetSetting("DownloadDirectory");
        if (string.IsNullOrWhiteSpace(downloadRoot))
        {
            downloadRoot = Path.Combine(
                UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory,
                "UniversalMediaOS",
                "Downloads");
        }

        string kindDirectory = kind switch
        {
            AudiovisualMediaKind.Movie => "Movies",
            AudiovisualMediaKind.Television => "TV Shows",
            AudiovisualMediaKind.Cartoon => "Cartoons",
            _ => "Other Media"
        };
        string destinationDirectory = temporaryDirectory ?? Path.Combine(downloadRoot, kindDirectory);
        Directory.CreateDirectory(destinationDirectory);

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        AudiovisualRequestHeaderReplay.Apply(request, sourceContext);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        Uri finalLocation = response.RequestMessage?.RequestUri ?? source;
        if (!finalLocation.Equals(source))
        {
            ValidateRemoteUri(finalLocation);
            await ValidateResolvedHostAsync(finalLocation, cancellationToken).ConfigureAwait(false);
        }
        response.EnsureSuccessStatusCode();
        if (IsStreamingPlaylist(finalLocation, response.Content.Headers.ContentType?.MediaType))
        {
            throw new InvalidOperationException(
                "Streaming playlists must be played directly and cannot be saved as a single media file.");
        }

        if (temporaryDirectory != null &&
            response.Content.Headers.ContentType?.MediaType is { } type &&
            (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("application/json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The source returned a page instead of a video file.");

        long? declaredLength = response.Content.Headers.ContentLength;
        long maximumBytes = GetMaximumBytes();
        if (declaredLength is { } length && length > maximumBytes)
        {
            throw new InvalidOperationException("The media file is larger than the configured download limit.");
        }
        if (temporaryDirectory != null) CheckTemporarySpace(destinationDirectory, declaredLength ?? 0);

        string extension = ResolveExtension(source, suggestedFileName, response.Content.Headers.ContentType);
        string baseName = SanitizeFileName(string.IsNullOrWhiteSpace(title) ? "media" : title);
        string destinationPath = GetUniquePath(destinationDirectory, baseName, extension);
        string partialPath = destinationPath + ".partial";

        try
        {
            long totalReceived = 0;
            await using (Stream input = await response.Content
                             .ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (FileStream output = new(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[128 * 1024];
                long nextSpaceCheck = 64L * 1024 * 1024;
                while (true)
                {
                    int read;
                    if (temporaryDirectory != null)
                    {
                        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        readDeadline.CancelAfter(TimeSpan.FromMinutes(2));
                        try { read = await input.ReadAsync(buffer, readDeadline.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        { throw new TimeoutException("The video download stopped receiving data. Retry or use Stream."); }
                    }
                    else read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    totalReceived += read;
                    if (totalReceived > maximumBytes)
                    {
                        throw new InvalidOperationException("The media file exceeded the configured download limit.");
                    }
                    if (temporaryDirectory != null && totalReceived >= nextSpaceCheck)
                    {
                        CheckTemporarySpace(destinationDirectory, 0);
                        nextSpaceCheck = totalReceived + 64L * 1024 * 1024;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    double? percentage = declaredLength is > 0
                        ? Math.Clamp(totalReceived * 100d / declaredLength.Value, 0, 100)
                        : null;
                    progress?.Report(new AuthorizedMediaDownloadProgress(totalReceived, declaredLength, percentage));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (temporaryDirectory != null &&
                (totalReceived == 0 || declaredLength is { } expected && totalReceived != expected))
                throw new InvalidDataException("The video download was empty or incomplete. Retry or use Stream.");

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, destinationPath);
            progress?.Report(new AuthorizedMediaDownloadProgress(totalReceived, declaredLength ?? totalReceived, 100));
            return destinationPath;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    public static void ValidateRemoteUri(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri ||
            (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute HTTP or HTTPS media URLs can be downloaded.", nameof(source));
        }

        if (source.IsLoopback ||
            source.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(source.Host, out IPAddress? address) &&
            !AudiovisualNetworkBoundary.IsPublicAddress(address))
        {
            throw new ArgumentException("Local and private-network media URLs are not accepted.", nameof(source));
        }
    }

    internal static async Task ValidateResolvedHostAsync(
        Uri source,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(source.Host, out IPAddress? literalAddress))
        {
            if (!AudiovisualNetworkBoundary.IsPublicAddress(literalAddress))
            {
                throw new ArgumentException("Local and private-network media URLs are not accepted.", nameof(source));
            }

            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(
                source.DnsSafeHost,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HttpRequestException("The media host could not be resolved.", ex);
        }

        if (addresses.Length == 0 ||
            addresses.Any(address => !AudiovisualNetworkBoundary.IsPublicAddress(address)))
        {
            throw new ArgumentException("The media host resolved to a local or private network.", nameof(source));
        }
    }

    private long GetMaximumBytes()
    {
        return long.TryParse(_config.GetSetting("OtherMediaMaximumDownloadBytes"), out long configured) &&
               configured >= 1_048_576
            ? configured
            : DefaultMaximumBytes;
    }

    private static string ResolveExtension(
        Uri source,
        string? suggestedFileName,
        MediaTypeHeaderValue? contentType)
    {
        string extension = Path.GetExtension(suggestedFileName ?? string.Empty) ?? string.Empty;
        if (!SupportedExtensions.Contains(extension))
        {
            extension = Path.GetExtension(source.AbsolutePath) ?? string.Empty;
        }

        if (SupportedExtensions.Contains(extension))
        {
            return extension.ToLowerInvariant();
        }

        string? mediaType = contentType?.MediaType;
        return mediaType?.ToLowerInvariant() switch
        {
            "video/webm" => ".webm",
            "video/ogg" => ".ogv",
            "video/x-matroska" => ".mkv",
            "video/quicktime" => ".mov",
            "video/x-msvideo" => ".avi",
            "video/mp4" => ".mp4",
            _ => throw new InvalidDataException(
                "The direct source did not declare a supported downloadable video format.")
        };
    }

    private static bool IsStreamingPlaylist(Uri source, string? contentType)
    {
        string extension = Path.GetExtension(source.AbsolutePath);
        return extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mpd", StringComparison.OrdinalIgnoreCase) ||
               (contentType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (contentType?.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value
            .Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        safe = safe.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(safe)
            ? "media"
            : safe.Length > 120 ? safe[..120].TrimEnd() : safe;
    }

    private static string GetUniquePath(string directory, string baseName, string extension)
    {
        string candidate = Path.Combine(directory, baseName + extension);
        for (int suffix = 2; File.Exists(candidate) || File.Exists(candidate + ".partial"); suffix++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({suffix}){extension}");
        }

        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale .partial is safer than deleting any unrelated user file.
        }
    }
}
