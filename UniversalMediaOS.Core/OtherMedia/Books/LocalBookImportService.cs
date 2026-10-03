using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace UniversalMediaOS.Core.OtherMedia.Books;

public sealed partial class LocalBookImportService
{
    private const int MaxMetadataBytes = 2 * 1024 * 1024;

    public Task<LocalBookImportResult> ImportAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        string fullPath = ValidatePath(filePath);
        return Task.Run(() => ImportCore(fullPath, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<LocalBookImportResult>> ImportManyAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var results = new List<LocalBookImportResult>();
        foreach (string path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ImportAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static LocalBookImportResult ImportCore(
        string fullPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(fullPath);
        return extension.Equals(".epub", StringComparison.OrdinalIgnoreCase)
            ? ImportEpub(fullPath, cancellationToken)
            : ImportPdf(fullPath, cancellationToken);
    }

    private static LocalBookImportResult ImportEpub(
        string fullPath,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(fullPath);
        cancellationToken.ThrowIfCancellationRequested();

        ZipArchiveEntry containerEntry = FindEntry(
            archive,
            "META-INF/container.xml")
            ?? throw new InvalidDataException("The EPUB container.xml file is missing.");
        XDocument container = ParseXml(ReadEntryText(containerEntry));
        string packagePath = container
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("rootfile", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("full-path")
            ?.Value
            ?.Trim() ?? string.Empty;
        packagePath = NormalizeArchivePath(string.Empty, packagePath);
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new InvalidDataException("The EPUB package path is invalid.");
        }

        ZipArchiveEntry packageEntry = FindEntry(archive, packagePath)
            ?? throw new InvalidDataException("The EPUB package document is missing.");
        XDocument package = ParseXml(ReadEntryText(packageEntry));
        cancellationToken.ThrowIfCancellationRequested();

        string title = FindMetadataValues(package, "title").FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Path.GetFileNameWithoutExtension(fullPath);
        }

        IReadOnlyList<string> authors = FindMetadataValues(package, "creator");
        string publisher = FindMetadataValues(package, "publisher").FirstOrDefault() ?? string.Empty;
        string language = FindMetadataValues(package, "language").FirstOrDefault() ?? string.Empty;
        string publishedDate = FindMetadataValues(package, "date").FirstOrDefault() ?? string.Empty;
        string description = FindMetadataValues(package, "description").FirstOrDefault() ?? string.Empty;
        IReadOnlyList<string> rawIdentifiers = FindMetadataValues(package, "identifier");
        IReadOnlyList<BookIdentifier> identifiers = ParseIdentifiers(rawIdentifiers);
        string bookId = BuildLocalBookId(fullPath);
        var asset = new BookAsset
        {
            Id = $"{bookId}:epub",
            BookId = bookId,
            DisplayName = Path.GetFileName(fullPath),
            Format = BookFileFormat.Epub,
            Access = BookAccessKind.Local,
            Location = fullPath,
            SourceLabel = "Local library",
            RightsStatement = "User-provided local file",
            SizeBytes = new FileInfo(fullPath).Length,
            IsDownloadAllowed = false
        };
        var book = new BookRecord
        {
            Id = bookId,
            Source = BookCatalogSource.Local,
            Title = title.Trim(),
            Authors = authors,
            Description = StripMarkup(description),
            Publisher = publisher,
            PublishedDate = publishedDate,
            PublishedYear = ParseYear(publishedDate),
            Language = language,
            Identifiers = identifiers,
            Assets = [asset],
            InfoUrl = new Uri(fullPath).AbsoluteUri
        };

        return new LocalBookImportResult { Book = book, Asset = asset };
    }

    private static LocalBookImportResult ImportPdf(
        string fullPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] metadataBytes = ReadPdfMetadataWindow(fullPath);
        string metadata = Encoding.Latin1.GetString(metadataBytes);
        string title = ReadPdfInfoValue(metadata, "Title");
        string author = ReadPdfInfoValue(metadata, "Author");
        string subject = ReadPdfInfoValue(metadata, "Subject");
        string creationDate = ReadPdfInfoValue(metadata, "CreationDate");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Path.GetFileNameWithoutExtension(fullPath);
        }

        string bookId = BuildLocalBookId(fullPath);
        var asset = new BookAsset
        {
            Id = $"{bookId}:pdf",
            BookId = bookId,
            DisplayName = Path.GetFileName(fullPath),
            Format = BookFileFormat.Pdf,
            Access = BookAccessKind.Local,
            Location = fullPath,
            SourceLabel = "Local library",
            RightsStatement = "User-provided local file",
            SizeBytes = new FileInfo(fullPath).Length,
            IsDownloadAllowed = false
        };
        var book = new BookRecord
        {
            Id = bookId,
            Source = BookCatalogSource.Local,
            Title = title.Trim(),
            Authors = string.IsNullOrWhiteSpace(author) ? Array.Empty<string>() : [author.Trim()],
            Description = subject,
            PublishedDate = creationDate,
            PublishedYear = ParsePdfYear(creationDate),
            Assets = [asset],
            InfoUrl = new Uri(fullPath).AbsoluteUri
        };

        return new LocalBookImportResult { Book = book, Asset = asset };
    }

