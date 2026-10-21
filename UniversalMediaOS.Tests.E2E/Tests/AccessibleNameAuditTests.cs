using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class AccessibleNameAuditTests
{
    private static readonly string[] Views =
    {
        "Anime", "Manga", "Movies", "Books", "TV Shows", "Cartoons", "Player",
        "VA Detect", "Watch Together", "My List", "Downloads"
    };

    [Fact]
    public void EveryMainViewNamesItsInteractiveControls()
    {
        using var fixture = new AppFixture();
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Title == AppFixture.ExpectedMainWindowTitle);
        var unnamed = new List<string>();
        foreach (string view in Views)
        {
            Window().FindFirstDescendant(cf => cf.ByName(view).And(cf.ByControlType(ControlType.Button)))!.AsButton().Invoke();
            Thread.Sleep(1500);
            // A screen reader announces a control by its UI Automation name; an unnamed one only by its type.
            foreach (var control in Window().FindAllDescendants(cf => cf.ByControlType(ControlType.Button)
                         .Or(cf.ByControlType(ControlType.Edit)).Or(cf.ByControlType(ControlType.ComboBox))
                         .Or(cf.ByControlType(ControlType.CheckBox)).Or(cf.ByControlType(ControlType.Slider))
                         .Or(cf.ByControlType(ControlType.RadioButton)).Or(cf.ByControlType(ControlType.Hyperlink))))
            {
                // Scroll bar and slider track parts take no keyboard focus and are not announced.
                if (control.IsOffscreen || !control.Properties.IsKeyboardFocusable.ValueOrDefault ||
                    !string.IsNullOrWhiteSpace(control.Name)) continue;
                unnamed.Add($"{view}: {control.ControlType} id='{control.AutomationId}' help='{control.HelpText}'");
            }
        }
        // Identical lines (several unnamed fields without tooltips) are counted, not merged.
        Assert.True(unnamed.Count == 0, "Unnamed controls:" + Environment.NewLine + string.Join(Environment.NewLine,
            unnamed.GroupBy(line => line).Select(group => group.Count() > 1 ? $"{group.Key} x{group.Count()}" : group.Key)));
    }
}
