using System.IO;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class RetainedPlayerTabsTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(140)]
    public void WorkspaceZoomDoesNotScaleTheRetainedNativeOverlay(int zoom)
    {
        using var fixture = new AppFixture();
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        Button Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(name))!.AsButton();
        Window().Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        Window().Patterns.Transform.Pattern.Resize(900, 560);
        Button("Player").Invoke();
        Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf => cf.ByName("Play or pause")) != null,
            TimeSpan.FromSeconds(4)));
        Window().FindFirstDescendant(cf => cf.ByName("Playback options"))!.Patterns.ExpandCollapse.Pattern.Expand();
        Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf => cf.ByName("Playback speed")) != null,
            TimeSpan.FromSeconds(3)));
        double originalHeight = Button("Play or pause").BoundingRectangle.Height;
        double originalSpeedHeight = Window().FindFirstDescendant(cf => cf.ByName("Playback speed"))!.BoundingRectangle.Height;

        Window().FindFirstDescendant(cf => cf.ByAutomationId("OpenSettings"))!.AsButton().Invoke();
        string help = zoom > 100 ? "Zoom in by 5 percent." : "Zoom out by 5 percent.";
        for (int value = 100; value != zoom; value += zoom > 100 ? 5 : -5)
        {
            Window().FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Single(element => element.Properties.HelpText.ValueOrDefault == help).AsButton().Invoke();
        }
        Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf => cf.ByText($"{zoom}%")) != null,
            TimeSpan.FromSeconds(3)));
        Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab"))
            .Single(element => element.Name == "Playback").AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf => cf.ByName("Play or pause")) != null,
            TimeSpan.FromSeconds(3)));
        Thread.Sleep(300); // Allow the foreground HWND to finish its own layout.
        Assert.InRange(Button("Play or pause").BoundingRectangle.Height, originalHeight - 1, originalHeight + 1);
        Assert.InRange(Window().FindFirstDescendant(cf => cf.ByName("Playback speed"))!.BoundingRectangle.Height,
            originalSpeedHeight - 1, originalSpeedHeight + 1);
        Assert.False(fixture.App.HasExited);
    }

    [Fact]
    public void TabSwitchesRetainDistinctNativeHandlesAndCloseOnlyTheRemovedView()
    {
        using var fixture = new AppFixture();
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Title == AppFixture.ExpectedMainWindowTitle);
        Button[] Players() => Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab"))
            .Where(element => element.Name == "Playback").Select(element => element.AsButton()).ToArray();
        string logPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
        string Log()
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        string[] Handles(string log) => Regex.Matches(log, @"Native video surface attached hwnd=([0-9A-F]+)\.")
            .Select(match => match.Groups[1].Value).Where(handle => handle != "0").ToArray();

        Window().FindFirstDescendant(cf => cf.ByName("Player"))!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Handles(Log()).Length > 0, TimeSpan.FromSeconds(4)));
        string firstHandle = Handles(Log()).Last();
        Window().FindFirstDescendant(cf => cf.ByName("Player"))!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Players().Length == 2 && Handles(Log()).Distinct().Count() == 2,
            TimeSpan.FromSeconds(4)));
        string secondHandle = Handles(Log()).Last();
        Assert.NotEqual(firstHandle, secondHandle);
        int offset = Log().Length;

        for (int i = 0; i < 20; i++)
        {
            Players()[i % 2].Invoke();
            string expected = i % 2 == 0 ? firstHandle : secondHandle;
            Assert.True(SpinWait.SpinUntil(() => Handles(Log()).Last() == expected, TimeSpan.FromSeconds(3)));
        }
        // Exercise a different data template, then return to the first player.
        Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab")).First().AsButton().Invoke();
        Players()[0].Invoke();
        Assert.True(SpinWait.SpinUntil(() => Handles(Log()).Last() == firstHandle, TimeSpan.FromSeconds(3)));
        string switches = Log()[offset..];
        Assert.DoesNotContain("disposed on unload", switches);
        Assert.DoesNotContain("Native video surface detached", switches);
        Assert.Equal(2, Handles(Log()).Distinct().Count());

        // Close the background player, preserving the currently visible one.
        Window().FindAllDescendants(cf => cf.ByAutomationId("CloseTab")).Last().AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Log()[offset..].Contains("Disposing PlaybackViewModel"),
            TimeSpan.FromSeconds(3)));
        Assert.Single(Players());
        Assert.Equal(firstHandle, Handles(Log()).Last());
        Assert.Single(Regex.Matches(Log()[offset..], "disposed on unload"));
        Assert.NotNull(Window().FindFirstDescendant(cf => cf.ByName("Play or pause")));
        Assert.False(fixture.App.HasExited);
    }
}
