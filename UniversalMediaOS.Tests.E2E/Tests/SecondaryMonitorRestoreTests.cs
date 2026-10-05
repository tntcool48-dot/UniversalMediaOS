using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class SecondaryMonitorRestoreTests(ITestOutputHelper output)
{
    [Fact]
    public void MaximizedStartupRestoresAndReturnsWithinItsRequestedMonitor()
    {
        using var fixture = new AppFixture();
        var window = fixture.MainWindow;
        var pattern = window.Patterns.Window.Pattern;
        Assert.True(SpinWait.SpinUntil(() => pattern.WindowVisualState.Value == WindowVisualState.Maximized,
            TimeSpan.FromSeconds(3)), "The default secondary-monitor profile should start maximized.");
        IntPtr handle = new(window.Properties.NativeWindowHandle.Value);
        IntPtr startupMonitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Assert.True(GetMonitorInfo(startupMonitor, ref info));
        bool hasSecondary = false;
        Assert.True(EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var candidate = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref candidate) && (candidate.Flags & 1) == 0) hasSecondary = true;
            return true;
        }, IntPtr.Zero));
        if (hasSecondary) Assert.Equal(0, info.Flags & 1);
        output.WriteLine($"Startup monitor work area: {info.Work.Left},{info.Work.Top} to {info.Work.Right},{info.Work.Bottom}; secondary available={hasSecondary}");

        void AssertRestoredOnStartupMonitor()
        {
            Assert.True(SpinWait.SpinUntil(() => pattern.WindowVisualState.Value == WindowVisualState.Normal,
                TimeSpan.FromSeconds(3)));
            Assert.Equal(startupMonitor, MonitorFromWindow(handle, 2));
            var bounds = window.BoundingRectangle;
            output.WriteLine($"Restored bounds: {bounds}");
            if (hasSecondary)
            {
                Assert.InRange(bounds.Left, info.Work.Left - 1, info.Work.Right);
                Assert.InRange(bounds.Top, info.Work.Top - 1, info.Work.Bottom);
                Assert.InRange(bounds.Right, info.Work.Left, info.Work.Right + 1);
                Assert.InRange(bounds.Bottom, info.Work.Top, info.Work.Bottom + 1);
            }
        }

        // Windows/UIA restore exercises the same path as desktop activation.
        pattern.SetWindowVisualState(WindowVisualState.Normal);
        AssertRestoredOnStartupMonitor();
        // The custom title-bar control must preserve the same restore rectangle.
        window.FindFirstDescendant(cf => cf.ByName("Maximize or Restore"))!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => pattern.WindowVisualState.Value == WindowVisualState.Maximized,
            TimeSpan.FromSeconds(3)));
        window.FindFirstDescendant(cf => cf.ByName("Maximize or Restore"))!.AsButton().Invoke();
        AssertRestoredOnStartupMonitor();
        pattern.SetWindowVisualState(WindowVisualState.Minimized);
        Assert.True(SpinWait.SpinUntil(() => pattern.WindowVisualState.Value == WindowVisualState.Minimized,
            TimeSpan.FromSeconds(3)));
        pattern.SetWindowVisualState(WindowVisualState.Normal);
        AssertRestoredOnStartupMonitor();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rect, MonitorCallback callback, IntPtr data);
}
