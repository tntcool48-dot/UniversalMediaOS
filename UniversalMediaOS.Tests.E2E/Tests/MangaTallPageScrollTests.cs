using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MangaTallPageScrollTests
{
    [Fact]
    public void ReaderCanReachTheBottomOfATallFinalPage()
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
        using var fixture = new AppFixture(mangaPagePng: page);
        Window window = fixture.MainWindow;
        void Wait(Func<bool> ready, string message) => Assert.True(SpinWait.SpinUntil(ready, TimeSpan.FromSeconds(5)), message);
        Button Button(string name) => window.FindFirstDescendant(cf => cf.ByName(name))!.AsButton();
        AutomationElement Reader() => window.FindAllDescendants(cf => cf.ByControlType(ControlType.List))
            .Single(element => !element.IsOffscreen && element.FindAllChildren()
                .Any(child => child.Name.Contains("/mangapages/data/", StringComparison.Ordinal)));
        AutomationElement LastImage() => Reader().FindAllChildren()
            .Single(item => item.Name.EndsWith("/page2.png", StringComparison.Ordinal))
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.Image))!;

        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        window.Patterns.Transform.Pattern.Resize(1200, 800);
        Button("Library").Invoke();
        Wait(() => window.FindFirstDescendant(cf => cf.ByName("Open Mock Manga chapters")) != null, "Manga recommendation must load.");
        Button("Open Mock Manga chapters").Invoke();
        Wait(() => window.FindFirstDescendant(cf => cf.ByName("Open chapter 1")) != null, "Native chapter must load.");
        Button("Open chapter 1").Invoke();
        Wait(() =>
        {
            try { return Reader().Patterns.Scroll.Pattern.VerticallyScrollable.Value; }
            catch (InvalidOperationException) { return false; }
        }, "Decoded tall pages must become scrollable.");
        Reader().Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
        Wait(() => LastImage().BoundingRectangle.Height > 1000, "The final page must decode at its full displayed height.");
        // Final panels on an oversized page must be reachable, not clipped below the viewport.
        Wait(() =>
        {
            var image = LastImage().BoundingRectangle;
            var viewport = Reader().BoundingRectangle;
            return image.Bottom <= viewport.Bottom + 2 && image.Bottom > viewport.Top;
        }, $"Final page bottom must be visible at the end of the reader. Image={LastImage().BoundingRectangle}; viewport={Reader().BoundingRectangle}.");
        Assert.Contains(window.FindAllDescendants(cf => cf.ByName("Mock Manga › Ch. 1")), element => !element.IsOffscreen);
        Assert.False(fixture.App.HasExited);
    }
}
