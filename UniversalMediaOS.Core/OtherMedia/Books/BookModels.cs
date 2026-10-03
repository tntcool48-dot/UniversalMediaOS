using System.Globalization;

namespace UniversalMediaOS.Core.OtherMedia.Books;

public enum BookCatalogSource
{
    OpenLibrary,
    GoogleBooks,
    InternetArchive,
    Local,
    AnnasArchive
}

public enum BookFileFormat
{
    Unknown,
    Epub,
    Pdf
}

public enum BookAccessKind
{
    MetadataOnly,
    Preview,
    PublicDomain,
    OpenLicense,
    Local
}

public sealed record BookIdentifier
{
    public string Scheme { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public sealed record BookAsset
{
    public string Id { get; init; } = string.Empty;
    public string BookId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public BookFileFormat Format { get; init; }
    public BookAccessKind Access { get; init; }
    public string Location { get; init; } = string.Empty;
    public string SourceLabel { get; init; } = string.Empty;
    public string RightsStatement { get; init; } = string.Empty;
    public long? SizeBytes { get; init; }
    public bool IsDownloadAllowed { get; init; }

    public bool IsLocal => Access == BookAccessKind.Local;

    public string FormatLabel => Format switch
    {
        BookFileFormat.Epub => "EPUB",
        BookFileFormat.Pdf => "PDF",
        _ => "Unknown"
    };

    public string SizeLabel => SizeBytes is > 0
        ? FormatSize(SizeBytes.Value)
        : string.Empty;

    public string AccessLabel => Access switch
    {
        BookAccessKind.PublicDomain => "Public domain",
        BookAccessKind.OpenLicense => "Open license",
        BookAccessKind.Local => "Local file",
        BookAccessKind.Preview => "Preview",
        _ => "Metadata only"
    };

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int suffix = 0;
        while (size >= 1024 && suffix < suffixes.Length - 1)
        {
            size /= 1024;
            suffix++;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{size:0.#} {suffixes[suffix]}");
    }
}

public sealed record BookRecord
{
    public string Id { get; init; } = string.Empty;
    public BookCatalogSource Source { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();
    public string Description { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string PublishedDate { get; init; } = string.Empty;
    public int? PublishedYear { get; init; }
    public int? PageCount { get; init; }
    public string Language { get; init; } = string.Empty;
    public string CoverUrl { get; init; } = string.Empty;
    public string PreviewUrl { get; init; } = string.Empty;
    public string InfoUrl { get; init; } = string.Empty;
    public IReadOnlyList<string> Subjects { get; init; } = Array.Empty<string>();
    public IReadOnlyList<BookIdentifier> Identifiers { get; init; } = Array.Empty<BookIdentifier>();
    public IReadOnlyList<BookAsset> Assets { get; init; } = Array.Empty<BookAsset>();

    public string AuthorLine => Authors.Count == 0
        ? "Unknown author"
        : string.Join(", ", Authors);

    public string SubjectLine => string.Join(" · ", Subjects.Take(3));

    public string MetadataLine
    {
        get
        {
            var parts = new List<string>();
            if (PublishedYear is > 0)
            {
                parts.Add(PublishedYear.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (PageCount is > 0)
            {
                parts.Add($"{PageCount.Value.ToString(CultureInfo.InvariantCulture)} pages");
            }

            if (!string.IsNullOrWhiteSpace(Language))
            {
                parts.Add(Language.ToUpperInvariant());
            }

            return string.Join(" · ", parts);
        }
    }
}

public sealed record BookSearchPage
{
    public IReadOnlyList<BookRecord> Items { get; init; } = Array.Empty<BookRecord>();
    public int Total { get; init; }
}

public sealed record LocalBookImportResult
{
    public BookRecord Book { get; init; } = new();
    public BookAsset Asset { get; init; } = new();
}

public sealed record BookReaderChapter
{
    public int Index { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
}

public sealed record BookReaderDocument
{
    public string AssetId { get; init; } = string.Empty;
    public BookFileFormat Format { get; init; }
    public string StartLocation { get; init; } = string.Empty;
    public IReadOnlyList<BookReaderChapter> Chapters { get; init; } = Array.Empty<BookReaderChapter>();
    public string WorkingDirectory { get; init; } = string.Empty;
}

public sealed record BookReadingProgress
{
    public string BookId { get; init; } = string.Empty;
    public string AssetId { get; init; } = string.Empty;
    public string BookTitle { get; init; } = string.Empty;
    public IReadOnlyList<string> BookAuthors { get; init; } = Array.Empty<string>();
    public string CoverUrl { get; init; } = string.Empty;
    public BookCatalogSource BookSource { get; init; }
    public string AssetDisplayName { get; init; } = string.Empty;
    public string AssetLocation { get; init; } = string.Empty;
    public string AssetSourceLabel { get; init; } = string.Empty;
    public string AssetRightsStatement { get; init; } = string.Empty;
    public BookFileFormat AssetFormat { get; init; }
    public BookAccessKind AssetAccess { get; init; }
    public bool AssetDownloadAllowed { get; init; }
    public int ChapterIndex { get; init; }
    public int PageNumber { get; init; }
    public string Location { get; init; } = string.Empty;
    public double Fraction { get; init; }
    public bool IsCompleted { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public int Percent => (int)Math.Round(
        Math.Clamp(Fraction, 0, 1) * 100,
        MidpointRounding.AwayFromZero);
}
