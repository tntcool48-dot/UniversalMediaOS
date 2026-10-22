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
        IntPtr ownedHandle = new(fixture.MainWindow.Properties.NativeWindowHandle.Value);
        string phase = "opening-player";
        Window Window()
        {
            Assert.False(fixture.App.HasExited, $"Owned caption app {fixture.App.ProcessId} exited.");
            // Reacquire the owned provider root after native overlay reloads;
            // keep its original HWND instead of enumerating windows by title.
            try
            {
                var window = fixture.Automation.FromHandle(ownedHandle).AsWindow();
                Assert.Equal(fixture.App.ProcessId, window.Properties.ProcessId.Value);
                return window;
            }
            catch (TimeoutException)
            {
                output.WriteLine($"Owned caption window query timed out: phase={phase}; pid={fixture.App.ProcessId}; " +
                    $"exited={fixture.App.HasExited}; paused={paused}; dropdown={dropdown}; restored={restoredWindow}; arabic={arabic}.");
                string ownedLog = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
                try
                {
                    if (File.Exists(ownedLog))
                    {
                        using var stream = new FileStream(ownedLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        stream.Seek(Math.Max(0, stream.Length - 4096), SeekOrigin.Begin);
                        using var reader = new StreamReader(stream);
                        output.WriteLine(reader.ReadToEnd());
                    }
                }
                catch (IOException exception) { output.WriteLine($"Owned diagnostic log unavailable: {exception.Message}"); }
                throw;
            }
        }
        bool arabicControls = false;
        Button? Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(
            arabicControls ? name switch
            {
                "Toggle subtitles" => "تبديل الترجمة",
                "Play or pause" => "تشغيل أو إيقاف مؤقت",
                _ => name
            } : name))?.AsButton();
        ComboBox? Captions() => Window().FindFirstDescendant(cf => cf.ByName(arabicControls ? "مسار الترجمة" : "Subtitle track"))?.AsComboBox();
        double Position() => Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))?
            .Patterns.RangeValue.Pattern.Value.Value ?? 0;
        bool? ReadPauseState(Button? button)
        {
            if (button == null) return null;
            if (button.FindFirstDescendant(cf => cf.ByText(arabicControls ? "تشغيل" : "Play")) != null) return true;
            if (button.FindFirstDescendant(cf => cf.ByText(arabicControls ? "إيقاف مؤقت" : "Pause")) != null) return false;
            return null;
        }
        bool? PauseState() => ReadPauseState(Button("Play or pause"));
        bool IsPaused() => PauseState() == true;
        string logPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
        string MenuEvents()
        {
            // The app logs each menu open and close with its cause. A close is logged once its burst settles,
            // and the log is written in the background, so wait briefly for every open to have its close.
            string events = "";
            SpinWait.SpinUntil(() =>
            {
                events = string.Join("\n", ReadLog(logPath).Split('\n').Where(line => line.Contains("[Menu]", StringComparison.Ordinal)));
                return events.Split(" opened: ").Length <= events.Split(" closed after ").Length;
            }, TimeSpan.FromSeconds(2));
            return events;
        }
        void WaitForReloadState(string label, int priorLogLength, Stopwatch selectionTimer,
            double minimumPosition, Func<double> maximumPosition)
        {
            // A ceiling, not a target: hosted runners measured up to 4 s from selection for dropdown variants,
            // because each whole-window search is slow there. The observed time is still written below.
            bool? lastPause = null;
            double lastPosition = double.NaN;
            bool eventSeen = false;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lastPause = PauseState();
                lastPosition = Position();
                // The old button state can outlive source replacement. Require
                // the new decoder's state event and the restored seek together.
                string log = ReadLog(logPath);
                string nativeEvent = paused ? "LibVLC paused event fired" : "LibVLC playing event fired";
                eventSeen = log.Length > priorLogLength && log[priorLogLength..].Contains(nativeEvent, StringComparison.Ordinal);
                return lastPause == paused && lastPosition >= minimumPosition && lastPosition <= maximumPosition() && eventSeen;
            }, TimeSpan.FromSeconds(10)), $"{label} did not restore the requested playback state: pause state {lastPause?.ToString() ?? "unreadable"} " +
                $"(wanted {paused}), position {lastPosition:0} ms (wanted {minimumPosition:0}-{maximumPosition():0}), decoder event {eventSeen}." +
                $"\n{ReadLog(logPath)}");
            output.WriteLine($"{label}: requested {(paused ? "paused" : "playing")} state observed after {selectionTimer.Elapsed.TotalSeconds:0.000} s.");
        }
        void SelectCaption(string label)
        {
            phase = "selecting-caption-" + label;
            if (!dropdown)
            {
                Button("Toggle subtitles")!.Click();
                return;
            }
            // Mouse input exercises the Popup's routed events and focus handling;
            // a Selection pattern alone bypasses the reported failure.
            void Snapshot(string phase)
            {
                string? directory = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_CAPTION_DIAGNOSTIC_DIR");
                if (string.IsNullOrWhiteSpace(directory)) return;
                // The control leaves the tree briefly while a choice reloads the source.
                if (Captions() is not { } caption)
                {
                    output.WriteLine($"{phase}: the caption control is not in the tree.");
                    return;
                }
                string snapshot = JsonSerializer.Serialize(new
                {
                    Phase = phase, Choice = label, Pid = fixture.App.ProcessId,
                    Popup = caption.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.ToString(),
                    caption.IsEnabled, caption.IsOffscreen, Bounds = caption.BoundingRectangle.ToString(),
                    Focused = caption.Properties.HasKeyboardFocus.ValueOrDefault, Selected = caption.SelectedItem?.Text,
                    Options = Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackOptions"))?
                        .Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.ToString(),
                    Position = Position(), Paused = IsPaused()
                });
                output.WriteLine(snapshot);
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "caption-popup.jsonl"), snapshot + Environment.NewLine);
            }
            ComboBox? readyCaption = null;
            Assert.True(SpinWait.SpinUntil(() => (readyCaption = Captions()) is { IsEnabled: true, IsOffscreen: false },
                TimeSpan.FromSeconds(3)), $"The caption control did not reattach before selecting {label}.\n{ReadLog(logPath)}");
            Snapshot("before-click");
            readyCaption!.Click(moveMouse: true);
            Assert.True(SpinWait.SpinUntil(() => Captions()?.Patterns.ExpandCollapse.Pattern
                .ExpandCollapseState.Value == ExpandCollapseState.Expanded, TimeSpan.FromSeconds(2)),
                $"Opening {label}; enabled={Captions()?.IsEnabled}; selected={Captions()?.SelectedItem?.Text}; " +
                $"position={Position()}; paused={IsPaused()}\n{ReadLog(logPath)}");
            Snapshot("opened");
            // A human leaves the menu open while finding the choice. Include
            // native clock/track refreshes instead of clicking immediately.
            Thread.Sleep(2000);
            Snapshot("after-two-second-open-interval");
            Assert.True(Captions()!.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value == ExpandCollapseState.Expanded,
                $"The caption menu closed during the open interval before {label}.\n{MenuEvents()}\n{ReadLog(logPath)}");
            // Choice labels follow a language switch through the next caption refresh; wait for the label.
            ComboBoxItem? choice = null;
            Assert.True(SpinWait.SpinUntil(() => (choice = Captions()?.Items.SingleOrDefault(item => item.Text == label)) != null,
                TimeSpan.FromSeconds(3)), $"The open caption list never offered {label}; it offered: " +
                string.Join(", ", Captions()?.Items.Select(item => item.Text) ?? []));
            choice!.Click(moveMouse: true);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_CAPTION_DIAGNOSTIC_DIR")))
            {
                Snapshot("after-choice");
                Thread.Sleep(500);
                Snapshot("after-choice-500-ms");
            }
        }
        async Task HoldForVisualObservation(string phase)
        {
            string? directory = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_CAPTION_VISUAL_QA_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            string release = Path.Combine(directory, phase + "-" + Guid.NewGuid().ToString("N") + ".continue");
            File.WriteAllText(Path.Combine(directory, "caption-visual-ready.json"), JsonSerializer.Serialize(new
            {
                Phase = phase, Pid = fixture.App.ProcessId,
                Window = Window().Properties.NativeWindowHandle.Value.ToInt64(),
                Profile = fixture.SandboxPath, Media = video, Position = Position(), Paused = IsPaused(),
                Selected = Captions()!.SelectedItem?.Text, Release = release, Utc = DateTime.UtcNow
            }));
            var hold = Stopwatch.StartNew();
            while (!File.Exists(release) && hold.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(200);
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
            phase = "opening-language-settings";
            int beforeSettingsLogLength = ReadLog(logPath).Length;
            string playerTab = Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab")).Last().Name;
            Window().FindFirstDescendant(cf => cf.ByAutomationId("OpenSettings"))!.AsButton().Invoke();
            Button? language = null;
            Assert.True(SpinWait.SpinUntil(() => (language = Button("Language"))?.IsEnabled == true,
                TimeSpan.FromSeconds(3)), "The opened Settings language action did not become available.");
            language!.Invoke();
            phase = "selecting-arabic";
            Window().FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox))!.AsComboBox().Select("Arabic");
            arabicControls = true;
            phase = "returning-player-after-language-change";
            Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab")).Single(tab => tab.Name == playerTab).AsButton().Invoke();
            Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackOptions"))?
                .FindFirstDescendant(cf => cf.ByText("خيارات التشغيل")) != null, TimeSpan.FromSeconds(3)),
                "The retained native options header must use the selected Arabic language.");
            Assert.True(SpinWait.SpinUntil(() => Captions()?.SelectedItem?.Properties.Name.ValueOrDefault == "الإنجليزية (ملف منزل)",
                TimeSpan.FromSeconds(3)), "The retained caption overlay must reattach with its selected downloaded English track.\n" + ReadLog(logPath));
            Button? returnedTransport = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                returnedTransport = Button("Play or pause");
                if (ReadPauseState(returnedTransport) != true) return false;
                string log = ReadLog(logPath);
                return log.Length > beforeSettingsLogLength &&
                    log[beforeSettingsLogLength..].Contains("LibVLC paused event fired", StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(3)), "Returning from language settings must settle with a paused decoder and translated Play action.\n" + ReadLog(logPath));
            if (!paused) returnedTransport!.Invoke();
        }
        if (!string.IsNullOrWhiteSpace(borrowedVideo))
        {
            // Pilot has an authored two-line cue here; decode the seek before
            // testing the paused case. This is opt-in, not a CI media dependency.
            long cuePosition = long.TryParse(Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_QA_CAPTION_POSITION_MS"),
                out long requestedCue) && requestedCue > 0 ? requestedCue : 700_000;
            Assert.True(Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!
                .Patterns.RangeValue.Pattern.Maximum.Value > cuePosition);
            Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!
                .Patterns.RangeValue.Pattern.SetValue(cuePosition);
            Assert.True(SpinWait.SpinUntil(() => Position() >= cuePosition, TimeSpan.FromSeconds(3)));
        }
        Button? observedTransport = null;
        bool? observedPause = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            observedTransport = Button("Play or pause");
            observedPause = ReadPauseState(observedTransport);
            return observedPause.HasValue;
        }, TimeSpan.FromSeconds(3)), "The native transport must expose its actual pause state before a toggle.");
        if (paused && observedPause == false)
        {
            observedTransport!.Invoke();
            Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(3)));
        }
        await HoldForVisualObservation("before-off");
        // Position() reads 0 while the slider is briefly absent from the tree; the media has already played,
        // so wait for the slider's actual value instead of taking 0 as the baseline.
        double position = 0;
        Assert.True(SpinWait.SpinUntil(() => (position = Position()) > 0, TimeSpan.FromSeconds(5)),
            "The playback slider must report the current position before a caption change.");
        var elapsed = Stopwatch.StartNew();
        int priorLogLength = ReadLog(logPath).Length;
        var selectionTimer = Stopwatch.StartNew();
        string offLabel = arabic ? "إيقاف الترجمة" : "CC Off";
        string onLabel = arabic ? "الإنجليزية (ملف منزل)" : "English (downloaded)";
        SelectCaption(offLabel);
        Assert.True(SpinWait.SpinUntil(() => Captions()?.SelectedItem?.Text == offLabel, TimeSpan.FromSeconds(3)),
            $"The {offLabel} choice did not take.\n{MenuEvents()}\n{ReadLog(logPath)}");
        WaitForReloadState("CC Off", priorLogLength, selectionTimer, position - 750,
            () => position + (paused ? 1500 : elapsed.Elapsed.TotalMilliseconds + 2500));
        Thread.Sleep(600); // Includes another decoder time/track refresh.
        Assert.Equal(offLabel, Captions()!.SelectedItem?.Text);
        Assert.InRange(Position(), position - 750,
            position + (paused ? 1500 : elapsed.Elapsed.TotalMilliseconds + 2500));
        Assert.Equal(paused, IsPaused());
        Assert.Equal(originalBytes, new FileInfo(video).Length);
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(video));
        await HoldForVisualObservation("off-stable");
        priorLogLength = ReadLog(logPath).Length;
        selectionTimer.Restart();
        SelectCaption(onLabel);
        Assert.True(SpinWait.SpinUntil(() => Captions()?.SelectedItem?.Text == onLabel, TimeSpan.FromSeconds(3)),
            $"The {onLabel} choice did not take.\n{MenuEvents()}\n{ReadLog(logPath)}");
        WaitForReloadState("English (downloaded)", priorLogLength, selectionTimer, position - 750,
            () => position + (paused ? 2500 : elapsed.Elapsed.TotalMilliseconds + 3500));
        Thread.Sleep(600);
        Assert.Equal(onLabel, Captions()!.SelectedItem?.Text);
        Assert.InRange(Position(), position - 750,
            position + (paused ? 2500 : elapsed.Elapsed.TotalMilliseconds + 3500));
        Assert.Equal(paused, IsPaused());
        Assert.Equal(originalBytes, new FileInfo(video).Length);
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(video));
        await HoldForVisualObservation("on-restored");
        if (dropdown) output.WriteLine(MenuEvents());
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
