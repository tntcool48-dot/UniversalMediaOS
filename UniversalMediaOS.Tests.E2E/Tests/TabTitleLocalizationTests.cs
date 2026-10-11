using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class TabTitleLocalizationTests
{
    [Theory]
    [InlineData("Settings", "Settings", true, "الإعدادات")]
    [InlineData("Anime", "Anime", true, "أنمي")]
    [InlineData("Playback", "Playback", true, "التشغيل")]
    [InlineData("Settings", "Detail", false, "Settings")]
    [InlineData("Playback", "Playback", false, "Playback")]
    [InlineData("Movies", "Movies", false, "Movies")]
    public void ActualTabLabelLocalizesOnlyAppOwnedTitlesAndRestoresEnglish(
        string title, string kind, bool localizeTitle, string arabicLabel)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            try
            {
                LocalizationRuntime.SetLanguage("English");
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root != null && !File.Exists(Path.Combine(root.FullName, "UniversalMediaOS.sln")))
                    root = root.Parent;
                Assert.NotNull(root);
                var document = XDocument.Load(Path.Combine(root.FullName, "UniversalMediaOS.WPF", "MainWindow.xaml"));
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var sourceStyle = new XElement(Assert.Single(document.Descendants(), element =>
                    (string?)element.Attribute(x + "Key") == "LocalizedTabTitleText"));
                sourceStyle.Attribute(x + "Key")!.Remove();
                sourceStyle.SetAttributeValue(XNamespace.Xmlns + "helpers",
                    "clr-namespace:UniversalMediaOS.WPF.Helpers;assembly=UniversalMediaOS.WPF");
                var tab = new MediaTabViewModel(title, kind, "", "", new TabContent(), localizeTitle: localizeTitle);
                var label = new TextBlock { Style = (Style)XamlReader.Parse(sourceStyle.ToString()), DataContext = tab };
                void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Flush();
                Assert.Equal(title, label.Text);
                LocalizationRuntime.SetLanguage("Arabic");
                Flush();
                Assert.Equal(arabicLabel, label.Text);
                Assert.Equal(title, tab.Title);
                Assert.Equal(kind, tab.Kind);
                Assert.True(BindingOperations.IsDataBound(label, TextBlock.TextProperty));
                LocalizationRuntime.SetLanguage("English");
                Flush();
                Assert.Equal(title, label.Text);
            }
            finally { LocalizationRuntime.SetLanguage("English"); }
        });
    }

    private sealed class TabContent : ObservableObject { }
}
