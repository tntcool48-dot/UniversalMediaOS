using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class SearchCompactLayoutTests
{
    [Fact]
    public void MinimumWindowAtMaximumZoomKeepsResultsScrollableAndDetailsReachable()
    {
        using var fixture = new AppFixture();
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        Window().Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        Window().Patterns.Transform.Pattern.Resize(900, 560);
        Window().FindFirstDescendant(cf => cf.ByAutomationId("OpenSettings"))!.AsButton().Invoke();
        for (int zoom = 100; zoom < 140; zoom += 5)
        {
            Window().FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Single(element => element.Properties.HelpText.ValueOrDefault == "Zoom in by 5 percent.")
                .AsButton().Invoke();
        }
        Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf => cf.ByText("140%")) != null,
            TimeSpan.FromSeconds(3)));
        Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab")).First().AsButton().Invoke();
        Window().FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))!.AsTextBox().Text = "Frieren";
        Window().FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf => cf.ByName("View Details")) != null,
            TimeSpan.FromSeconds(5)));

        var results = Window().FindFirstDescendant(cf => cf.ByAutomationId("ResultsList"))!;
        Assert.True(results.BoundingRectangle.Height >= 120,
            $"The compact result viewport must remain usable; actual height {results.BoundingRectangle.Height}.");
        // The card is taller than this viewport. Pixel scrolling must expose its
        // title and badges before the user opens the correct item.
        results.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var title = results.FindFirstDescendant(cf => cf.ByText("Mock English"));
            return title != null && !title.IsOffscreen &&
                title.BoundingRectangle.Bottom <= results.BoundingRectangle.Bottom;
        }, TimeSpan.FromSeconds(3)), "Scrolling must reveal the result's title.");
        results.FindFirstDescendant(cf => cf.ByName("View Details"))!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab"))
            .Any(element => element.Name.Contains("Mock English", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)),
            "Opening the compact card must select the same item's Details tab.");
        Assert.False(fixture.App.HasExited);
    }
}
