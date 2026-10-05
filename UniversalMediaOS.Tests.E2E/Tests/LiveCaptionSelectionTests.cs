using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class LiveCaptionSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadedCaptionOffAndOnRetainPositionAndPauseInNativeOverlay(bool paused)
    {
        using var fixture = new AppFixture();
        string directory = Path.Combine(fixture.SandboxPath, "caption-media");
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "episode.mp4");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25", "-t", "40", "-c:v", "mpeg4", "-y", video], deadline.Token);
        Assert.Equal(0, encoded.ExitCode);
        File.WriteAllText(Path.Combine(directory, "caption.en.srt"), "1\n00:00:00,000 --> 00:00:40,000\nFirst line\nSecond line\n");
        var identity = new AudiovisualIdentity { Kind = AudiovisualMediaKind.Television,
            ContentForm = AudiovisualContentForm.Series, Title = "Caption fixture", Year = 2008 };
        var unit = new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 };
        File.WriteAllText(Path.Combine(directory, "library-item.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, WorkKey = "caption:fixture", UnitKey = "season:1:episode:1", Identity = identity,
            Unit = unit, Title = "Caption fixture", MediaFile = "episode.mp4", CaptionFiles = new[] { "caption.en.srt" },
            SourceProvider = "generated-fixture", AudioNotice = "Generated video without audio."
        }));
        Assert.NotNull(AuthorizedMediaDownloadService.ReadLibraryPlayback(video));
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        Button? Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(name))?.AsButton();
        ComboBox? Captions() => Window().FindFirstDescendant(cf => cf.ByName("Subtitle track"))?.AsComboBox();
        double Position() => Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))?
            .Patterns.RangeValue.Pattern.Value.Value ?? 0;
        bool IsPaused() => Button("Play or pause")?.FindFirstDescendant(cf => cf.ByText("Play")) != null;
        string logPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");

        Button("Player")!.Invoke();
        Assert.True(SpinWait.SpinUntil(() => Button("Play or pause") != null, TimeSpan.FromSeconds(3)));
        Window().FindFirstDescendant(cf => cf.ByName("Playback options"))!.Patterns.ExpandCollapse.Pattern.Expand();
        Window().FindFirstDescendant(cf => cf.ByName("Media URL or local file path"))!.AsTextBox().Text = video;
        Button("Open media URL or path")!.Invoke();
        Assert.True(SpinWait.SpinUntil(() => Position() >= 1500 && Captions()?.IsEnabled == true, TimeSpan.FromSeconds(10)));
        Assert.Equal("English (downloaded)", Captions()!.SelectedItem?.Text);
        if (paused)
        {
            Button("Play or pause")!.Invoke();
            Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(3)));
        }
        double position = Position();
        Button("Toggle subtitles")!.Click();
        Assert.True(SpinWait.SpinUntil(() => Captions()?.SelectedItem?.Text == "CC Off", TimeSpan.FromSeconds(3)), ReadLog(logPath));
        Thread.Sleep(600); // Includes another decoder time/track refresh.
        Assert.Equal("CC Off", Captions()!.SelectedItem?.Text);
        Assert.InRange(Position(), position - 750, position + 6000);
        Assert.Equal(paused, IsPaused());
        Button("Toggle subtitles")!.Click();
        Assert.True(SpinWait.SpinUntil(() => Captions()?.SelectedItem?.Text == "English (downloaded)", TimeSpan.FromSeconds(3)), ReadLog(logPath));
        Thread.Sleep(600);
        Assert.Equal("English (downloaded)", Captions()!.SelectedItem?.Text);
        Assert.InRange(Position(), position - 750, position + 12000);
        Assert.Equal(paused, IsPaused());
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
