using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class LiveNativeSelectorTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NativeSpeedAndAudioMouseChoicesRetainPauseAndSurviveCaptionReload()
    {
        using var fixture = new AppFixture();
        string directory = Path.Combine(fixture.SandboxPath, "native-selector-media");
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "tones.mkv");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", [
            "-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-t", "60", "-c:v", "mpeg4", "-c:a", "aac",
            "-metadata:s:a:0", "title=Tone 440 Hz", "-metadata:s:a:1", "title=Tone 880 Hz", "-y", video
        ], deadline.Token);
        Assert.Equal(0, encoded.ExitCode);
        File.WriteAllText(Path.Combine(directory, "caption.en.srt"),
            "1\n00:00:00,000 --> 00:01:00,000\nSelector fixture\nSecond line\n");
        File.WriteAllText(Path.Combine(directory, "library-item.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, WorkKey = "av:fixture:show:native-selector", UnitKey = "season:1:episode:1",
            Identity = new AudiovisualIdentity { PrimaryId = new("fixture", "show", "native-selector"),
                Kind = AudiovisualMediaKind.Television,
                ContentForm = AudiovisualContentForm.Series, Title = "Native selector fixture", Year = 2008 },
            Unit = new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 }, Title = "Native selector fixture",
            MediaFile = "tones.mkv", CaptionFiles = new[] { "caption.en.srt" }, SourceProvider = "generated-fixture",
            AudioNotice = "Generated tones; no spoken audio language."
        }));

        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        Button? Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(name))?.AsButton();
        ComboBox? Choice(string name) => Window().FindFirstDescendant(cf => cf.ByName(name))?.AsComboBox();
        double Position() => Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!
            .Patterns.RangeValue.Pattern.Value.Value;
        bool IsPaused() => Button("Play or pause")?.FindFirstDescendant(cf => cf.ByText("Play")) != null;
        string logPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
        void Select(string name, Func<ComboBoxItem, bool> matches)
        {
            // Allow the native overlay to reveal/reflow as the pointer reaches
            // the control, before injecting its click.
            Choice(name)!.Click(moveMouse: true);
            Assert.True(SpinWait.SpinUntil(() => Choice(name)!.Patterns.ExpandCollapse.Pattern
                .ExpandCollapseState.Value == ExpandCollapseState.Expanded, TimeSpan.FromSeconds(2)),
                $"Opening {name}; enabled={Choice(name)!.IsEnabled}; bounds={Choice(name)!.BoundingRectangle}; " +
                $"position={Position()}; paused={IsPaused()}\n{ReadLog(logPath)}");
            Thread.Sleep(1000); // Keep the Popup open across native track/clock refreshes.
            var items = Choice(name)!.Items;
            output.WriteLine($"{name}: {string.Join(", ", items.Select(item => item.Text))}");
            items.Single(matches).Click(moveMouse: true);
            Assert.True(SpinWait.SpinUntil(() => Choice(name)?.SelectedItem is { } selected && matches(selected),
                TimeSpan.FromSeconds(2)), $"{name} returned to '{Choice(name)?.SelectedItem?.Text}'.\n{ReadLog(logPath)}");
        }
        double MeasureClockRatio()
        {
            Thread.Sleep(1000); // Let VLC apply the new rate before measuring.
            var clock = Stopwatch.StartNew();
            double start = Position();
            Thread.Sleep(3500);
            double end = Position();
            clock.Stop();
            double ratio = (end - start) / clock.Elapsed.TotalMilliseconds;
            output.WriteLine($"Media advanced {end - start:0} ms over {clock.Elapsed.TotalMilliseconds:0} ms; ratio {ratio:0.000}.");
            return ratio;
        }

        Button("Player")!.Invoke();
        Assert.True(SpinWait.SpinUntil(() => Button("Play or pause") != null, TimeSpan.FromSeconds(3)));
        Window().FindFirstDescendant(cf => cf.ByName("Playback options"))!.Patterns.ExpandCollapse.Pattern.Expand();
        Window().FindFirstDescendant(cf => cf.ByName("Playback volume"))!.Patterns.RangeValue.Pattern.SetValue(0);
        Window().FindFirstDescendant(cf => cf.ByName("Media URL or local file path"))!.AsTextBox().Text = video;
        Button("Open media URL or path")!.Invoke();
        Assert.True(SpinWait.SpinUntil(() => Position() >= 1500 && Choice("Audio track")?.IsEnabled == true &&
            Choice("Subtitle track")?.IsEnabled == true, TimeSpan.FromSeconds(10)));
        Assert.Contains("Tone 440 Hz", Choice("Audio track")!.SelectedItem!.Text);
        Assert.Equal("English (downloaded)", Choice("Subtitle track")!.SelectedItem!.Text);

        Select("Playback speed", item => item.Text == "2x");
        Assert.InRange(MeasureClockRatio(), 1.5, 2.6);
        Select("Playback speed", item => item.Text == "0.5x");
        Assert.InRange(MeasureClockRatio(), 0.25, 0.85);

        Button("Play or pause")!.Invoke();
        Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(3)));
        double pausedAt = Position();
        Select("Playback speed", item => item.Text == "0.75x");
        Select("Audio track", item => item.Text.Contains("Tone 880 Hz", StringComparison.Ordinal));
        Thread.Sleep(1000);
        Assert.True(IsPaused());
        Assert.InRange(Position(), pausedAt - 500, pausedAt + 500);
        Assert.Contains("Tone 880 Hz", Choice("Audio track")!.SelectedItem!.Text);

        // An external caption change replaces the native input. The requested
        // rate/audio and paused position must remain, rather than just the labels.
        Button("Toggle subtitles")!.Click();
        Assert.True(SpinWait.SpinUntil(() => Choice("Subtitle track")?.SelectedItem?.Text == "CC Off" &&
            Choice("Audio track")?.SelectedItem?.Text.Contains("Tone 880 Hz", StringComparison.Ordinal) == true &&
            IsPaused(), TimeSpan.FromSeconds(5)));
        Thread.Sleep(1000);
        Assert.True(IsPaused());
        Assert.InRange(Position(), pausedAt - 500, pausedAt + 1500);
        Assert.Equal("0.75x", Choice("Playback speed")!.SelectedItem!.Text);
        Button("Play or pause")!.Invoke();
        Assert.True(SpinWait.SpinUntil(() => !IsPaused(), TimeSpan.FromSeconds(3)));
        Assert.InRange(MeasureClockRatio(), 0.45, 1.05);
        Select("Audio track", item => item.Text.Contains("Tone 440 Hz", StringComparison.Ordinal));
        Thread.Sleep(1000);
        Assert.Contains("Tone 440 Hz", Choice("Audio track")!.SelectedItem!.Text);
        Assert.False(IsPaused());
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
