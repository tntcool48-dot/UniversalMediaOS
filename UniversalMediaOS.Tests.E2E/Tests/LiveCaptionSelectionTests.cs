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

public sealed class LiveCaptionSelectionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    public async Task DownloadedCaptionOffAndOnRetainPositionAndPauseInNativeOverlay(bool paused, bool dropdown, bool restoredWindow, bool arabic)
    {
        using var fixture = new AppFixture();
        string directory = Path.Combine(fixture.SandboxPath, "caption-media");
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "episode.mp4");
        string? borrowedVideo = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_QA_NATIVE_MEDIA");
        if (string.IsNullOrWhiteSpace(borrowedVideo))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error",
                "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25", "-t", "90", "-c:v", "mpeg4", "-y", video], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
            File.WriteAllText(Path.Combine(directory, "caption.en.srt"), "1\n00:00:00,000 --> 00:01:30,000\nFirst line\nSecond line\n");
            var identity = new AudiovisualIdentity { PrimaryId = new("fixture", "show", "captions"),
                Kind = AudiovisualMediaKind.Television,
                ContentForm = AudiovisualContentForm.Series, Title = "Caption fixture", Year = 2008 };
            var unit = new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 };
            File.WriteAllText(Path.Combine(directory, "library-item.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, WorkKey = "av:fixture:show:captions", UnitKey = "season:1:episode:1", Identity = identity,
                Unit = unit, Title = "Caption fixture", MediaFile = "episode.mp4", CaptionFiles = new[] { "caption.en.srt" },
                SourceProvider = "generated-fixture", AudioNotice = "Generated video without audio."
            }));
        }
        else
        {
            // Optional local QA reuses a permanent file read-only. The generated
            // fixture and all progress writes stay in AppFixture's isolated root.
            video = Path.GetFullPath(borrowedVideo);
            Assert.True(File.Exists(video), "The explicitly supplied QA media must exist.");
        }
        Assert.NotNull(AuthorizedMediaDownloadService.ReadLibraryPlayback(video));
        long originalBytes = new FileInfo(video).Length;
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(video);
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        bool arabicControls = false;
        Button? Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(
            arabicControls && name == "Toggle subtitles" ? "تبديل الترجمة" : name))?.AsButton();
        ComboBox? Captions() => Window().FindFirstDescendant(cf => cf.ByName(arabicControls ? "مسار الترجمة" : "Subtitle track"))?.AsComboBox();
        double Position() => Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))?
            .Patterns.RangeValue.Pattern.Value.Value ?? 0;
        bool IsPaused() => Button("Play or pause")?.FindFirstDescendant(cf => cf.ByText(arabicControls ? "تشغيل" : "Play")) != null;
        string logPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
        void WaitForReloadState(string label, int priorLogLength, Stopwatch selectionTimer,
            double minimumPosition, Func<double> maximumPosition)
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (IsPaused() != paused) return false;
                double current = Position();
                if (current < minimumPosition || current > maximumPosition()) return false;
                // The old button state can outlive source replacement. Require
                // the new decoder's state event and the restored seek together.
                string log = ReadLog(logPath);
                string nativeEvent = paused ? "LibVLC paused event fired" : "LibVLC playing event fired";
                return log.Length > priorLogLength && log[priorLogLength..].Contains(nativeEvent, StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(3)), $"{label} did not restore the requested playback state.\n{ReadLog(logPath)}");
            output.WriteLine($"{label}: requested {(paused ? "paused" : "playing")} state observed after {selectionTimer.Elapsed.TotalSeconds:0.000} s.");
        }
        void SelectCaption(string label)
        {
            if (!dropdown)
            {
                Button("Toggle subtitles")!.Click();
                return;
            }
            // Mouse input exercises the Popup's routed events and focus handling;
            // a Selection pattern alone bypasses the reported failure.
            Captions()!.Click(moveMouse: true);
            Assert.True(SpinWait.SpinUntil(() => Captions()?.Patterns.ExpandCollapse.Pattern
                .ExpandCollapseState.Value == ExpandCollapseState.Expanded, TimeSpan.FromSeconds(2)),
                $"Opening {label}; enabled={Captions()!.IsEnabled}; selected={Captions()!.SelectedItem?.Text}; " +
                $"position={Position()}; paused={IsPaused()}\n{ReadLog(logPath)}");
            // A human leaves the menu open while finding the choice. Include
            // native clock/track refreshes instead of clicking immediately.
            Thread.Sleep(2000);
            Assert.Equal(ExpandCollapseState.Expanded,
                Captions()!.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value);
            Captions()!.Items.Single(item => item.Text == label).Click(moveMouse: true);
        }

        if (restoredWindow)
        {
            Window().Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
            Assert.True(SpinWait.SpinUntil(() => Window().Patterns.Window.Pattern.WindowVisualState.Value == WindowVisualState.Normal,
                TimeSpan.FromSeconds(3)));
            output.WriteLine($"Restored caption window: {Window().BoundingRectangle}");
        }
        Button("Player")!.Invoke();
        Assert.True(SpinWait.SpinUntil(() => Button("Play or pause") != null, TimeSpan.FromSeconds(3)));
        Window().FindFirstDescendant(cf => cf.ByName("Playback options"))!.Patterns.ExpandCollapse.Pattern.Expand();
        Window().FindFirstDescendant(cf => cf.ByName("Playback volume"))!.Patterns.RangeValue.Pattern.SetValue(0);
        Window().FindFirstDescendant(cf => cf.ByName("Media URL or local file path"))!.AsTextBox().Text = video;
        Button("Open media URL or path")!.Invoke();
        Assert.True(SpinWait.SpinUntil(() => Position() >= 1500 && Captions()?.IsEnabled == true, TimeSpan.FromSeconds(10)));
        Assert.Equal("English (downloaded)", Captions()!.SelectedItem?.Text);
        if (arabic)
        {
            int beforeSettingsLogLength = ReadLog(logPath).Length;
            string playerTab = Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab")).Last().Name;
            Window().FindFirstDescendant(cf => cf.ByAutomationId("OpenSettings"))!.AsButton().Invoke();
            Button("Language")!.Invoke();
            Window().FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox))!.AsComboBox().Select("Arabic");
            arabicControls = true;
            Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab")).Single(tab => tab.Name == playerTab).AsButton().Invoke();
            Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackOptions"))?
                .FindFirstDescendant(cf => cf.ByText("خيارات التشغيل")) != null, TimeSpan.FromSeconds(3)),
                "The retained native options header must use the selected Arabic language.");
            Assert.True(SpinWait.SpinUntil(() => Captions()?.SelectedItem?.Text == "الإنجليزية (ملف منزل)",
                TimeSpan.FromSeconds(3)), "The retained caption overlay must reattach with its selected downloaded English track.\n" + ReadLog(logPath));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (!IsPaused()) return false;
                string log = ReadLog(logPath);
                return log.Length > beforeSettingsLogLength &&
                    log[beforeSettingsLogLength..].Contains("LibVLC paused event fired", StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(3)), "Returning from language settings must settle with a paused decoder and translated Play action.\n" + ReadLog(logPath));
            if (!paused) Button("Play or pause")!.Invoke();
        }
        if (!string.IsNullOrWhiteSpace(borrowedVideo))
        {
            // Pilot has an authored two-line cue here; decode the seek before
            // testing the paused case. This is opt-in, not a CI media dependency.
            Assert.True(Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!
                .Patterns.RangeValue.Pattern.Maximum.Value > 700_000);
            Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!
                .Patterns.RangeValue.Pattern.SetValue(700_000);
            Assert.True(SpinWait.SpinUntil(() => Position() >= 700_000, TimeSpan.FromSeconds(3)));
        }
        if (paused && !IsPaused())
        {
            Button("Play or pause")!.Invoke();
            Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(3)));
        }
        double position = Position();
        var elapsed = Stopwatch.StartNew();
        int priorLogLength = ReadLog(logPath).Length;
        var selectionTimer = Stopwatch.StartNew();
        string offLabel = arabic ? "إيقاف الترجمة" : "CC Off";
        string onLabel = arabic ? "الإنجليزية (ملف منزل)" : "English (downloaded)";
        SelectCaption(offLabel);
        Assert.True(SpinWait.SpinUntil(() => Captions()?.SelectedItem?.Text == offLabel, TimeSpan.FromSeconds(3)), ReadLog(logPath));
        WaitForReloadState("CC Off", priorLogLength, selectionTimer, position - 750,
            () => position + (paused ? 1500 : elapsed.Elapsed.TotalMilliseconds + 2500));
        Thread.Sleep(600); // Includes another decoder time/track refresh.
        Assert.Equal(offLabel, Captions()!.SelectedItem?.Text);
        Assert.InRange(Position(), position - 750,
            position + (paused ? 1500 : elapsed.Elapsed.TotalMilliseconds + 2500));
        Assert.Equal(paused, IsPaused());
        Assert.Equal(originalBytes, new FileInfo(video).Length);
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(video));
        priorLogLength = ReadLog(logPath).Length;
        selectionTimer.Restart();
        SelectCaption(onLabel);
        Assert.True(SpinWait.SpinUntil(() => Captions()?.SelectedItem?.Text == onLabel, TimeSpan.FromSeconds(3)), ReadLog(logPath));
        WaitForReloadState("English (downloaded)", priorLogLength, selectionTimer, position - 750,
            () => position + (paused ? 2500 : elapsed.Elapsed.TotalMilliseconds + 3500));
        Thread.Sleep(600);
        Assert.Equal(onLabel, Captions()!.SelectedItem?.Text);
        Assert.InRange(Position(), position - 750,
            position + (paused ? 2500 : elapsed.Elapsed.TotalMilliseconds + 3500));
        Assert.Equal(paused, IsPaused());
        Assert.Equal(originalBytes, new FileInfo(video).Length);
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(video));
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
