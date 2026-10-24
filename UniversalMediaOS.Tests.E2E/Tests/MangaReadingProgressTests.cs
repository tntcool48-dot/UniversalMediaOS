using System.IO;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MangaReadingProgressTests
{
    private static readonly MangaSearchResult Manga = new() { Id = "manga-exact", Title = "Exact manga" };

    [Fact]
    public async Task ProgressContinuesOnlyTheExactChapterIdAcrossNewReaders()
    {
        string directory = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "MangaProgress-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "manga_reading_progress.json");
        try
        {
            var first = new FixedMangaService(Chapter("chapter-a", "1"), Chapter("chapter-b", "2"));
            using (var reader = new MangaViewModel(first, new MangaReadingProgressStore(path)))
            {
                await reader.ReadCommand.ExecuteAsync(Manga);
                Assert.False(reader.HasContinueReading);
                await reader.SelectChapterCommand.ExecuteAsync(first.Chapters[1]);
                Assert.Equal(-1, reader.PendingRestorePage);
                reader.ReportVisiblePage(2);
            }

            using (var reader = new MangaViewModel(first, new MangaReadingProgressStore(path)))
            {
                await reader.ReadCommand.ExecuteAsync(Manga);
                Assert.Same(first.Chapters[1], reader.ContinueChapter);
                Assert.Equal("Continue Ch. 2 · page 3 of 3", reader.ContinueText);
                Assert.Equal("Continue chapter 2 from page 3", reader.ContinueAccessibleName);

                await reader.ContinueReadingCommand.ExecuteAsync(null);
                Assert.Same(first.Chapters[1], reader.SelectedChapter);
                Assert.Equal(2, reader.PendingRestorePage);
                reader.ReportVisiblePage(0); // The reader has not reached the restored page yet.
                reader.CompletePageRestore();
                reader.ReportVisiblePage(1);
                reader.GoBackCommand.Execute(null);
                Assert.Equal("Continue Ch. 2 · page 2 of 3", reader.ContinueText);
            }

            // A chapter re-uploaded under a new ID is not matched by its number.
            var renumbered = new FixedMangaService(Chapter("chapter-a", "1"), Chapter("chapter-c", "2"));
            using (var reader = new MangaViewModel(renumbered, new MangaReadingProgressStore(path)))
            {
                await reader.ReadCommand.ExecuteAsync(Manga);
                Assert.False(reader.HasContinueReading);
                Assert.False(reader.ContinueReadingCommand.CanExecute(null));
            }

            // Damaged progress is not guessed at, and the next chapter opened replaces it.
            File.WriteAllText(path, "{ not json");
            using (var reader = new MangaViewModel(first, new MangaReadingProgressStore(path)))
            {
                await reader.ReadCommand.ExecuteAsync(Manga);
                Assert.False(reader.HasContinueReading);
                await reader.SelectChapterCommand.ExecuteAsync(first.Chapters[0]);
            }

            using (var reader = new MangaViewModel(first, new MangaReadingProgressStore(path)))
            {
                await reader.ReadCommand.ExecuteAsync(Manga);
                Assert.Equal("Continue chapter 1 from page 1", reader.ContinueAccessibleName);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static MangaChapter Chapter(string id, string number) => new() { Id = id, ChapterNumber = number, Pages = 3 };

    private sealed class FixedMangaService(params MangaChapter[] chapters) : MangaService
    {
        public MangaChapter[] Chapters { get; } = chapters;

        public override Task<List<MangaChapter>> GetChaptersAsync(string mangaId, CancellationToken token = default) =>
            Task.FromResult(Chapters.ToList());

        public override Task<List<string>> GetPageUrlsAsync(string chapterId, CancellationToken token = default) =>
            Task.FromResult(Enumerable.Range(1, 3).Select(page => $"http://127.0.0.1/{chapterId}/page{page}.png").ToList());
    }
}
