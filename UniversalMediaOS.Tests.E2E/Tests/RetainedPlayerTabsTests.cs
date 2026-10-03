using System.IO;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class RetainedPlayerTabsTests
{
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