    private static string ValidatePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A local book path is required.", nameof(filePath));
        }

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The local book file was not found.", fullPath);
        }

        string extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".epub", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only EPUB and PDF books can be imported.");
        }

        return fullPath;
    }

    private static XDocument ParseXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxMetadataBytes,
            MaxCharactersFromEntities = 0
        };
        using var stringReader = new StringReader(xml);
        using XmlReader reader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxMetadataBytes)
        {
            throw new InvalidDataException("The EPUB metadata document is too large.");
        }

        using Stream stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        char[] buffer = new char[MaxMetadataBytes + 1];
        int count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > MaxMetadataBytes)
        {
            throw new InvalidDataException("The EPUB metadata document is too large.");
        }

        return new string(buffer, 0, count);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string fullName)
    {
        string normalized = fullName.Replace('\\', '/');
        return archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Replace('\\', '/')
                .Equals(normalized, StringComparison.Ordinal));
    }

    private static string NormalizeArchivePath(string baseDirectory, string relativePath)
    {
        string raw = (relativePath ?? string.Empty)
            .Split('#', '?')[0]
            .Replace('\\', '/')
            .Trim();
        if (string.IsNullOrWhiteSpace(raw) ||
            raw.StartsWith("/", StringComparison.Ordinal) ||
            raw.Contains(':'))
        {
            return string.Empty;
        }

        var segments = new List<string>();
        string combined = string.IsNullOrWhiteSpace(baseDirectory)
            ? raw
            : $"{baseDirectory.TrimEnd('/')}/{raw}";
        foreach (string segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return string.Empty;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join("/", segments);
    }

    private static IReadOnlyList<string> FindMetadataValues(
        XDocument package,
        string localName)
    {
        return package.Descendants()
            .Where(element =>
                element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<BookIdentifier> ParseIdentifiers(
        IEnumerable<string> identifiers)
    {
        var output = new List<BookIdentifier>();
        foreach (string identifier in identifiers)
        {
            string normalized = new string(identifier
                .Where(char.IsAsciiLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
            string scheme = normalized.Length switch
            {
                13 when normalized.All(char.IsDigit) => "ISBN-13",
                10 => "ISBN-10",
                _ => "EPUB"
            };
            output.Add(new BookIdentifier
            {
                Scheme = scheme,
                Value = scheme == "EPUB" ? identifier : normalized
            });
        }

        return output
            .DistinctBy(
                identifier => $"{identifier.Scheme}:{identifier.Value}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildLocalBookId(string fullPath)
    {
        var file = new FileInfo(Path.GetFullPath(fullPath));
        string versionedIdentity = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(versionedIdentity));
        return "local:" + Convert.ToHexString(digest)[..24].ToLowerInvariant();
    }

    private static int? ParseYear(string value)
    {
        Match match = FourDigitYearRegex().Match(value ?? string.Empty);
        return match.Success && int.TryParse(match.Value, out int year) ? year : null;
    }

    private static int? ParsePdfYear(string value)
    {
        string normalized = value.StartsWith("D:", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;
        return ParseYear(normalized);
    }

    private static byte[] ReadPdfMetadataWindow(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        int windowSize = Math.Min(MaxMetadataBytes, checked((int)Math.Min(stream.Length, int.MaxValue)));
        byte[] first = new byte[windowSize];
        int firstCount = ReadFully(stream, first);
        if (stream.Length <= MaxMetadataBytes)
        {
            return first[..firstCount];
        }

        stream.Seek(-MaxMetadataBytes, SeekOrigin.End);
        byte[] last = new byte[MaxMetadataBytes];
        int lastCount = ReadFully(stream, last);
        byte[] combined = new byte[firstCount + lastCount];
        Buffer.BlockCopy(first, 0, combined, 0, firstCount);
        Buffer.BlockCopy(last, 0, combined, firstCount, lastCount);
        return combined;
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }

    private static string ReadPdfInfoValue(string text, string name)
    {
        Match literal = Regex.Match(
            text,
            $@"/{Regex.Escape(name)}\s*\((?<value>(?:\\.|[^\\)])*)\)",
            RegexOptions.CultureInvariant);
        if (literal.Success)
        {
            return DecodePdfLiteral(literal.Groups["value"].Value);
        }

        Match hexadecimal = Regex.Match(
            text,
            $@"/{Regex.Escape(name)}\s*<(?<value>[0-9A-Fa-f\s]+)>",
            RegexOptions.CultureInvariant);
        return hexadecimal.Success
            ? DecodePdfHex(hexadecimal.Groups["value"].Value)
            : string.Empty;
    }

    private static string DecodePdfLiteral(string value)
    {
        string decoded = PdfEscapeRegex().Replace(
            value,
            match => match.Groups["escape"].Value switch
            {
                "n" => "\n",
                "r" => "\r",
                "t" => "\t",
                "b" => "\b",
                "f" => "\f",
                "(" => "(",
                ")" => ")",
                "\\" => "\\",
                string octal when octal.All(character => character is >= '0' and <= '7') =>
                    ((char)Convert.ToInt32(octal, 8)).ToString(),
                string other => other
            });
        return decoded.Trim();
    }

    private static string DecodePdfHex(string value)
    {
        string hex = new(value.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length % 2 != 0)
        {
            hex += "0";
        }

        byte[] bytes = Convert.FromHexString(hex);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2).Trim();
        }

        return Encoding.Latin1.GetString(bytes).Trim();
    }

    private static string StripMarkup(string value)
    {
        return MarkupRegex().Replace(value ?? string.Empty, " ").Trim();
    }

    [GeneratedRegex(@"\b\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex FourDigitYearRegex();

    [GeneratedRegex(@"\\(?<escape>[nrtbf()\\]|[0-7]{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex PdfEscapeRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupRegex();
}
