using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UniversalMediaOS.Core.OtherMedia.Books;

public sealed class BookReaderService
{
    private const int MaxArchiveEntries = 10_000;
    private const long MaxSingleEntryBytes = 64L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const long MaxRemoteBookBytes = 256L * 1024 * 1024;
    private const int MaxXmlBytes = 2 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ExtractionLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;

    public BookReaderService(HttpClient httpClient, string? cacheRoot = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(
            UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory,
            "UniversalMediaOS",
            "book_cache"));
    }

    public async Task<BookReaderDocument> PrepareAsync(
        BookAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(asset.Id) ||
            string.IsNullOrWhiteSpace(asset.Location))
        {
            throw new ArgumentException("The book asset is incomplete.", nameof(asset));
        }

        if (asset.Format == BookFileFormat.Pdf)
        {
            string pdfPath = await ResolvePdfPathAsync(asset, cancellationToken)
                .ConfigureAwait(false);
            string location = new Uri(pdfPath).AbsoluteUri;
            return new BookReaderDocument
            {
                AssetId = asset.Id,
                Format = BookFileFormat.Pdf,
                StartLocation = location,
                WorkingDirectory = Path.GetDirectoryName(pdfPath) ?? string.Empty
            };
        }

        if (asset.Format != BookFileFormat.Epub)
        {
            throw new NotSupportedException("Only EPUB and PDF reader assets are supported.");
        }

        string epubPath = await ResolveEpubPathAsync(asset, cancellationToken)
            .ConfigureAwait(false);
        string sourceVersion = asset.IsLocal
            ? GetLocalFileVersion(epubPath)
            : string.Empty;
        string extractionKey = BuildCacheKey(
            $"{asset.Id}|{asset.Location}|{sourceVersion}");
        string extractionRoot = Path.Combine(_cacheRoot, "epub", extractionKey);
        SemaphoreSlim gate = ExtractionLocks.GetOrAdd(
            extractionRoot,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BookReaderDocument? cached = TryReadExtractedBook(asset.Id, extractionRoot);
            if (cached != null)
            {
                return cached;
            }

            ResetCacheDirectory(extractionRoot);
            try
            {
                await ExtractArchiveAsync(epubPath, extractionRoot, cancellationToken)
                    .ConfigureAwait(false);
                BookReaderDocument document = ReadExtractedBook(asset.Id, extractionRoot);
                await File.WriteAllTextAsync(
                        Path.Combine(extractionRoot, ".ready"),
                        asset.Id,
                        Encoding.UTF8,
                        cancellationToken)
                    .ConfigureAwait(false);
                return document;
            }
            catch
            {
                TryDeleteCacheDirectory(extractionRoot);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<int> CleanStaleCacheAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default)
    {
        if (olderThan < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(olderThan));
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(_cacheRoot))
            {
                return 0;
            }

            DateTime cutoffUtc = DateTime.UtcNow - olderThan;
            int removed = 0;
            foreach (string directory in Directory.EnumerateDirectories(
                         _cacheRoot,
                         "*",
                         SearchOption.AllDirectories)
                     .ToArray()
                     .OrderByDescending(path => path.Length))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.GetLastWriteTimeUtc(directory) >= cutoffUtc)
                {
                    continue;
                }

                try
                {
                    TryDeleteCacheDirectory(directory);
                    if (!Directory.Exists(directory))
                    {
                        removed++;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return removed;
        }, cancellationToken);
    }

    private async Task<string> ResolvePdfPathAsync(
        BookAsset asset,
        CancellationToken cancellationToken)
    {
        if (asset.IsLocal)
        {
            string path = Path.GetFullPath(asset.Location);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The local PDF was not found.", path);
            }

            return path;
        }

        EnsureAuthorizedRemoteAsset(asset);
        return await DownloadRemoteAssetAsync(
                asset,
                ".pdf",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> ResolveEpubPathAsync(
        BookAsset asset,
        CancellationToken cancellationToken)
    {
        if (asset.IsLocal)
        {
            string path = Path.GetFullPath(asset.Location);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The local EPUB was not found.", path);
            }

            return path;
        }

        EnsureAuthorizedRemoteAsset(asset);
        return await DownloadRemoteAssetAsync(
                asset,
                ".epub",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> DownloadRemoteAssetAsync(
        BookAsset asset,
        string extension,
        CancellationToken cancellationToken)
    {
        Uri remoteUri = GetSafeRemoteUri(asset.Location);
        if (asset.SizeBytes is > MaxRemoteBookBytes)
        {
            throw new InvalidDataException("The remote book exceeds the size limit.");
        }

        string archiveDirectory = Path.Combine(_cacheRoot, "downloads");
        Directory.CreateDirectory(archiveDirectory);
        string target = Path.Combine(
            archiveDirectory,
            BuildCacheKey(asset.Id + "|" + remoteUri.AbsoluteUri) + extension);
        if (File.Exists(target) &&
            new FileInfo(target).Length is > 0 and <= MaxRemoteBookBytes)
        {
            return target;
        }

        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, remoteUri);
            request.Headers.UserAgent.ParseAdd("UniversalMediaOS/1.0 (open-access book client)");
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Uri finalUri = response.RequestMessage?.RequestUri ?? remoteUri;
            if (finalUri.Scheme is not ("http" or "https"))
            {
                throw new InvalidDataException("The remote book redirected to an unsafe URI.");
            }

            if (response.Content.Headers.ContentLength is > MaxRemoteBookBytes)
            {
                throw new InvalidDataException("The remote EPUB exceeds the size limit.");
            }

            await using Stream input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await CopyBoundedAsync(
                    input,
                    output,
                    MaxRemoteBookBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void EnsureAuthorizedRemoteAsset(BookAsset asset)
    {
        if (!asset.IsDownloadAllowed ||
            asset.Access is not (BookAccessKind.PublicDomain or BookAccessKind.OpenLicense))
        {
            throw new InvalidOperationException(
                "Remote reading is limited to assets explicitly marked public domain or openly licensed.");
        }
    }

    private static Uri GetSafeRemoteUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidDataException("The remote book URI is invalid.");
        }

        return uri;
    }

    private static async Task ExtractArchiveAsync(
        string archivePath,
        string extractionRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(extractionRoot);
        string rootWithSeparator = EnsureDirectorySeparator(Path.GetFullPath(extractionRoot));
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException("The EPUB contains too many files.");
        }

        long declaredTotal = 0;
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException("Symbolic links are not allowed in EPUB files.");
            }

            if (entry.Length > MaxSingleEntryBytes)
            {
                throw new InvalidDataException("An EPUB entry exceeds the size limit.");
            }

            declaredTotal = checked(declaredTotal + entry.Length);
            if (declaredTotal > MaxExpandedBytes)
            {
                throw new InvalidDataException("The expanded EPUB exceeds the size limit.");
            }

            string target = ResolveExtractionTarget(rootWithSeparator, entry.FullName);
            if (!destinations.Add(target))
            {
                throw new InvalidDataException("The EPUB contains duplicate output paths.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await using Stream input = entry.Open();
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await CopyBoundedAsync(
                    input,
                    output,
                    MaxSingleEntryBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static BookReaderDocument? TryReadExtractedBook(
        string assetId,
        string extractionRoot)
    {
        if (!File.Exists(Path.Combine(extractionRoot, ".ready")))
        {
            return null;
        }

        try
        {
            return ReadExtractedBook(assetId, extractionRoot);
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static BookReaderDocument ReadExtractedBook(
        string assetId,
        string extractionRoot)
    {
        string root = Path.GetFullPath(extractionRoot);
        string containerPath = Path.Combine(root, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
        {
            throw new InvalidDataException("The EPUB container document is missing.");
        }

        XDocument container = LoadBoundedXml(containerPath);
        string packageRelativePath = container.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("rootfile", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("full-path")
            ?.Value
            ?.Trim() ?? string.Empty;
        string packagePath = ResolveContentPath(root, root, packageRelativePath);
        if (!File.Exists(packagePath))
        {
            throw new InvalidDataException("The EPUB package document is missing.");
        }

        XDocument package = LoadBoundedXml(packagePath);
        string packageDirectory = Path.GetDirectoryName(packagePath) ?? root;
        var manifest = package.Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
            .Select(element => new
            {
                Id = element.Attribute("id")?.Value?.Trim() ?? string.Empty,
                Href = element.Attribute("href")?.Value?.Trim() ?? string.Empty,
                MediaType = element.Attribute("media-type")?.Value?.Trim() ?? string.Empty
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var ordered = new List<(string Id, string Href, string MediaType)>();
        foreach (XElement itemReference in package.Descendants().Where(element =>
                     element.Name.LocalName.Equals("itemref", StringComparison.OrdinalIgnoreCase)))
        {
            string id = itemReference.Attribute("idref")?.Value?.Trim() ?? string.Empty;
            if (manifest.TryGetValue(id, out var item))
            {
                ordered.Add((item.Id, item.Href, item.MediaType));
            }
        }

        if (ordered.Count == 0)
        {
            ordered.AddRange(manifest.Values
                .Where(item => IsHtmlMediaType(item.MediaType, item.Href))
                .Select(item => (item.Id, item.Href, item.MediaType)));
        }

        var chapters = new List<BookReaderChapter>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, string href, string mediaType) in ordered)
        {
            if (!IsHtmlMediaType(mediaType, href))
            {
                continue;
            }

            string path = ResolveContentPath(root, packageDirectory, href);
            if (!File.Exists(path) || !seenPaths.Add(path))
            {
                continue;
            }

            chapters.Add(new BookReaderChapter
            {
                Index = chapters.Count,
                Title = BuildChapterTitle(id, path, chapters.Count),
                Location = new Uri(path).AbsoluteUri
            });
        }

        if (chapters.Count == 0)
        {
            throw new InvalidDataException("The EPUB contains no readable HTML chapters.");
        }

        return new BookReaderDocument
        {
            AssetId = assetId,
            Format = BookFileFormat.Epub,
            StartLocation = chapters[0].Location,
            Chapters = chapters,
            WorkingDirectory = root
        };
    }

    private static XDocument LoadBoundedXml(string path)
    {
        var file = new FileInfo(path);
        if (file.Length > MaxXmlBytes)
        {
            throw new InvalidDataException("An EPUB XML document exceeds the size limit.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlBytes,
            MaxCharactersFromEntities = 0
        };
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using XmlReader reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string ResolveContentPath(
        string extractionRoot,
        string baseDirectory,
        string relativePath)
    {
        string href = (relativePath ?? string.Empty).Split('#', '?')[0].Trim();
        try
        {
            href = Uri.UnescapeDataString(href);
        }
        catch (UriFormatException ex)
        {
            throw new InvalidDataException("The EPUB contains an invalid content path.", ex);
        }

        if (string.IsNullOrWhiteSpace(href) ||
            Path.IsPathRooted(href) ||
            href.Contains('\0'))
        {
            throw new InvalidDataException("The EPUB contains an unsafe content path.");
        }

        string rootWithSeparator = EnsureDirectorySeparator(Path.GetFullPath(extractionRoot));
        string fullPath = Path.GetFullPath(Path.Combine(
            baseDirectory,
            href.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The EPUB content path escapes the extracted book.");
        }

        return fullPath;
    }

    private static string ResolveExtractionTarget(
        string rootWithSeparator,
        string archiveEntryName)
    {
        string entryName = (archiveEntryName ?? string.Empty).Replace(
            '/',
            Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(entryName) ||
            Path.IsPathRooted(entryName) ||
            entryName.Contains('\0'))
        {
            throw new InvalidDataException("The EPUB contains an unsafe archive path.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(rootWithSeparator, entryName));
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The EPUB archive path escapes the cache directory.");
        }

        return fullPath;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        int unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        return (unixMode & UnixFileTypeMask) == UnixSymbolicLink;
    }

    private static bool IsHtmlMediaType(string mediaType, string href)
    {
        string extension = Path.GetExtension(href);
        return mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".htm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xht", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildChapterTitle(string id, string path, int index)
    {
        string candidate = string.IsNullOrWhiteSpace(id)
            ? Path.GetFileNameWithoutExtension(path)
            : id;
        candidate = candidate.Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(candidate)
            ? $"Chapter {index + 1}"
            : candidate;
    }

    private static async Task CopyBoundedAsync(
        Stream input,
        Stream output,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await input
                .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maxBytes)
            {
                throw new InvalidDataException("The book data exceeds the size limit.");
            }

            await output
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string BuildCacheKey(string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..32].ToLowerInvariant();
    }

    private static string GetLocalFileVersion(string path)
    {
        var file = new FileInfo(path);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{file.Length}|{file.LastWriteTimeUtc.Ticks}");
    }

    private void ResetCacheDirectory(string path)
    {
        TryDeleteCacheDirectory(path);
        Directory.CreateDirectory(path);
    }

    private void TryDeleteCacheDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        string rootWithSeparator = EnsureDirectorySeparator(_cacheRoot);
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the book cache.");
        }

        Directory.Delete(fullPath, recursive: true);
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string EnsureDirectorySeparator(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }
}
