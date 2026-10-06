using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MangaExternalReaderOutcomeTests
{
    [Fact]
    public async Task LateWebsiteCallbacksCannotReplaceReturnedReselectedOrDisposedReaderState()
    {
        using var reader = new MangaViewModel(new MangaService());
        reader.SelectedManga = new() { Id = "exact-work", Title = "Exact work" };
        var chapter = new MangaChapter { Id = "exact-external-unit", ChapterNumber = "1", ExternalUrl = "https://reader.example/unit-one" };
        reader.Chapters.Add(chapter);
        await reader.SelectChapterCommand.ExecuteAsync(chapter);
        int oldGeneration = reader.ExternalReaderGeneration;
        reader.ReportExternalReader(oldGeneration, chapter, chapter.ExternalUrl, "failed current", true);
        Assert.True(reader.CanRetryWebsite);
        reader.GoBackCommand.Execute(null);
        reader.ReportExternalReader(oldGeneration, chapter, chapter.ExternalUrl, "late after Back", true);
        Assert.Equal(1, reader.CurrentViewMode);
        Assert.Same(chapter, Assert.Single(reader.Chapters));
        Assert.Empty(reader.ReaderStatus);
        Assert.False(reader.CanRetryWebsite);
        await reader.SelectChapterCommand.ExecuteAsync(chapter);
        int currentGeneration = reader.ExternalReaderGeneration;
        Assert.NotEqual(oldGeneration, currentGeneration);
        reader.ReportExternalReader(oldGeneration, chapter, chapter.ExternalUrl, "late same-unit attempt", true);
        reader.ReportExternalReader(currentGeneration, new MangaChapter { Id = chapter.Id, ExternalUrl = chapter.ExternalUrl },
            chapter.ExternalUrl, "different selected chapter instance", true);
        reader.ReportExternalReader(currentGeneration, chapter, "https://reader.example/different-unit", "different URL", true);
        Assert.Equal("Opening website reader...", reader.ReaderStatus);
        Assert.False(reader.CanRetryWebsite);
        Assert.Same(chapter, reader.SelectedChapter);
        reader.ReportExternalReader(currentGeneration, chapter, chapter.ExternalUrl, "Website reader", false);
        Assert.Equal("Website reader", reader.ReaderStatus);
        reader.CancelActiveWork();
        reader.ReportExternalReader(currentGeneration, chapter, chapter.ExternalUrl, "late after cancel", true);
        Assert.Equal("Website reader", reader.ReaderStatus);
        Assert.False(reader.CanRetryWebsite);
        reader.Dispose();
        reader.ReportExternalReader(currentGeneration, chapter, chapter.ExternalUrl, "late after dispose", true);
        Assert.Equal("Website reader", reader.ReaderStatus);
    }
}
