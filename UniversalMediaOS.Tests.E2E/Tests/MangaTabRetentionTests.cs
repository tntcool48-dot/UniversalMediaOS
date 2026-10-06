using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MangaTabRetentionTests
{
    [Fact]
    public void NativeReaderTabsKeepIndependentScrollPositionsAndCloseOnlyTheirOwnView()
    {
        // Authored tall pages exercise the production image decoder and reader layout.
        byte[] page = [];
        RecoveryLayoutTests.RunSta(() =>
        {
            const int width = 200, height = 1600;
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
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Name == AppFixture.ExpectedMainWindowTitle);
        Button Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(name))!.AsButton();
        AutomationElement Reader() => Window().FindAllDescendants(cf => cf.ByControlType(ControlType.List))
            .Single(element => !element.IsOffscreen && element.FindAllChildren()
                .Any(child => child.Name.Contains("/mangapages/data/", StringComparison.Ordinal)));
        Button[] Tabs() => Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab"))
            .Where(element => element.Name == "Manga").Select(element => element.AsButton()).ToArray();
        void OpenReader()
        {
            Button("Library").Invoke();
            Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf => cf.ByName("Open Mock Manga chapters")) != null,
                TimeSpan.FromSeconds(5)));
            Button("Open Mock Manga chapters").Invoke();
            Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf => cf.ByName("Open chapter 1")) != null,
                TimeSpan.FromSeconds(5)));
            Button("Open chapter 1").Invoke();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try { return Reader().Patterns.Scroll.Pattern.VerticallyScrollable.Value; }
                catch (InvalidOperationException) { return false; }
            }, TimeSpan.FromSeconds(5)), "Decoded native chapter pages must become scrollable.");
        }
        double SetScroll(double percent)
        {
            Reader().Patterns.Scroll.Pattern.SetScrollPercent(-1, percent);
            Assert.True(SpinWait.SpinUntil(() => percent == 0
                ? Reader().Patterns.Scroll.Pattern.VerticalScrollPercent.Value == 0
                : Reader().Patterns.Scroll.Pattern.VerticalScrollPercent.Value > 0,
                TimeSpan.FromSeconds(3)));
            return Reader().Patterns.Scroll.Pattern.VerticalScrollPercent.Value;
        }
        void AssertScroll(double expected)
        {
            Assert.True(SpinWait.SpinUntil(() =>
                Math.Abs(Reader().Patterns.Scroll.Pattern.VerticalScrollPercent.Value - expected) < 1,
                TimeSpan.FromSeconds(3)), $"Retained reader lost its {expected:F2}% scroll position; actual " +
                Reader().Patterns.Scroll.Pattern.VerticalScrollPercent.Value);
        }

        Window().Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        Window().Patterns.Transform.Pattern.Resize(1200, 800);
        OpenReader();
        double first = SetScroll(100);
        OpenReader();
        double second = SetScroll(0);
        Assert.NotEqual(first, second);
        Assert.Equal(2, Tabs().Length);
        for (int i = 0; i < 20; i++)
        {
            int selected = i % 2;
            Tabs()[selected].Invoke();
            AssertScroll(selected == 0 ? first : second);
        }
        Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab")).First().AsButton().Invoke();
        Tabs()[0].Invoke();
        AssertScroll(first);
        // Close the background reader; the first reader and its position survive.
        Window().FindAllDescendants(cf => cf.ByAutomationId("CloseTab")).Last().AsButton().Invoke();
        Assert.Single(Tabs());
        AssertScroll(first);
        Assert.False(fixture.App.HasExited);
    }
}
