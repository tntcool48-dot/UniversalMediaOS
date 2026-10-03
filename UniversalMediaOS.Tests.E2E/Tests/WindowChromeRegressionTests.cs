using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class WindowChromeRegressionTests
    {
        [Fact]
        public void MainTitleBar_LeavesCaptionSurfaceToWindowsAndOnlyOptsButtonsIntoClientHitTesting()
        {
            string root = FindWorkspaceRoot();
            XDocument document = XDocument.Load(Path.Combine(root, "UniversalMediaOS.WPF", "MainWindow.xaml"));
            XElement window = Assert.IsType<XElement>(document.Root);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

            Assert.Equal("CanResize", (string?)window.Attribute("ResizeMode"));
            Assert.Equal("900", (string?)window.Attribute("MinWidth"));
            Assert.Equal("560", (string?)window.Attribute("MinHeight"));

            XElement chrome = Assert.Single(window.Descendants(), element =>
                element.Name.LocalName == "WindowChrome" && element.Attribute("CaptionHeight") != null);
            Assert.Equal("34", (string?)chrome.Attribute("CaptionHeight"));

            XElement titleBar = Assert.Single(window.Descendants(), element =>
                element.Name.LocalName == "Border" &&
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Grid.Row" && attribute.Value == "0") &&
                element.Parent?.Name.LocalName == "Grid");
            Assert.DoesNotContain(titleBar.Attributes(), attribute =>
                attribute.Name.LocalName == "WindowChrome.IsHitTestVisibleInChrome" &&
                attribute.Value.Equals("True", StringComparison.OrdinalIgnoreCase));

            foreach (string styleKey in new[] { "TopMediaButton", "WindowControlButton" })
            {
                XElement style = Assert.Single(window.Descendants(), element =>
                    element.Name.LocalName == "Style" && (string?)element.Attribute(x + "Key") == styleKey);
                Assert.Contains(style.Elements(), element =>
                    element.Name.LocalName == "Setter" &&
                    (string?)element.Attribute("Property") == "WindowChrome.IsHitTestVisibleInChrome" &&
                    (string?)element.Attribute("Value") == "True");
            }
        }

        private static string FindWorkspaceRoot()
        {
            foreach (string startingPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var candidate = new DirectoryInfo(startingPath);
                while (candidate != null)
                {
                    if (File.Exists(Path.Combine(candidate.FullName, "UniversalMediaOS.sln")))
                    {
                        return candidate.FullName;
                    }

                    candidate = candidate.Parent;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the UniversalMediaOS workspace root.");
        }
    }
}
