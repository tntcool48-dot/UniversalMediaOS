using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using UniversalMediaOS.WPF.Helpers;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class ControlThemeRenderTests
{
    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void SettingsNavigationShowsTheWholeProviderLabelWithinItsButton(FlowDirection direction)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            var resources = LoadResources(false);
            var button = new Button { Resources = resources, Style = (Style)resources["PremiumNavButton"],
                Content = "Providers & Scrapers", Tag = "\uE8A5", Width = 180, FlowDirection = direction };
            button.Measure(new Size(180, 200));
            button.Arrange(new Rect(button.DesiredSize));
            button.UpdateLayout();
            var text = Descendants(button).OfType<TextBlock>().Single(element => element.Text == "Providers & Scrapers");
            Assert.True(text.ActualHeight >= text.FontSize * 1.75, "The long provider label must fit as complete wrapped lines.");
            Point start = text.TranslatePoint(new Point(), button);
            Assert.InRange(start.X, 0, button.ActualWidth - text.ActualWidth);
            Assert.InRange(start.Y, 0, button.ActualHeight - text.ActualHeight);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnimeTagChipsHaveReadableTextAfterPaletteSwitch(bool dark)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            using var stream = typeof(ControlThemeRenderTests).Assembly.GetManifestResourceStream("SearchView.xaml")!;
            var view = XDocument.Load(stream);
            XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var style = view.Root!.Element(ui + "UserControl.Resources")!.Elements(ui + "Style")
                .Single(element => (string?)element.Attribute(x + "Key") == "TagToggleButtonStyle");
            var dictionary = new XElement(ui + "ResourceDictionary",
                new XElement(ui + "ResourceDictionary.MergedDictionaries", new[] { "Tokens", "Controls" }.Select(name =>
                    new XElement(ui + "ResourceDictionary", new XAttribute("Source", $"/UniversalMediaOS.WPF;component/Themes/{name}.xaml")))),
                new XElement(style));
            var resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            ThemeRuntime.ApplyPalette(resources, !dark, "Teal");
            var chip = new ToggleButton { Resources = resources, Style = (Style)resources["TagToggleButtonStyle"], Content = "Adventure" };
            chip.Measure(new Size(180, 60));
            chip.Arrange(new Rect(chip.DesiredSize));
            chip.UpdateLayout();
            ThemeRuntime.ApplyPalette(resources, dark, "Teal");
            foreach (bool selected in new[] { false, true })
            {
                chip.IsChecked = selected;
                chip.UpdateLayout();
                var chrome = (Border)chip.Template.FindName("Chrome", chip);
                Color surface = ((SolidColorBrush)chrome.Background).Color;
                Color baseColor = ColorOf(resources, "BgApp");
                double alpha = surface.A / 255d;
                Color composite = Color.FromRgb((byte)(surface.R * alpha + baseColor.R * (1 - alpha)),
                    (byte)(surface.G * alpha + baseColor.G * (1 - alpha)),
                    (byte)(surface.B * alpha + baseColor.B * (1 - alpha)));
                Assert.True(Contrast(((SolidColorBrush)chip.Foreground).Color, composite) >= 4.5,
                    $"Unreadable anime tag: dark={dark}, selected={selected}");
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TabChromeAndDropdownsRenderWithThemeColors(bool dark)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            var resources = LoadResources(dark);
            var rows = new StackPanel { Margin = new Thickness(20) };
            var root = new Border
            {
                Resources = resources, Background = (Brush)resources["BgPanel"],
                Width = 620, Height = 200, Child = rows
            };
            rows.Children.Add(new TextBlock
            {
                Text = dark ? "Dark theme — tab actions and playback selectors" : "Light theme — tab actions and playback selectors",
                Foreground = (Brush)resources["TextPrimary"], FontSize = 16, Margin = new Thickness(0, 0, 0, 18)
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            rows.Children.Add(actions);
            foreach (bool enabled in new[] { true, false })
            {
                foreach (string symbol in new[] { "‹", "›", "×" })
                {
                    var button = new Button
                    {
                        Style = (Style)resources["TabIconButton"], Content = symbol, FontSize = 20,
                        Width = 30, Height = 30, Margin = new Thickness(0, 0, 5, 0), IsEnabled = enabled
                    };
                    actions.Children.Add(button);
                }
            }
            var selectors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            rows.Children.Add(selectors);
            var combo = new ComboBox
            {
                Style = (Style)resources["CompactComboBox"], Width = 88,
                DisplayMemberPath = "Label", SelectedValuePath = "Tag",
                ItemsSource = new[] { new { Label = "Auto", Tag = "auto" }, new { Label = "1080p", Tag = "1080" } },
                SelectedValue = "1080"
            };
            selectors.Children.Add(combo);
            foreach (string style in new[] { "PrimaryButton", "SecondaryButton", "DangerButton" })
                selectors.Children.Add(new Button { Style = (Style)resources[style], Content = style.Replace("Button", ""), Margin = new Thickness(12, 0, 0, 0) });

            root.Measure(new Size(root.Width, root.Height));
            root.Arrange(new Rect(0, 0, root.Width, root.Height));
            root.UpdateLayout();
            foreach (Button button in actions.Children)
            {
                var chrome = (Border)button.Template.FindName("Chrome", button);
                Assert.Equal(Colors.Transparent, ((SolidColorBrush)chrome.Background).Color);
                Assert.Equal(button.IsEnabled ? 1 : 0.45, chrome.Opacity);
                Assert.Equal(30, chrome.ActualWidth);
            }
            Assert.Equal("1080", combo.SelectedValue);
            Assert.Contains(Descendants(combo).OfType<TextBlock>(), text => text.Text == "1080p");
            Assert.Equal(88, combo.ActualWidth);
            var bitmap = new RenderTargetBitmap(620, 200, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string output = Path.Combine(AppContext.BaseDirectory, "ui-evidence");
            Directory.CreateDirectory(output);
            using var stream = File.Create(Path.Combine(output, dark ? "controls-dark.png" : "controls-light.png"));
            encoder.Save(stream);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InteractiveForegroundsHaveReadableContrastAndPaletteCanSwitch(bool dark)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            var resources = LoadResources(!dark);
            ThemeRuntime.ApplyPalette(resources, dark, "Teal");
            foreach (string background in new[] { "ControlHoverBackground", "ControlPressedBackground", "BgInput", "BgElevated" })
                Assert.True(Contrast(ColorOf(resources, "TextPrimary"), ColorOf(resources, background)) >= 4.5, background);
            foreach (string accent in new[] { "Teal", "Purple", "Pink", "Cyan", "Green", "Amber", "Red" })
            {
                ThemeRuntime.ApplyPalette(resources, dark, accent);
                Assert.True(Contrast(ColorOf(resources, "AccentText"), ColorOf(resources, "AccentPrimary")) >= 4.5, accent);
            }
            Assert.True(Contrast(ColorOf(resources, "DangerText"), ColorOf(resources, "Error")) >= 4.5);
            Assert.Equal(ColorOf(resources, "TextPrimary"), ColorOf(resources, SystemColors.ControlTextBrushKey));
        });
    }

    private static ResourceDictionary LoadResources(bool dark)
    {
        var resources = new ResourceDictionary();
        foreach (string name in new[] { "Tokens", "Controls" })
            resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri($"/UniversalMediaOS.WPF;component/Themes/{name}.xaml", UriKind.Relative)));
        ThemeRuntime.ApplyPalette(resources, dark, "Teal");
        return resources;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static Color ColorOf(ResourceDictionary resources, object key) => ((SolidColorBrush)resources[key]).Color;
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value) => value / 255d <= 0.04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + 0.055) / 1.055, 2.4);
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        return (Math.Max(Luminance(a), Luminance(b)) + 0.05) / (Math.Min(Luminance(a), Luminance(b)) + 0.05);
    }
}
