using System.IO;
using FlaUI.Core.AutomationElements;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

// Chrome transitions recreate automation elements. Keep their window handles
// separate from the shared navigation fixture used by the rest of the suite.
public sealed class PlaybackPresentationTests : IDisposable
{
    private readonly AppFixture fixture = new();
    public void Dispose() => fixture.Dispose();

    [Fact]
    public void OwnerWindowShortcutsWorkAfterTabFocusAndEscapeRestoresWhileEditingSource()
    {
        CurrentWindow().FindFirstDescendant(cf => cf.ByName("Player"))!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => FindButton("Toggle fullscreen") != null, TimeSpan.FromSeconds(3)));
        string logPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
        int offset = ReadLog(logPath).Length;
        // Reproduce owner activation restoring focus outside the native overlay.
        CurrentWindow().SetForeground();
        CurrentWindow().FindAllDescendants(cf => cf.ByAutomationId("SelectTab"))
            .Single(tab => tab.Name == "Playback").Focus();
        FlaUI.Core.Input.Keyboard.Type("f");
        Assert.True(SpinWait.SpinUntil(() => ReadLog(logPath)[offset..].Contains("Entered fullscreen"), TimeSpan.FromSeconds(3)));

        CurrentWindow().FindFirstDescendant(cf => cf.ByName("Playback options"))!
            .Patterns.ExpandCollapse.Pattern.Expand();
        var source = CurrentWindow().FindFirstDescendant(cf => cf.ByName("Media URL or local file path"))!.AsTextBox();
        source.Focus();
        FlaUI.Core.Input.Keyboard.Type("f");
        Assert.True(SpinWait.SpinUntil(() => source.Text == "f", TimeSpan.FromSeconds(2)));
        Assert.Single(ReadLog(logPath)[offset..].Split("Entered fullscreen").Skip(1));
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Assert.True(SpinWait.SpinUntil(() => ReadLog(logPath)[offset..].Contains("with Escape"), TimeSpan.FromSeconds(3)));
        Assert.False(FindButton("Player")!.IsOffscreen);
        Assert.DoesNotContain("disposed on unload", ReadLog(logPath)[offset..]);
    }

    [Theory]
    [InlineData("Toggle picture in picture", "Entered picture-in-picture")]
    [InlineData("Toggle fullscreen", "Entered fullscreen")]
    public void PresentationKeepsThePlayerVisibleAndOnlyDisposesItWhenClosed(string toggle, string enteredLog)
    {
        CurrentWindow().FindFirstDescendant(cf => cf.ByName("Player"))!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => FindButton(toggle) != null, TimeSpan.FromSeconds(3)));
        string logPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
        int logOffset = ReadLog(logPath).Length;
        try
        {
            FindButton(toggle)!.Invoke();
            Assert.True(SpinWait.SpinUntil(() => ReadLog(logPath)[logOffset..].Contains(enteredLog), TimeSpan.FromSeconds(3)));
            Thread.Sleep(300); // Let the window template's Unloaded/Loaded pair settle.
            var play = FindButton("Play or pause");
            Assert.NotNull(play);
            Assert.False(play.IsOffscreen);
            Assert.True(CurrentWindow().BoundingRectangle.IntersectsWith(play.BoundingRectangle));
            Assert.DoesNotContain("disposed on unload", ReadLog(logPath)[logOffset..]);

            // Native controls may auto-hide while UI Automation reads the
            // window. Reveal them as a user would before toggling back.
            FlaUI.Core.Input.Mouse.MoveTo(play.BoundingRectangle.Location);
            Assert.True(SpinWait.SpinUntil(() => FindButton(toggle) != null, TimeSpan.FromSeconds(3)));
            FindButton(toggle)!.Invoke();
            Thread.Sleep(300);
            Assert.False(FindButton("Player")!.IsOffscreen);
            Assert.DoesNotContain("disposed on unload", ReadLog(logPath)[logOffset..]);
        }
        finally
        {
            CurrentWindow().FindAllDescendants(cf => cf.ByAutomationId("CloseTab")).LastOrDefault()?.AsButton().Invoke();
        }
        Assert.True(SpinWait.SpinUntil(() => ReadLog(logPath)[logOffset..].Contains("Disposing PlaybackViewModel"), TimeSpan.FromSeconds(3)));
        string closure = ReadLog(logPath)[logOffset..];
        int browserDisposed = closure.IndexOf("disposed on unload", StringComparison.Ordinal);
        int nativeDisposed = closure.IndexOf("Disposing PlaybackViewModel", StringComparison.Ordinal);
        Assert.True(browserDisposed >= 0 && browserDisposed < nativeDisposed, closure);
    }

    private Window CurrentWindow() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
        .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
    private Button? FindButton(string name) => CurrentWindow().FindFirstDescendant(cf => cf.ByName(name))?.AsButton();
    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
