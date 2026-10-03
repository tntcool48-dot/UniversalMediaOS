using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using UniversalMediaOS.WPF.Controls;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class RecoveryLayoutTests
{
    [Theory]
    [InlineData(1200, 0)]
    [InlineData(700, 34)]
    public void NavigationSeparatesGroupsAndWrapsUtilities(double width, double utilityTop)
    {
        RunSta(() =>
        {
            var panel = new GroupedNavigationPanel();
            var media = new Border { Width = 400, Height = 30 };
            var utilities = new Border { Width = 350, Height = 30 };
            panel.Children.Add(media);
            panel.Children.Add(utilities);
            panel.Measure(new Size(width, 200));
            panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
            Point position = utilities.TranslatePoint(new Point(), panel);
            Assert.Equal(width - 350, position.X);
            Assert.Equal(utilityTop, position.Y);
            Assert.Equal(0, media.TranslatePoint(new Point(), panel).X);
        });
    }

    [Theory]
    [InlineData(72)]
    [InlineData(88)]
    public void CompactDropdownHonorsWidthAndSelectedLabel(double width)
    {
        RunSta(() =>
        {
            var resources = new ResourceDictionary();
            foreach (string name in new[] { "Tokens", "Controls" })
                resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                    new Uri($"/UniversalMediaOS.WPF;component/Themes/{name}.xaml", UriKind.Relative)));
            var combo = new ComboBox
            {
                Resources = resources,
                Style = (Style)resources["CompactComboBox"],
                Height = 34,
                Width = width,
                DisplayMemberPath = "Label",
                ItemsSource = new[] { new { Label = "1x" }, new { Label = "2x" } },
                SelectedIndex = 0
            };
            combo.Measure(new Size(400, 100));
            combo.Arrange(new Rect(combo.DesiredSize));
            combo.UpdateLayout();
            Assert.Equal(width, combo.ActualWidth);
            Assert.Equal(34, combo.ActualHeight);
            var chrome = (Border)combo.Template.FindName("Chrome", combo);
            Assert.Equal(width, chrome.ActualWidth);
            Assert.Equal(combo.ActualHeight, chrome.ActualHeight);
            Assert.NotNull(combo.Template.FindName("PART_Popup", combo));
            var presenter = (ContentPresenter)combo.Template.FindName("ContentSite", combo);
            Assert.NotNull(presenter.ContentTemplateSelector);
            Assert.Same(combo.SelectedItem, presenter.Content);
        });
    }

    internal static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF layout did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
