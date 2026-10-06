using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.WPF.Helpers;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class LocalizationSelectionTests
{
    [Fact]
    public void SelectedLanguagePresenterChangesWithSelectionAndLocalization()
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            try
            {
                LocalizationRuntime.SetLanguage("English");
                var resources = new ResourceDictionary();
                foreach (string name in new[] { "Tokens", "Controls" })
                    resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                        new Uri($"/UniversalMediaOS.WPF;component/Themes/{name}.xaml", UriKind.Relative)));
                var combo = (ComboBox)XamlReader.Parse("""
                    <ComboBox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:helpers="clr-namespace:UniversalMediaOS.WPF.Helpers;assembly=UniversalMediaOS.WPF"
                     SelectedIndex="0">
                      <ComboBoxItem Content="{helpers:Loc English}" Tag="English"/>
                      <ComboBoxItem Content="{helpers:Loc Arabic}" Tag="Arabic"/>
                    </ComboBox>
                    """);
                combo.Resources = resources;
                combo.Style = (Style)resources["AppComboBox"];
                bool arabic = false;
                LocalizationRuntime.EnableAutoApply(combo, () => arabic);
                void Layout()
                {
                    combo.Measure(new Size(500, 80));
                    combo.Arrange(new Rect(combo.DesiredSize));
                    combo.UpdateLayout();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                }
                string Label() => Descendants((ContentPresenter)combo.Template.FindName("ContentSite", combo))
                    .OfType<TextBlock>().Single().Text;
                Layout();
                combo.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal("English", Label());
                combo.SelectedIndex = 1;
                arabic = true;
                LocalizationRuntime.SetLanguage("Arabic");
                Layout();
                combo.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal("العربية", Label());
                combo.SelectedIndex = 0;
                arabic = false;
                LocalizationRuntime.SetLanguage("English");
                Layout();
                Assert.Equal("English", Label());
            }
            finally { LocalizationRuntime.SetLanguage("English"); }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [Theory]
    [InlineData("English", "Arabic", "العربية")]
    [InlineData("English (downloaded)", "CC Off", "إيقاف الترجمة")]
    public void ReusedUnboundLabelKeepsItsCurrentSelection(string first, string second, string translated)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            try
            {
                LocalizationRuntime.SetLanguage("English");
                var label = new TextBlock { Text = first };
                bool arabic = true;
                LocalizationRuntime.EnableAutoApply(label, () => arabic);
                label.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                // WPF reuses this presenter when a dropdown selection changes.
                label.Text = second;
                label.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(translated, label.Text);
                label.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(translated, label.Text);
                arabic = false;
                label.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(second, label.Text);
            }
            finally { LocalizationRuntime.SetLanguage("English"); }
        });
    }

    [Fact]
    public void LiveJobStateAndLanguageChangesRetainTheMultiBinding()
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            try
            {
                LocalizationRuntime.SetLanguage("English");
                var job = new DownloadQueueJob { Status = DownloadJobStatus.Paused };
                var label = (TextBlock)XamlReader.Parse("""
                    <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:helpers="clr-namespace:UniversalMediaOS.WPF.Helpers;assembly=UniversalMediaOS.WPF"
                     Text="{helpers:LocValue StatusText}" ToolTip="{helpers:LocValue StatusMessage}" />
                    """);
                label.DataContext = job;
                void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Flush();
                LocalizationRuntime.EnableAutoApply(label, () => true);
                label.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.True(BindingOperations.IsDataBound(label, TextBlock.TextProperty));
                Assert.True(BindingOperations.IsDataBound(label, FrameworkElement.ToolTipProperty));
                Assert.Equal("Paused", label.Text);
                LocalizationRuntime.SetLanguage("Arabic");
                Flush();
                Assert.Equal("متوقف مؤقتًا", label.Text);
                job.Status = DownloadJobStatus.Completed;
                job.StatusMessage = "Download and validation completed";
                Flush();
                Assert.Equal("مكتمل", label.Text);
                Assert.Equal("اكتمل التنزيل والتحقق", label.ToolTip);
                job.StatusMessage = "Original provider error: fixture.mkv";
                Flush();
                Assert.Equal(job.StatusMessage, label.ToolTip);
                LocalizationRuntime.SetLanguage("English");
                Flush();
                Assert.Equal("Completed", label.Text);
                Assert.True(BindingOperations.IsDataBound(label, TextBlock.TextProperty));
            }
            finally { LocalizationRuntime.SetLanguage("English"); }
        });
    }
}
