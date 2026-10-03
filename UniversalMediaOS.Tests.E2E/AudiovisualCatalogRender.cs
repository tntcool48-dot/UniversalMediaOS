using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using UniversalMediaOS.WPF.Helpers;

namespace UniversalMediaOS.Tests.E2E;

internal static class AudiovisualCatalogRender
{
    // Use the build's exact view XAML and shared resources without starting App services.
    // The production view's only code-behind operation is InitializeComponent.
    public static UserControl Load(bool dark)
    {
        using var stream = typeof(AudiovisualCatalogRender).Assembly.GetManifestResourceStream("CatalogView.xaml")!;
        using var reader = new StreamReader(stream);
        var source = XDocument.Parse(reader.ReadToEnd().Replace("clr-namespace:UniversalMediaOS.WPF.Controls",
            "clr-namespace:UniversalMediaOS.WPF.Controls;assembly=UniversalMediaOS.WPF", StringComparison.Ordinal));
        var root = source.Root!;
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        root.Attribute(x + "Class")!.Remove();
        var resources = root.Element(ui + "UserControl.Resources")!;
        var existing = resources.Elements().ToArray();
        resources.RemoveNodes();
        resources.Add(new XElement(ui + "ResourceDictionary",
            new XElement(ui + "ResourceDictionary.MergedDictionaries",
                new[] { "Tokens", "Typography", "Motion", "Elevation", "Controls" }.Select(name =>
                    new XElement(ui + "ResourceDictionary", new XAttribute("Source", $"/UniversalMediaOS.WPF;component/Themes/{name}.xaml")))),
            new XElement(ui + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "BoolToVisibility")), existing));
        var view = (UserControl)XamlReader.Parse(source.ToString());
        ThemeRuntime.ApplyPalette(view.Resources, dark, "Teal");
        return view;
    }

    public static void Save(UserControl view, string name)
    {
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = Path.Combine(AppContext.BaseDirectory, "ui-evidence");
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
