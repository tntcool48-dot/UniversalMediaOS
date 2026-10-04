using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class BookReaderProgressTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task OpeningAndClosingFirstChapterDoesNotCompleteUnreadBook(int chapters)
    {
        using var fixture = new ReaderFixture(chapters);
        using var vm = fixture.CreateReader();
        await vm.InitializeAsync(fixture.Import.Book, fixture.Import.Asset);
        Assert.Equal(0, vm.ProgressPercent);
        await vm.SaveCurrentProgressAsync();
        var saved = await fixture.NewStore().GetAsync(fixture.Import.Book.Id, fixture.Import.Asset.Id);
        Assert.NotNull(saved);
        Assert.False(saved.IsCompleted);
        Assert.Equal(0, saved.ChapterIndex);
        Assert.Equal(0, saved.Fraction);
        Assert.Equal(fixture.OriginalHash, SHA256.HashData(File.ReadAllBytes(fixture.Source)));
    }

    [Fact]
    public async Task LastChapterSurvivesFreshReaderAndStoreUntilExplicitCompletion()
    {
        using var fixture = new ReaderFixture(3);
        using (var first = fixture.CreateReader())
        {
            await first.InitializeAsync(fixture.Import.Book, fixture.Import.Asset);
            await first.NextChapterCommand.ExecuteAsync(null);
            await first.NextChapterCommand.ExecuteAsync(null);
            await first.SaveCurrentProgressAsync();
        }
        using var restarted = fixture.CreateReader();
        await restarted.InitializeAsync(fixture.Import.Book, fixture.Import.Asset);
        Assert.Equal(2, restarted.CurrentChapterIndex);
        Assert.Equal(3, restarted.CurrentPage);
        Assert.Equal(200d / 3, restarted.ProgressPercent, 8);
        Assert.False((await fixture.NewStore().GetAsync(fixture.Import.Book.Id, fixture.Import.Asset.Id))!.IsCompleted);
        await restarted.PreviousChapterCommand.ExecuteAsync(null);
        Assert.Equal(1, restarted.CurrentChapterIndex);
        await restarted.MarkCompletedCommand.ExecuteAsync(null);
        var completed = (await fixture.NewStore().GetAsync(fixture.Import.Book.Id, fixture.Import.Asset.Id))!;
        Assert.True(completed.IsCompleted);
        Assert.Equal(1, completed.Fraction);
        Assert.Equal(2, completed.ChapterIndex);
    }

    [Fact]
    public async Task LateHistoryFailureCannotReplaceCurrentReaderLoadingState()
    {
        using var fixture = new ReaderFixture(2);
        var store = new ControlledStore();
        using var vm = fixture.CreateReader(store);
        Task oldLoad = vm.InitializeAsync(fixture.Import.Book with { Id = "old" }, fixture.Import.Asset);
        await store.OldEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task currentLoad = vm.InitializeAsync(fixture.Import.Book with { Id = "current" }, fixture.Import.Asset);
        await store.CurrentEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        store.OldResult.SetException(new IOException("old history failed"));
        await oldLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(vm.IsLoading);
        Assert.Empty(vm.ErrorMessage);
        Assert.Equal("current", vm.Book!.Id);
        store.CurrentResult.SetResult(null);
        await currentLoad;
        Assert.NotNull(vm.Document);
        Assert.False(vm.IsLoading);
        Assert.Equal("Ready to read", vm.StatusMessage);
    }

    [Fact]
    public async Task CancellationSettlesReaderWithoutPublishingAnOldDocument()
    {
        using var fixture = new ReaderFixture(2);
        var store = new ControlledStore();
        using var vm = fixture.CreateReader(store);
        using var cancellation = new CancellationTokenSource();
        Task loading = vm.InitializeAsync(fixture.Import.Book with { Id = "old" }, fixture.Import.Asset, cancellation.Token);
        await store.OldEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await loading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(vm.IsLoading);
        Assert.Null(vm.Document);
        Assert.Empty(vm.ErrorMessage);
        Assert.Contains("canceled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        store.OldResult.SetResult(null);
    }

    private sealed class ReaderFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "umos-reader-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _client = new();
        public string Source { get; }
        public byte[] OriginalHash { get; }
        public LocalBookImportResult Import { get; }

        public ReaderFixture(int chapters)
        {
            Directory.CreateDirectory(_root);
            Source = Path.Combine(_root, "fixture.epub");
            using (var archive = ZipFile.Open(Source, ZipArchiveMode.Create))
            {
                Write(archive, "META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='book.opf'/></rootfiles></container>");
                string manifest = string.Concat(Enumerable.Range(0, chapters).Select(i => $"<item id='c{i}' href='c{i}.xhtml' media-type='application/xhtml+xml'/>"));
                string spine = string.Concat(Enumerable.Range(0, chapters).Select(i => $"<itemref idref='c{i}'/>"));
                Write(archive, "book.opf", $"<package xmlns='http://www.idpf.org/2007/opf'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>Reader fixture</dc:title><dc:creator>Author</dc:creator><dc:language>en</dc:language></metadata><manifest>{manifest}</manifest><spine>{spine}</spine></package>");
                for (int i = 0; i < chapters; i++) Write(archive, $"c{i}.xhtml", $"<html xmlns='http://www.w3.org/1999/xhtml'><body><h1>Chapter {i}</h1><p>Readable chapter text.</p></body></html>");
            }
            OriginalHash = SHA256.HashData(File.ReadAllBytes(Source));
            Import = new LocalBookImportService().ImportAsync(Source).GetAwaiter().GetResult();
        }
        public JsonReadingProgressStore NewStore() => new(Path.Combine(_root, "history.json"));
        public BookReaderViewModel CreateReader(IReadingProgressStore? store = null) =>
            new(new BookReaderService(_client, Path.Combine(_root, "cache")), store ?? NewStore());
        private static void Write(ZipArchive archive, string path, string text)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open());
            writer.Write(text);
        }
        public void Dispose() { _client.Dispose(); Directory.Delete(_root, true); }
    }

    private sealed class ControlledStore : IReadingProgressStore
    {
        public TaskCompletionSource OldEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CurrentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BookReadingProgress?> OldResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BookReadingProgress?> CurrentResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BookReadingProgress?> GetAsync(string bookId, string assetId, CancellationToken cancellationToken = default)
        {
            if (bookId == "old") { OldEntered.TrySetResult(); return OldResult.Task; }
            CurrentEntered.TrySetResult(); return CurrentResult.Task;
        }
        public Task<IReadOnlyList<BookReadingProgress>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BookReadingProgress>>([]);
        public Task SaveAsync(BookReadingProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string bookId, string assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
