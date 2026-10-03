using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UniversalMediaOS.Core.OtherMedia;

public enum AudiovisualCatalogMode { Discover, Search }

[Flags]
public enum AudiovisualCatalogCapabilities { None = 0, Discover = 1, Search = 2, Continuation = 4, Locales = 8, Episodes = 16 }

public sealed record AudiovisualCatalogRequest(
    AudiovisualMediaKind Kind,
    AudiovisualCatalogMode Mode = AudiovisualCatalogMode.Discover,
    string Query = "",
    int PageSize = 20,
    string? ContinuationToken = null,
    string? Locale = null)
{
    internal AudiovisualCatalogRequest Normalize()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Mode) || PageSize is < 1 or > 100 || (Locale?.Length ?? 0) > 64 || (Query?.Length ?? 0) > 1000)
            throw new ArgumentException("Invalid catalog request.");
        return this with { Query = Mode == AudiovisualCatalogMode.Search ? (Query ?? "").Trim() : "", Locale = Locale?.Trim() };
    }
}

public sealed record AudiovisualCatalogPage(
    IReadOnlyList<AudiovisualMediaItem> Items,
    string? NextToken,
    IReadOnlyList<ProviderOutcome> Outcomes,
    bool IsPartial = false,
    bool IsStale = false)
{
    internal static AudiovisualCatalogPage Failure(string provider, ProviderOutcomeStatus status, string code) =>
        new(Array.Empty<AudiovisualMediaItem>(), null, [new(provider, status, TimeSpan.Zero, DiagnosticCode: code)]);
}

public interface IPagedAudiovisualCatalogService : IAudiovisualCatalogService
{
    AudiovisualCatalogCapabilities Capabilities { get; }
    Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default);
}

// Tokens are scoped to this app run. Only cursor coordinates and an opaque binding hash
// are encoded; API keys and private search terms are never embedded in the token.
internal static class CatalogContinuation
{
    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);
    internal sealed record Cursor(int Page, int Offset, string Binding);

    private static string Binding(string provider, string revision, AudiovisualCatalogRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { provider, revision, request.Kind, request.Mode, request.Query, request.Locale, request.PageSize }))));

    internal static Cursor Read(string provider, string revision, AudiovisualCatalogRequest request, int maxOffset = 100)
    {
        string binding = Binding(provider, revision, request);
        if (request.ContinuationToken == null) return new(1, 0, binding);
        try
        {
            if (request.ContinuationToken.Length > 2048) throw new FormatException();
            string[] parts = request.ContinuationToken.Split('.');
            if (parts.Length != 2) throw new FormatException();
            byte[] payload = Convert.FromBase64String(parts[0]);
            byte[] signature = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Secret, payload), signature)) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<Cursor>(payload);
            if (cursor == null || cursor.Binding != binding || cursor.Page < 1 || cursor.Page > 1_000_000 || cursor.Offset < 0 || cursor.Offset > maxOffset)
                throw new FormatException();
            return cursor;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("Continuation expired or does not belong to this catalog request.", nameof(request));
        }
    }

    internal static string Write(Cursor cursor)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(cursor);
        return Convert.ToBase64String(payload) + "." + Convert.ToBase64String(HMACSHA256.HashData(Secret, payload));
    }

    internal static AudiovisualCatalogPage Slice(IReadOnlyList<AudiovisualMediaItem> items, Cursor cursor,
        int pageSize, bool hasNextPage, ProviderOutcome outcome, bool stale)
    {
        var selected = items.Skip(cursor.Offset).Take(pageSize).ToArray();
        int nextOffset = cursor.Offset + selected.Length;
        string? next = nextOffset < items.Count ? Write(cursor with { Offset = nextOffset })
            : hasNextPage ? Write(cursor with { Page = cursor.Page + 1, Offset = 0 }) : null;
        return new(selected, next, [outcome], IsStale: stale);
    }
}
