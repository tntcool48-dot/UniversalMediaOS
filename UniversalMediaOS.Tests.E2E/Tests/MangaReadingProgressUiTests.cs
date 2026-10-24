using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MangaReadingProgressUiTests
{
    private const string TwoChapterFeed = "{\"total\": 2, \"data\": [" +
        "{\"id\": \"mock-chapter-1\", \"type\": \"chapter\", \"attributes\": {\"chapter\": \"1\", \"title\": \"Chapter 1\", \"pages\": 2, \"externalUrl\": \"\"}}," +
        "{\"id\": \"mock-chapter-2\", \"type\": \"chapter\", \"attributes\": {\"chapter\": \"2\", \"title\": \"Chapter 2\", \"pages\": 2, \"externalUrl\": \"\"}}]}";

    [Fact]
    public void ContinueReadingReturnsToTheLastChapterAndPageAfterRestart()
    {
        byte[] page = [];
        RecoveryLayoutTests.RunSta(() =>
        {
            const int width = 600, height = 1600;
            var pixels = new byte[width * height];
            for (int y = 0; y < height; y++)
                Array.Fill(pixels, (byte)(40 + y / 100 * 12), y * width, width);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Gray8, null, pixels, width)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            page = stream.ToArray();
        });
        using var fixture = new AppFixture(mangaPagePng: page, mangaChapterFeed: _ => (200, TwoChapterFeed));
        Window Window() => fixture.MainWindow;
        void Wait(Func<bool> ready, string message) => Assert.True(SpinWait.SpinUntil(ready, TimeSpan.FromSeconds(5)), message);
        AutomationElement? Find(string name) => Window().FindFirstDescendant(cf => cf.ByName(name));
        AutomationElement Reader() => Window().FindAllDescendants(cf => cf.ByControlType(ControlType.List))
            .Single(element => !element.IsOffscreen && element.FindAllChildren()
                .Any(child => child.Name.Contains("/mangapages/data/", StringComparison.Ordinal)));
        void WaitForReader() => Wait(() =>
        {
            try { return Reader().Patterns.Scroll.Pattern.VerticallyScrollable.Value; }
            catch (InvalidOperationException) { return false; }
        }, "Decoded chapter pages must become scrollable.");
        void OpenChapters()
        {
            Window().Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
            Window().Patterns.Transform.Pattern.Resize(1200, 800);
            Find("Manga")!.AsButton().Invoke();
            Wait(() => Find("Open Mock Manga chapters") != null, "Manga recommendation must load.");
            Find("Open Mock Manga chapters")!.AsButton().Invoke();
            Wait(() => Find("Open chapter 2") != null, "Both native chapters must load.");
        }

        OpenChapters();
        Assert.Null(Find("Continue chapter 2 from page 2"));
        Find("Open chapter 2")!.AsButton().Invoke();
        WaitForReader();
        Reader().Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
        Wait(() => Reader().Patterns.Scroll.Pattern.VerticalScrollPercent.Value > 99, "The reader must reach the final page.");

        // A new process with the same profile must offer the exact chapter and page that was being read.
        fixture.Restart();
        OpenChapters();
        Wait(() => Find("Continue chapter 2 from page 2") != null,
            "The chapter list must offer to continue the last chapter at its last page after restart.");
        Assert.Null(Find("Continue chapter 1 from page 1"));
        Find("Continue chapter 2 from page 2")!.AsButton().Invoke();
        WaitForReader();
        Assert.Contains(Window().FindAllDescendants(cf => cf.ByName("Mock Manga › Ch. 2")), element => !element.IsOffscreen);
        Wait(() =>
        {
            var finalPage = Reader().FindAllChildren().Single(item => item.Name.EndsWith("/page2.png", StringComparison.Ordinal));
            var viewport = Reader().BoundingRectangle;
            return !finalPage.IsOffscreen && finalPage.BoundingRectangle.Top <= viewport.Top + 2 &&
                   finalPage.BoundingRectangle.Bottom > viewport.Top;
        }, $"Continuing must open at page 2; scroll={Reader().Patterns.Scroll.Pattern.VerticalScrollPercent.Value}.");
        Assert.False(fixture.App.HasExited);
    }
}
