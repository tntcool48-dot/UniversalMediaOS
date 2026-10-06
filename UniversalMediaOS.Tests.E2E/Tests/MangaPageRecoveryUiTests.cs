using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MangaPageRecoveryUiTests
{
    [Fact]
    public void FailedNativePageOffersRetryWithoutReplacingChapterOrOtherPages()
    {
        bool recovered = false;
        int firstRequests = 0, secondRequests = 0;
        byte[] page = [];
        RecoveryLayoutTests.RunSta(() =>
        {
            const int width = 200, height = 400;
            var pixels = new byte[width * height];
            for (int y = 0; y < height; y++)
                Array.Fill(pixels, (byte)(60 + y / 50 * 20), y * width, width);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Gray8, null, pixels, width)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            page = stream.ToArray();
        });
        using var fixture = new AppFixture(mangaPagePng: page, mangaPageStatus: request =>
        {
            if (request.Url!.AbsolutePath.EndsWith("page1.png", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref firstRequests);
                return Volatile.Read(ref recovered) ? 200 : 503;
            }
            Interlocked.Increment(ref secondRequests);
            return 200;
        });
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Name == AppFixture.ExpectedMainWindowTitle);
        Button Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(name))!.AsButton();
        bool Has(string name) => Window().FindAllDescendants(cf => cf.ByName(name)).Any(element => !element.IsOffscreen);
        void WaitFor(string name) => Assert.True(SpinWait.SpinUntil(() => Has(name), TimeSpan.FromSeconds(5)), name);
        void VisualHold(string phase)
        {
            string? directory = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_MANGA_PAGE_UI_QA_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "native-page-ui-ready.json"), JsonSerializer.Serialize(new
            {
                Phase = phase, Pid = fixture.App.ProcessId, Window = Window().Properties.NativeWindowHandle.Value.ToInt64(),
                Profile = fixture.SandboxPath, FirstRequests = Volatile.Read(ref firstRequests),
                SecondRequests = Volatile.Read(ref secondRequests), Utc = DateTime.UtcNow
            }));
            if (int.TryParse(Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_MANGA_PAGE_UI_HOLD_MS"), out int hold))
                Thread.Sleep(Math.Clamp(hold, 0, 60_000));
        }
        Button("Library").Invoke();
        WaitFor("Open Mock Manga chapters");
        Button("Open Mock Manga chapters").Invoke();
        WaitFor("Open chapter 1");
        Button("Open chapter 1").Invoke();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref firstRequests) == 1, TimeSpan.FromSeconds(5)));
        WaitFor("This page could not load.");
        WaitFor("Retry page");
        Assert.True(Has("Mock Manga › Ch. 1"));
        Assert.True(Button("Retry page").IsEnabled);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref secondRequests) == 1, TimeSpan.FromSeconds(5)));
        VisualHold("failed-page-with-retry");
        Volatile.Write(ref recovered, true);
        Button("Retry page").Invoke();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref firstRequests) == 2 && !Has("Retry page") && !Has("Loading page..."),
            TimeSpan.FromSeconds(5)), "Retry must decode the same page and clear its loading/error controls.");
        Assert.True(Has("Mock Manga › Ch. 1"));
        Assert.Equal(1, Volatile.Read(ref secondRequests));
        var firstPage = Window().FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Single(item => item.Name.EndsWith("/page1.png", StringComparison.Ordinal));
        Assert.True(firstPage.FindFirstDescendant(cf => cf.ByControlType(ControlType.Image))!.BoundingRectangle.Height > 300,
            "The retried page must display the decoded bitmap rather than the 32-pixel failure tile.");
        Assert.False(fixture.App.HasExited);
        VisualHold("same-page-decoded-after-retry");
    }
}
