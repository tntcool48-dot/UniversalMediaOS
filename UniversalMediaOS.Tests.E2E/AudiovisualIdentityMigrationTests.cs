using System.IO;
using System.Text.Json;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class AudiovisualIdentityMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "av-library-migration-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "library.json");
    public AudiovisualIdentityMigrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task LegacyMigrationBacksUpExactBytesAndIsIdempotent()
    {
        byte[] original = await Seed(Legacy(AudiovisualMediaKind.Movie, 42));
        var service = new AudiovisualLibraryService(FilePath);
        var migrated = Assert.Single(await service.GetAllAsync());
        Assert.Equal("av:tmdb:movie:42", migrated.Key.WorkKey);
        Assert.True(migrated.IsFavorite);
        Assert.Equal(125, migrated.PositionSeconds);
        Assert.Single(migrated.LegacyRecords);
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(_root, "*.bak"))));
        byte[] version2 = await File.ReadAllBytesAsync(FilePath);
        Assert.Equal(2, JsonDocument.Parse(version2).RootElement.GetProperty("Version").GetInt32());
        var reloaded = Assert.Single(await new AudiovisualLibraryService(FilePath).GetAllAsync());
        Assert.Equal(migrated.Key.WorkKey, reloaded.Key.WorkKey);
        Assert.Equal(version2, await File.ReadAllBytesAsync(FilePath));
    }

    [Fact]
    public async Task MovieCartoonEquivalenceRetainsProvenanceAndLatestSummary()
    {
        var movie = Legacy(AudiovisualMediaKind.Movie, 42) with { UpdatedUtc = DateTimeOffset.Parse("2020-01-01Z") };
        var cartoon = Legacy(AudiovisualMediaKind.Cartoon, 42) with { IsFavorite = false, Title = "Enriched title",
            Status = AudiovisualLibraryStatus.Completed, PositionSeconds = 50,
            UpdatedUtc = DateTimeOffset.Parse("2021-01-01Z"), LastOpenedUtc = DateTimeOffset.Parse("2022-01-01Z") };
        await Seed(movie, cartoon);
        var service = new AudiovisualLibraryService(FilePath);
        var result = Assert.Single(await service.GetAllAsync());
        Assert.True(result.IsFavorite);
        Assert.Equal("Enriched title", result.Title);
        Assert.Equal(AudiovisualLibraryStatus.Completed, result.Status);
        Assert.Equal(50, result.PositionSeconds);
        Assert.Equal(2, result.LegacyRecords.Count);
        Assert.Equal(2, result.Aliases.Count);
        Assert.Single(await service.GetAllAsync(AudiovisualMediaKind.Movie));
        Assert.Single(await service.GetAllAsync(AudiovisualMediaKind.Cartoon));
        Assert.Equal(result.Key.WorkKey, (await service.GetAsync(movie.Key))!.Key.WorkKey);
        Assert.Equal(result.Key.WorkKey, (await service.GetAsync(cartoon.Key))!.Key.WorkKey);
    }

    [Fact]
    public async Task AmbiguousCartoonsAndSameTitleConflictingIdsRemainSeparate()
    {
        var ambiguous = Legacy(AudiovisualMediaKind.Cartoon, 42) with
        { Key = AudiovisualLibraryKey.Create(AudiovisualMediaKind.Cartoon, 42, "Same", 2020) };
        await Seed(Legacy(AudiovisualMediaKind.Movie, 42), Legacy(AudiovisualMediaKind.Movie, 43), ambiguous);
        var entries = await new AudiovisualLibraryService(FilePath).GetAllAsync();
        Assert.Equal(3, entries.Count);
        var unknown = Assert.Single(entries, e => e.Key.ContentForm == AudiovisualContentForm.Unknown);
        Assert.StartsWith("av:local:legacy-", unknown.Key.WorkKey);
        Assert.Empty(unknown.Key.ProviderIds!);
    }

    [Fact]
    public async Task DuplicateAmbiguousLegacyRowsAreRetainedWithoutSilentOverwrite()
    {
        var row = Legacy(AudiovisualMediaKind.Movie, 42) with
        { Key = AudiovisualLibraryKey.Create(AudiovisualMediaKind.Cartoon, null, "Same", 2020) };
        await Seed(row, row with { PositionSeconds = 200 });
        var service = new AudiovisualLibraryService(FilePath);
        Assert.Equal(2, (await service.GetAllAsync()).Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(row.Key));
    }

    [Fact]
    public async Task ProviderIdsKeepCaseAndIgnoreDisplayEnrichmentInStableKeys()
    {
        var service = new AudiovisualLibraryService(FilePath);
        var upper = Key("archive", "item", "CaseSensitive");
        var lower = Key("archive", "item", "casesensitive");
        await service.SetFavoriteAsync(upper, "Same", "poster", true);
        await service.SetFavoriteAsync(lower, "Same", "", false);
        await service.SetStatusAsync(upper with { Year = 2022, Title = "New title" }, "New title", "poster", AudiovisualLibraryStatus.Watching);
        var entries = await new AudiovisualLibraryService(FilePath).GetAllAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal("New title", entries.Single(e => e.Key.WorkKey == upper.WorkKey).Title);
        var restored = AudiovisualLibraryMapping.ToMediaItem(entries.Single(e => e.Key.WorkKey == upper.WorkKey));
        Assert.Equal("CaseSensitive", restored.Identity.PrimaryId!.Value);
        Assert.Equal(upper.WorkKey, restored.PersistedWorkKey);
        Assert.Equal(upper.WorkKey, AudiovisualLibraryKey.Create(restored.Identity, restored.PersistedWorkKey).StableId);
    }

    [Fact]
    public async Task VerifiedIdEnrichmentRetainsFrozenWorkKeyAndAlias()
    {
        var service = new AudiovisualLibraryService(FilePath);
        var first = Key("imdb", "title", "tt0133093");
        await service.SetFavoriteAsync(first, "Movie", "", true);
        var enriched = AudiovisualLibraryKey.Create(new() { ContentForm = AudiovisualContentForm.Feature,
            PrimaryId = new("wikidata", "entity", "Q83495"), ExternalIds = [new("imdb", "title", "tt0133093")] });
        var entry = await service.SetStatusAsync(enriched, "Enriched", "", AudiovisualLibraryStatus.Watching);
        Assert.Equal(first.WorkKey, entry.Key.WorkKey);
        Assert.Contains(enriched.WorkKey!, entry.Aliases);
        Assert.Single(await service.GetAllAsync());
        Assert.Equal(first.WorkKey, (await new AudiovisualLibraryService(FilePath).GetAsync(enriched))!.Key.WorkKey);
    }

    [Fact]
    public async Task FailedMigrationPreservesOriginalAndCanRetryInSameService()
    {
        byte[] original = await Seed(Legacy(AudiovisualMediaKind.Movie, 42));
        bool fail = true;
        var service = new AudiovisualLibraryService(FilePath, (temporary, destination) =>
        { if (fail) throw new IOException("Injected commit failure"); File.Replace(temporary, destination, null); });
        await Assert.ThrowsAsync<IOException>(() => service.GetAllAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(FilePath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        fail = false;
        Assert.Single(await service.GetAllAsync());
        Assert.Single(Directory.GetFiles(_root, "*.bak"));
    }

    [Fact]
    public async Task FailedUpdatesAndRemovesDoNotPublishUnwrittenMemoryOrEvents()
    {
        var key = Key("imdb", "title", "tt0133093");
        await new AudiovisualLibraryService(FilePath).SetFavoriteAsync(key, "Movie", "", true);
        byte[] original = await File.ReadAllBytesAsync(FilePath);
        var failing = new AudiovisualLibraryService(FilePath, (_, _) => throw new IOException("Injected commit failure"));
        int events = 0;
        failing.LibraryChanged += (_, _) => events++;
        await Assert.ThrowsAsync<IOException>(() => failing.SetFavoriteAsync(key, "Movie", "", false));
        Assert.True((await failing.GetAsync(key))!.IsFavorite);
        await Assert.ThrowsAsync<IOException>(() => failing.RemoveAsync(key));
        Assert.NotNull(await failing.GetAsync(key));
        Assert.Equal(0, events);
        Assert.Equal(original, await File.ReadAllBytesAsync(FilePath));
    }

    [Theory]
    [InlineData("broken JSON")]
    [InlineData("{\"Version\":99,\"Entries\":[]}")]
    public async Task UnreadableOrFutureLibraryCannotBeOverwritten(string content)
    {
        await File.WriteAllTextAsync(FilePath, content);
        var service = new AudiovisualLibraryService(FilePath);
        Assert.NotNull(await Record.ExceptionAsync(() => service.GetAllAsync()));
        Assert.NotNull(await Record.ExceptionAsync(() => service.SetFavoriteAsync(Key("imdb", "title", "tt1"), "Movie", "", true)));
        Assert.Equal(content, await File.ReadAllTextAsync(FilePath));
        Assert.Empty(Directory.GetFiles(_root, "*.bak"));
    }

    [Fact]
    public async Task CancellationBeforeLoadDoesNotCacheAnEmptyLibrary()
    {
        await Seed(Legacy(AudiovisualMediaKind.Movie, 42));
        var service = new AudiovisualLibraryService(FilePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAllAsync(cancellationToken: cancellation.Token));
        Assert.Single(await service.GetAllAsync());
    }

    private async Task<byte[]> Seed(params AudiovisualLibraryEntry[] rows)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(rows);
        await File.WriteAllBytesAsync(FilePath, bytes);
        return bytes;
    }
    private static AudiovisualLibraryKey Key(string provider, string space, string id) =>
        AudiovisualLibraryKey.Create(new() { Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature,
            Title = "Same", PrimaryId = new(provider, space, id), Year = 2020 });
    private static AudiovisualLibraryEntry Legacy(AudiovisualMediaKind kind, int id) => new()
    { Key = AudiovisualLibraryKey.Create(kind, id, "Same", 2020, AudiovisualContentForm.Feature), Title = "Same",
        IsFavorite = true, Status = AudiovisualLibraryStatus.Watching, LastOpenedUtc = DateTimeOffset.Parse("2020-01-01Z"),
        PositionSeconds = 125, DurationSeconds = 600 };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
