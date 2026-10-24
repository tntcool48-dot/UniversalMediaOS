using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E.Tests;

// LibVLCSharp draws the native controls in a separate top-level window that it
// positions in the owner's DIPs. Each display (and each display's scaling) must
// keep that window over the video area, at the owner's DPI, with a paused frame
// rather than a white or empty surface. Run unchanged at mixed Windows scaling.
public sealed class NativePlayerDisplayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NativeOverlayFollowsTheVideoAreaAcrossDisplaysAndPresentationModes()
    {
        using var fixture = new AppFixture();
        string directory = Path.Combine(fixture.SandboxPath, "display-media");
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "bars.mkv");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", [
                "-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=25",
                "-t", "120", "-c:v", "mpeg4", "-y", video
            ], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
        }
        File.WriteAllText(Path.Combine(directory, "library-item.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, WorkKey = "av:fixture:show:native-display", UnitKey = "season:1:episode:1",
            Identity = new AudiovisualIdentity { PrimaryId = new("fixture", "show", "native-display"),
                Kind = AudiovisualMediaKind.Television,
                ContentForm = AudiovisualContentForm.Series, Title = "Native display fixture", Year = 2008 },
            Unit = new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 }, Title = "Native display fixture",
            MediaFile = "bars.mkv", SourceProvider = "generated-fixture", AudioNotice = "Generated video without audio."
        }));

        // Physical pixels for other processes' windows on any display.
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr owner = new(fixture.MainWindow.Properties.NativeWindowHandle.Value);
        Window Window() => fixture.Automation.FromHandle(owner).AsWindow();
        AutomationElement? Named(string name) => Window().FindFirstDescendant(cf => cf.ByName(name));
        double Position() => Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!
            .Patterns.RangeValue.Pattern.Value.Value;
        bool IsPaused() => Named("Play or pause")?.FindFirstDescendant(cf => cf.ByText("Play")) != null;
        var monitors = Monitors();
        output.WriteLine("Displays: " + string.Join("; ", monitors.Select(monitor =>
            $"{monitor.Device} {monitor.Bounds} {monitor.Dpi * 100 / 96}%")));

        Named("Player")!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Named("Play or pause") != null, TimeSpan.FromSeconds(3)));
        Named("Playback options")!.Patterns.ExpandCollapse.Pattern.Expand();
        Named("Playback volume")!.Patterns.RangeValue.Pattern.SetValue(0);
        Named("Media URL or local file path")!.AsTextBox().Text = video;
        Named("Open media URL or path")!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Position() >= 1500, TimeSpan.FromSeconds(10)));
        Named("Play or pause")!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(3)));
        Named("Playback options")!.Patterns.ExpandCollapse.Pattern.Collapse();
        double pausedAt = Position();

        IntPtr Overlay() => TopLevelWindows(fixture.App.ProcessId).Single(window =>
            window.Title == "LibVLCSharp.WPF" && IsWindowVisible(window.Handle)).Handle;
        string Check(string phase, Rectangle expectedVideo)
        {
            IntPtr overlay = Overlay();
            Rectangle overlayBounds = WindowBounds(overlay);
            uint ownerDpi = GetDpiForWindow(owner), overlayDpi = GetDpiForWindow(overlay);
            var monitor = monitors.Single(display => display.Handle == MonitorFromWindow(owner, 2));
            Rectangle play = Named("Play or pause")!.BoundingRectangle;
            Rectangle seek = Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!.BoundingRectangle;
            string state = $"{phase}: display {monitor.Device} {monitor.Dpi * 100 / 96}%, owner {WindowBounds(owner)} " +
                $"at {ownerDpi} dpi, overlay {overlayBounds} at {overlayDpi} dpi, expected video {expectedVideo}, " +
                $"play {play}, seek {seek}, position {Position():0}, paused {IsPaused()}";
            output.WriteLine(state);
            Assert.Equal(ownerDpi, overlayDpi);
            Assert.Equal(monitor.Dpi, ownerDpi);
            // One device pixel of rounding on each edge.
            Assert.True(Math.Abs(overlayBounds.Left - expectedVideo.Left) <= 1 &&
                Math.Abs(overlayBounds.Right - expectedVideo.Right) <= 1 &&
                Math.Abs(overlayBounds.Top - expectedVideo.Top) <= 1 &&
                Math.Abs(overlayBounds.Bottom - expectedVideo.Bottom) <= 1, state);
            Assert.True(overlayBounds.Contains(play) && overlayBounds.Contains(seek), state);
            Assert.True(monitor.Bounds.Contains(play) && monitor.Bounds.Contains(seek), state);
            var frame = FrameStatistics(overlayBounds, play, seek);
            output.WriteLine($"{phase}: frame white {frame.White:P1}, dark {frame.Dark:P1}, colours {frame.Colours}");
            Assert.True(frame.White < 0.5 && frame.Dark < 0.9 && frame.Colours >= 6,
                $"{state}\nThe paused video area is not a rendered frame: white {frame.White:P1}, dark {frame.Dark:P1}, colours {frame.Colours}.");
            Assert.True(IsPaused(), state);
            Assert.InRange(Position(), pausedAt - 500, pausedAt + 500);
            return state;
        }
        // LibVLCSharp's native video host: a visible "Static" child of the owner.
        Rectangle WindowedVideo() => WindowBounds(Assert.Single(ChildWindows(owner), child =>
            child.Class == "Static" && IsWindowVisible(child.Handle)).Handle);
        void Settle()
        {
            Thread.Sleep(1500);
            Named("Play or pause");
        }
        void MoveTo(MonitorInfo monitor)
        {
            Rectangle work = monitor.Work;
            Rectangle current = WindowBounds(owner);
            int width = Math.Min(current.Width, work.Width - 40), height = Math.Min(current.Height, work.Height - 40);
            Assert.True(SetWindowPos(owner, IntPtr.Zero, work.Left + (work.Width - width) / 2,
                work.Top + (work.Height - height) / 2, width, height, 0x0014));
            Settle();
        }

        var start = monitors.Single(display => display.Handle == MonitorFromWindow(owner, 2));
        Settle();
        Check("windowed on start display", WindowedVideo());
        foreach (var other in monitors.Where(display => display.Handle != start.Handle))
        {
            MoveTo(other);
            Check($"moved to {other.Device}", WindowedVideo());
        }
        MoveTo(start);
        Check("returned to start display", WindowedVideo());

        Named("Toggle fullscreen")!.AsButton().Invoke();
        Settle();
        Check("fullscreen", start.Bounds);
        Window().SetForeground();
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Settle();
        Check("after fullscreen", WindowedVideo());

        Named("Toggle picture in picture")!.AsButton().Invoke();
        Settle();
        Check("picture in picture", WindowedVideo());
        Window().SetForeground();
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Settle();
        Check("after picture in picture", WindowedVideo());
    }

    private static (double White, double Dark, int Colours) FrameStatistics(Rectangle area, params Rectangle[] controls)
    {
        // Sample the video above the transport, away from the control panels.
        Rectangle sample = Rectangle.FromLTRB(area.Left + area.Width / 8, area.Top + area.Height / 6,
            area.Right - area.Width / 8, Math.Min(area.Bottom - area.Height / 3, controls.Min(control => control.Top) - 8));
        using var capture = Capture.Rectangle(sample);
        using var bitmap = new Bitmap(capture.Bitmap);
        int white = 0, dark = 0, total = 0;
        var colours = new HashSet<int>();
        for (int y = 0; y < bitmap.Height; y += 4)
        for (int x = 0; x < bitmap.Width; x += 4)
        {
            Color pixel = bitmap.GetPixel(x, y);
            total++;
            if (pixel.R > 235 && pixel.G > 235 && pixel.B > 235) white++;
            if (pixel.R < 20 && pixel.G < 20 && pixel.B < 20) dark++;
            colours.Add((pixel.R / 64) << 4 | (pixel.G / 64) << 2 | pixel.B / 64);
        }
        return (white / (double)total, dark / (double)total, colours.Count);
    }

    private sealed record MonitorInfo(IntPtr Handle, string Device, Rectangle Bounds, Rectangle Work, uint Dpi);

    private static List<MonitorInfo> Monitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref NativeRect _, IntPtr _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            GetMonitorInfo(handle, ref info);
            GetDpiForMonitor(handle, 0, out uint dpi, out _);
            monitors.Add(new MonitorInfo(handle, info.Device, info.Monitor.ToRectangle(), info.Work.ToRectangle(), dpi));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    private static List<(IntPtr Handle, string Title)> TopLevelWindows(int processId)
    {
        var windows = new List<(IntPtr, string)>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner == processId)
            {
                var title = new StringBuilder(256);
                GetWindowText(handle, title, title.Capacity);
                windows.Add((handle, title.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static Rectangle WindowBounds(IntPtr window)
    {
        Assert.True(GetWindowRect(window, out NativeRect bounds));
        return bounds.ToRectangle();
    }

    private static List<(IntPtr Handle, string Class)> ChildWindows(IntPtr parent)
    {
        var children = new List<(IntPtr, string)>();
        EnumChildWindows(parent, (handle, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(handle, name, name.Capacity);
            children.Add((handle, name.ToString()));
            return true;
        }, IntPtr.Zero);
        return children;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref NativeRect bounds, IntPtr data);
    private delegate bool WindowEnumProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
