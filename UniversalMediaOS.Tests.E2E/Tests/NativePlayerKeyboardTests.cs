using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E.Tests;

// The native controls live in LibVLCSharp's separate overlay window. After the
// fixture opens, every step here uses physical keys only: Tab must cross into
// and out of that window, and its menus must open, commit and close by keyboard
// without the player's own shortcuts taking their keys.
public sealed class NativePlayerKeyboardTests(ITestOutputHelper output)
{
    private static readonly string[] OverlayControls =
    [
        "Playback options", "Seek video", "Playback volume", "Toggle picture in picture", "Toggle fullscreen",
        "Play or pause", "Skip backward 10 seconds", "Skip forward 30 seconds"
    ];

    [Fact]
    public async Task KeyboardReachesNativeControlsOperatesMenusAndReturnsFocus()
    {
        using var fixture = new AppFixture();
        string directory = Path.Combine(fixture.SandboxPath, "keyboard-media");
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "tones.mkv");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", [
                "-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-t", "180", "-c:v", "mpeg4", "-c:a", "aac",
                "-metadata:s:a:0", "title=Tone 440 Hz", "-metadata:s:a:1", "title=Tone 880 Hz", "-y", video
            ], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
        }
        File.WriteAllText(Path.Combine(directory, "caption.en.srt"),
            "1\n00:00:00,000 --> 00:03:00,000\nKeyboard fixture\nSecond line\n");
        File.WriteAllText(Path.Combine(directory, "library-item.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, WorkKey = "av:fixture:show:native-keyboard", UnitKey = "season:1:episode:1",
            Identity = new AudiovisualIdentity { PrimaryId = new("fixture", "show", "native-keyboard"),
                Kind = AudiovisualMediaKind.Television,
                ContentForm = AudiovisualContentForm.Series, Title = "Native keyboard fixture", Year = 2008 },
            Unit = new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 }, Title = "Native keyboard fixture",
            MediaFile = "tones.mkv", CaptionFiles = new[] { "caption.en.srt" }, SourceProvider = "generated-fixture",
            AudioNotice = "Generated tones; no spoken audio language."
        }));

        IntPtr ownedHandle = new(fixture.MainWindow.Properties.NativeWindowHandle.Value);
        string logPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
        Window Window() => fixture.Automation.FromHandle(ownedHandle).AsWindow();
        AutomationElement? Named(string name) => Window().FindFirstDescendant(cf => cf.ByName(name));
        ComboBox Choice(string name) => Named(name)!.AsComboBox();
        double Position() => Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!
            .Patterns.RangeValue.Pattern.Value.Value;
        bool IsPaused() => Named("Play or pause")?.FindFirstDescendant(cf => cf.ByText("Play")) != null;
        bool IsExpanded(string name) => Named(name)?.Patterns.ExpandCollapse.PatternOrDefault?
            .ExpandCollapseState.Value == ExpandCollapseState.Expanded;
        AutomationElement? Focused()
        {
            try { return fixture.Automation.FocusedElement(); }
            catch (Exception) { return null; }
        }
        string Describe(AutomationElement? element)
        {
            if (element == null) return "<none>";
            try
            {
                return $"{element.Properties.ControlType.ValueOrDefault}:'{element.Properties.Name.ValueOrDefault}'" +
                    $"#{element.Properties.AutomationId.ValueOrDefault}";
            }
            catch (Exception exception) { return $"<unreadable {exception.GetType().Name}>"; }
        }
        string FocusName() => Focused()?.Properties.Name.ValueOrDefault ?? string.Empty;
        var trail = new List<string>();
        void Press(VirtualKeyShort key, VirtualKeyShort? modifier = null)
        {
            if (modifier is { } held && key is VirtualKeyShort.DOWN or VirtualKeyShort.UP)
            {
                // A physical arrow key is extended. FlaUI sends a bare virtual key,
                // which Windows reads under Alt as numeric-keypad Alt-code entry.
                ushort scan = key == VirtualKeyShort.DOWN ? (ushort)0x50 : (ushort)0x48;
                Keyboard.Press(held);
                Keyboard.PressScanCode(scan, true);
                Keyboard.ReleaseScanCode(scan, true);
                Keyboard.Release(held);
            }
            else if (modifier is { } other) Keyboard.TypeSimultaneously(other, key);
            else Keyboard.Type(key);
            Thread.Sleep(300);
            trail.Add($"{(modifier is { } m ? m + "+" : string.Empty)}{key} -> {Describe(Focused())}");
        }
        bool TabUntil(Func<string, bool> reached, bool backwards = false, int limit = 40)
        {
            for (int index = 0; index < limit && !reached(FocusName()); index++)
                Press(VirtualKeyShort.TAB, backwards ? VirtualKeyShort.SHIFT : null);
            return reached(FocusName());
        }
        string Report() => $"Keys: {string.Join(" | ", trail)}\nFocus: {Describe(Focused())}; position={Position():0}; " +
            $"paused={IsPaused()}; options={IsExpanded("Playback options")}\n" +
            string.Join("\n", ReadLog(logPath).Split('\n').Where(line => line.Contains("[Menu]", StringComparison.Ordinal)));

        // Setup through UI Automation only: open the fixture, mute it and pause.
        Named("Player")!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Named("Play or pause") != null, TimeSpan.FromSeconds(3)));
        Named("Playback options")!.Patterns.ExpandCollapse.Pattern.Expand();
        Named("Playback volume")!.Patterns.RangeValue.Pattern.SetValue(0);
        Named("Media URL or local file path")!.AsTextBox().Text = video;
        Named("Open media URL or path")!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Position() >= 1500 && Named("Audio track")?.IsEnabled == true &&
            Named("Subtitle track")?.IsEnabled == true, TimeSpan.FromSeconds(10)));
        Named("Play or pause")!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(3)));
        Named("Playback options")!.Patterns.ExpandCollapse.Pattern.Collapse();
        string playerTab = Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab"))
            .Single(tab => tab.Name.Contains("keyboard", StringComparison.OrdinalIgnoreCase) || tab.Name == "Playback").Name;

        // 1. From the selected player tab, Tab reaches the native controls.
        Window().SetForeground();
        Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab")).Single(tab => tab.Name == playerTab).Focus();
        Thread.Sleep(300);
        Assert.True(TabUntil(name => OverlayControls.Contains(name)),
            $"Tab never reached the native player controls.\n{Report()}");
        output.WriteLine($"Entered the player at {FocusName()}.");

        // 2. Shortcuts work with an overlay button focused; K pauses and resumes.
        Assert.True(TabUntil(name => name == "Skip forward 30 seconds"), $"Transport not reachable.\n{Report()}");
        double pausedAt = Position();
        Press(VirtualKeyShort.KEY_K);
        Assert.True(SpinWait.SpinUntil(() => !IsPaused(), TimeSpan.FromSeconds(3)), $"K did not resume.\n{Report()}");
        Press(VirtualKeyShort.KEY_K);
        Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(3)), $"K did not pause.\n{Report()}");
        Assert.Equal("Skip forward 30 seconds", FocusName());

        // 3. The focused seek slider moves by player steps, not by milliseconds.
        Assert.True(TabUntil(name => name == "Seek video"), $"Seek slider not reachable.\n{Report()}");
        pausedAt = Position();
        Press(VirtualKeyShort.RIGHT);
        Assert.True(SpinWait.SpinUntil(() => Position() >= pausedAt + 25_000, TimeSpan.FromSeconds(3)),
            $"Right on the focused seek slider did not seek forward from {pausedAt:0}.\n{Report()}");
        pausedAt = Position();
        Press(VirtualKeyShort.LEFT);
        Assert.True(SpinWait.SpinUntil(() => Position() <= pausedAt - 8_000, TimeSpan.FromSeconds(3)),
            $"Left on the focused seek slider did not seek back from {pausedAt:0}.\n{Report()}");
        Assert.True(IsPaused(), $"Keyboard seeking resumed playback.\n{Report()}");
        Assert.Equal("Seek video", FocusName());

        // 4. Playback options open by keyboard; a menu opens, commits a choice
        //    with Enter and returns focus to its selector.
        Assert.True(TabUntil(name => name == "Playback options", backwards: true),
            $"Playback options not reachable backwards.\n{Report()}");
        Press(VirtualKeyShort.SPACE);
        Assert.True(SpinWait.SpinUntil(() => IsExpanded("Playback options"), TimeSpan.FromSeconds(2)),
            $"Space did not open Playback options.\n{Report()}");
        Assert.True(TabUntil(name => name == "Subtitle track"), $"Subtitle menu not reachable.\n{Report()}");
        pausedAt = Position();
        Press(VirtualKeyShort.DOWN, VirtualKeyShort.ALT);
        if (!SpinWait.SpinUntil(() => IsExpanded("Subtitle track"), TimeSpan.FromSeconds(2)))
        {
            Press(VirtualKeyShort.F4);
            bool f4 = SpinWait.SpinUntil(() => IsExpanded("Subtitle track"), TimeSpan.FromSeconds(2));
            Assert.Fail($"Alt+Down did not open the subtitle menu; F4 {(f4 ? "did" : "did not either")}.\n{Report()}");
        }
        // Arrow keys inside an open menu belong to the menu, not to seeking.
        Press(VirtualKeyShort.RIGHT);
        Press(VirtualKeyShort.LEFT);
        Assert.True(IsExpanded("Subtitle track"), $"Arrow keys closed the subtitle menu.\n{Report()}");
        Assert.InRange(Position(), pausedAt - 500, pausedAt + 500);
        Press(VirtualKeyShort.HOME);
        Press(VirtualKeyShort.RETURN);
        Assert.True(SpinWait.SpinUntil(() => Choice("Subtitle track").SelectedItem?.Text == "CC Off" &&
            !IsExpanded("Subtitle track"), TimeSpan.FromSeconds(5)),
            $"Enter did not commit CC Off.\n{Report()}\n{ReadLog(logPath)}");
        Assert.True(SpinWait.SpinUntil(() => FocusName() == "Subtitle track", TimeSpan.FromSeconds(2)),
            $"Focus did not return to the subtitle menu after its choice.\n{Report()}");
        Assert.True(IsExpanded("Playback options"), $"Committing a choice closed Playback options.\n{Report()}");
        // The caption change reloads the media and pauses it again after start.
        Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(10)), $"The caption change resumed playback.\n{Report()}");
        Assert.InRange(Position(), pausedAt - 1500, pausedAt + 1500);

        // 5. Escape in an open menu closes only that menu and keeps its choice.
        Press(VirtualKeyShort.DOWN, VirtualKeyShort.ALT);
        Assert.True(SpinWait.SpinUntil(() => IsExpanded("Subtitle track"), TimeSpan.FromSeconds(2)),
            $"Alt+Down did not reopen the subtitle menu.\n{Report()}");
        Press(VirtualKeyShort.END);
        Press(VirtualKeyShort.ESCAPE);
        Assert.True(SpinWait.SpinUntil(() => !IsExpanded("Subtitle track"), TimeSpan.FromSeconds(2)),
            $"Escape did not close the subtitle menu.\n{Report()}");
        Assert.Equal("CC Off", Choice("Subtitle track").SelectedItem?.Text);
        Assert.True(IsExpanded("Playback options"), $"Escape in the menu also closed Playback options.\n{Report()}");
        Assert.True(SpinWait.SpinUntil(() => FocusName() == "Subtitle track", TimeSpan.FromSeconds(2)),
            $"Focus left the subtitle menu after Escape.\n{Report()}");

        // 6. The audio and speed menus work the same way.
        Assert.True(TabUntil(name => name == "Audio track", backwards: true), $"Audio menu not reachable.\n{Report()}");
        Press(VirtualKeyShort.F4);
        Assert.True(SpinWait.SpinUntil(() => IsExpanded("Audio track"), TimeSpan.FromSeconds(2)),
            $"F4 did not open the audio menu.\n{Report()}");
        Press(VirtualKeyShort.END);
        Press(VirtualKeyShort.RETURN);
        Assert.True(SpinWait.SpinUntil(() => Choice("Audio track").SelectedItem?.Text.Contains("Tone 880 Hz",
            StringComparison.Ordinal) == true && !IsExpanded("Audio track"), TimeSpan.FromSeconds(5)),
            $"Enter did not commit the second audio track.\n{Report()}");
        Assert.True(SpinWait.SpinUntil(() => FocusName() == "Audio track", TimeSpan.FromSeconds(2)),
            $"Focus did not return to the audio menu.\n{Report()}");
        Assert.True(TabUntil(name => name == "Playback speed", backwards: true), $"Speed menu not reachable.\n{Report()}");
        Press(VirtualKeyShort.DOWN, VirtualKeyShort.ALT);
        Assert.True(SpinWait.SpinUntil(() => IsExpanded("Playback speed"), TimeSpan.FromSeconds(2)),
            $"Alt+Down did not open the speed menu.\n{Report()}");
        Press(VirtualKeyShort.DOWN);
        Press(VirtualKeyShort.RETURN);
        Assert.True(SpinWait.SpinUntil(() => Choice("Playback speed").SelectedItem?.Text == "1.25x" &&
            !IsExpanded("Playback speed"), TimeSpan.FromSeconds(3)), $"Enter did not commit 1.25x.\n{Report()}");
        Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(10)), $"Menu choices resumed playback.\n{Report()}");
        Assert.InRange(Position(), pausedAt - 1500, pausedAt + 1500);

        // 7. Escape on a closed selector closes Playback options and keeps the
        //    keyboard in the player, where shortcuts still work.
        Press(VirtualKeyShort.ESCAPE);
        Assert.True(SpinWait.SpinUntil(() => !IsExpanded("Playback options"), TimeSpan.FromSeconds(2)),
            $"Escape did not close Playback options.\n{Report()}");
        Assert.True(SpinWait.SpinUntil(() => FocusName() == "Playback options", TimeSpan.FromSeconds(2)),
            $"Focus was not returned to Playback options.\n{Report()}");
        pausedAt = Position();
        Press(VirtualKeyShort.RIGHT);
        Assert.True(SpinWait.SpinUntil(() => Position() >= pausedAt + 25_000, TimeSpan.FromSeconds(3)),
            $"Right did not seek after Playback options closed.\n{Report()}");

        // 8. Tab leaves the player in both directions instead of trapping focus.
        Assert.True(TabUntil(name => !OverlayControls.Contains(name)),
            $"Tab stayed inside the native controls.\n{Report()}");
        string after = FocusName();
        Assert.True(TabUntil(name => OverlayControls.Contains(name), backwards: true),
            $"Shift+Tab did not return to the native controls from '{after}'.\n{Report()}");
        Assert.True(TabUntil(name => !OverlayControls.Contains(name), backwards: true),
            $"Shift+Tab stayed inside the native controls.\n{Report()}");
        Assert.True(IsPaused());
        output.WriteLine(Report());
    }

    [Fact]
    public async Task PlayingControlsStayInTheAutomationTreeAndReturnForPointerAndScreenReaders()
    {
        using var fixture = new AppFixture();
        string directory = Path.Combine(fixture.SandboxPath, "hidden-controls-media");
        Directory.CreateDirectory(directory);
        // A solid colour makes the shown or hidden transport measurable from pixels.
        string video = Path.Combine(directory, "yellow.mkv");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", [
                "-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=yellow:size=640x360:rate=25",
                "-t", "120", "-c:v", "mpeg4", "-y", video
            ], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
        }

        IntPtr ownedHandle = new(fixture.MainWindow.Properties.NativeWindowHandle.Value);
        Window Window() => fixture.Automation.FromHandle(ownedHandle).AsWindow();
        AutomationElement? Named(string name) => Window().FindFirstDescendant(cf => cf.ByName(name));
        double Position() => Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!
            .Patterns.RangeValue.Pattern.Value.Value;
        bool IsPaused() => Named("Play or pause")?.FindFirstDescendant(cf => cf.ByText("Play")) != null;
        System.Drawing.Rectangle seekBounds = default;
        string Shown()
        {
            // The transport panel just above the middle of the seek bar, inside the
            // picture: dark when shown, the yellow video when hidden.
            var sample = new System.Drawing.Rectangle(seekBounds.Left + seekBounds.Width / 2, seekBounds.Top - 4, 4, 2);
            using var capture = FlaUI.Core.Capturing.Capture.Rectangle(sample);
            using var bitmap = new System.Drawing.Bitmap(capture.Bitmap);
            var pixel = bitmap.GetPixel(1, 1);
            return pixel.R < 120 && pixel.G < 120 ? "shown" : pixel.R > 180 && pixel.G > 180 && pixel.B < 90 ? "hidden" : $"unclear {pixel}";
        }
        void PointerAway()
        {
            // The player may fill its display; rest the pointer on the other one.
            var bounds = Window().BoundingRectangle;
            Mouse.MoveTo(new System.Drawing.Point(bounds.Left - 200, bounds.Top + 200));
        }

        Named("Player")!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Named("Play or pause") != null, TimeSpan.FromSeconds(3)));
        Named("Playback options")!.Patterns.ExpandCollapse.Pattern.Expand();
        Named("Playback volume")!.Patterns.RangeValue.Pattern.SetValue(0);
        Named("Media URL or local file path")!.AsTextBox().Text = video;
        Named("Open media URL or path")!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Position() >= 1500, TimeSpan.FromSeconds(10)));
        Named("Playback options")!.Patterns.ExpandCollapse.Pattern.Collapse();
        seekBounds = Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!.BoundingRectangle;
        Assert.Equal("shown", Shown());

        // 1. Idle playback hides the controls from sight, not from screen readers.
        PointerAway();
        Thread.Sleep(5000);
        Assert.False(IsPaused());
        var play = Named("Play or pause");
        Assert.True(play != null, "The transport left the UI Automation tree while playing.");
        Assert.Equal("hidden", Shown());

        // 2. The pointer over the video shows them again.
        var area = Window().BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(area.Left + area.Width / 2, area.Top + area.Height / 2));
        Mouse.MoveBy(12, 8);
        Assert.True(SpinWait.SpinUntil(() => Shown() == "shown", TimeSpan.FromSeconds(2)),
            $"Moving the pointer over the playing video did not show the controls ({Shown()}).");

        // 3. They hide again; a screen reader's Invoke on the hidden button pauses,
        //    and paused controls stay shown.
        PointerAway();
        Assert.True(SpinWait.SpinUntil(() => Shown() == "hidden", TimeSpan.FromSeconds(6)), $"Controls did not hide ({Shown()}).");
        Named("Play or pause")!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(3)));
        Assert.True(SpinWait.SpinUntil(() => Shown() == "shown", TimeSpan.FromSeconds(2)),
            $"Pausing from a screen reader left the controls hidden ({Shown()}).");
        Thread.Sleep(4000);
        Assert.Equal("shown", Shown());
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
