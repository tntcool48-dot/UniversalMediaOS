using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class BrowserPlaybackStartupTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public void NativeOverlayOnlyReceivesInputForTheActiveNativeTab(bool web, bool active, bool visible)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            using var source = typeof(BrowserPlaybackStartupTests).Assembly.GetManifestResourceStream("PlaybackView.xaml")!;
            var document = XDocument.Load(source);
            XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var root = new XElement(ui + "Grid", new XAttribute(XNamespace.Xmlns + "x", x),
                new XElement(ui + "Grid.Resources",
                    new XElement(ui + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "BoolToVisibility")),
                    document.Descendants().Where(element => (string?)element.Attribute(x + "Key") is
                        "ControlButtonStyle" or "PrimaryControlButtonStyle" or "PlaybackStateOverlayTemplate")
                        .Select(element => new XElement(element))));
            foreach (var attribute in root.Descendants().Attributes().Where(attribute =>
                attribute.Name.LocalName is "Click" or "MouseMove" or "MouseLeftButtonDown").ToArray())
                attribute.Remove();
            var visual = (Grid)XamlReader.Parse(root.ToString());
            var overlay = (Grid)((DataTemplate)visual.Resources["PlaybackStateOverlayTemplate"]).LoadContent();
            overlay.DataContext = new { IsWebViewActive = web, IsTabActive = active,
                IsPlaybackBusy = true, HasPlaybackError = true, IsPictureInPictureMode = false };
            visual.Children.Add(overlay);
            visual.Measure(new Size(900, 500));
            visual.Arrange(new Rect(0, 0, 900, 500));
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, overlay.Visibility);
            Assert.Equal(visible, overlay.IsHitTestVisible);
        });
    }

    [Theory]
    [InlineData(900, 500)]
    [InlineData(360, 240)]
    public void NativeControlsOverlayVideoWithoutResizingItAndSiteControlsStayHidden(double width, double height)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            using var source = typeof(BrowserPlaybackStartupTests).Assembly.GetManifestResourceStream("PlaybackView.xaml")!;
            var document = XDocument.Load(source);
            XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var productionControls = document.Descendants(ui + "Border")
                .Single(element => (string?)element.Attribute(x + "Name") == "ControlsLayer");
            Assert.Contains(productionControls.Ancestors(), element => element.Name.LocalName == "VideoView");
            var controls = new XElement(productionControls);
            // Keep the actual panel styling and mode trigger; substitute the
            // toolbar contents, which are unrelated to this layout assertion.
            controls.Elements(ui + "Grid").Single().ReplaceWith(
                new XElement(ui + "Border", new XAttribute("Height", "36")));
            var root = new XElement(document.Descendants(ui + "Grid")
                .Single(element => (string?)element.Attribute(x + "Name") == "PlaybackRoot"));
            Assert.Null(root.Element(ui + "Grid.RowDefinitions"));
            root.RemoveNodes();
            root.Add(new XAttribute(XNamespace.Xmlns + "x", x),
                new XElement(ui + "Border", new XAttribute(x + "Name", "Video")), controls);
            foreach (var attribute in root.DescendantsAndSelf().Attributes()
                .Where(attribute => attribute.Name.LocalName is "MouseLeave" or "MouseMove" or "PreviewMouseDown" or "PreviewMouseWheel").ToArray())
                attribute.Remove();
            var visual = (Grid)XamlReader.Parse(root.ToString());
            var panel = (Border)visual.FindName("ControlsLayer");
            var video = (Border)visual.FindName("Video");
            foreach (bool web in new[] { true, false, true })
            {
                visual.DataContext = new { IsWebViewActive = web };
                panel.ClearValue(UIElement.VisibilityProperty);
                visual.Measure(new Size(width, height));
                visual.Arrange(new Rect(0, 0, width, height));
                visual.UpdateLayout();
                Assert.Equal(web ? Visibility.Collapsed : Visibility.Visible, panel.Visibility);
                if (web)
                {
                    Assert.False(panel.IsHitTestVisible);
                    Assert.Equal(height, video.ActualHeight);
                }
                else
                {
                    Assert.Equal(height, video.ActualHeight);
                    Assert.Equal(height, panel.TranslatePoint(new Point(0, panel.ActualHeight), visual).Y);
                    panel.Visibility = Visibility.Collapsed;
                    visual.UpdateLayout();
                    Assert.Equal(height, video.ActualHeight);
                    Assert.Equal(width, video.ActualWidth);
                }
            }
        });
    }

    [Theory]
    [InlineData(900, 500, 1920, 1080)]
    [InlineData(360, 240, 1920, 1080)]
    [InlineData(2560, 1440, 1920, 1080)]
    [InlineData(900, 700, 1920, 800)]
    public void ExpandedOptionsScrollAboveTransportAndLeaveNativeCaptionSpace(
        double width, double height, uint videoWidth, uint videoHeight)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            using var source = typeof(BrowserPlaybackStartupTests).Assembly.GetManifestResourceStream("PlaybackView.xaml")!;
            var document = XDocument.Load(source);
            XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var controls = new XElement(document.Descendants(ui + "Border")
                .Single(element => (string?)element.Attribute(x + "Name") == "ControlsLayer"));
            XElement Named(string name) => controls.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
            // Retain the production panel positions, grid constraints, expander
            // and scroll container. Substitute text/buttons with sized content.
            Named("TitlePanel").ReplaceNodes(new XElement(ui + "Border", new XAttribute("Height", 24)));
            Named("TransportPanel").ReplaceNodes(new XElement(ui + "Border", new XAttribute("Height", 62)));
            var expander = Named("PlaybackOptions");
            expander.Attribute("IsExpanded")!.Value = "True";
            expander.Elements().Single().ReplaceNodes(new XElement(ui + "Border", new XAttribute("Height", 360)));
            foreach (var attribute in controls.DescendantsAndSelf().Attributes()
                .Where(a => a.Name.LocalName is "MouseLeave" or "Expanded" or "Collapsed" or "AutomationProperties.Name").ToArray())
                attribute.Remove();
            var root = new XElement(ui + "Grid", new XAttribute(XNamespace.Xmlns + "x", x),
                new XElement(ui + "Border", new XAttribute(x + "Name", "Video")), controls);
            var visual = (Grid)XamlReader.Parse(root.ToString());
            visual.DataContext = new { IsWebViewActive = false };
            var transport = (Border)visual.FindName("TransportPanel");
            transport.Margin = new Thickness(0, 0, 0, UniversalMediaOS.WPF.Views.PlaybackView
                .CaptionControlsBottomInset(width, height, videoWidth, videoHeight, true));
            visual.Measure(new Size(width, height));
            visual.Arrange(new Rect(0, 0, width, height));
            visual.UpdateLayout();
            var options = (Border)visual.FindName("OptionsPanel");
            var video = (Border)visual.FindName("Video");
            double transportTop = transport.TranslatePoint(new Point(), visual).Y;
            double transportBottom = transport.TranslatePoint(new Point(0, transport.ActualHeight), visual).Y;
            double pictureHeight = Math.Min(height, width * videoHeight / videoWidth);
            double bottomCaptionBand = (height + pictureHeight) / 2 - pictureHeight * 0.12;
            Assert.True(transportBottom <= bottomCaptionBand);
            Assert.True(options.TranslatePoint(new Point(0, options.ActualHeight), visual).Y <= transportTop);
            Assert.Equal(new Size(width, height), video.RenderSize);
            var playbackOptions = (Expander)visual.FindName("PlaybackOptions");
            playbackOptions.IsExpanded = false;
            visual.UpdateLayout();
            Assert.Equal(transportTop, transport.TranslatePoint(new Point(), visual).Y);
            Assert.Equal(new Size(width, height), video.RenderSize);
            transport.Margin = new Thickness(0, 0, 0, UniversalMediaOS.WPF.Views.PlaybackView
                .CaptionControlsBottomInset(width, height, videoWidth, videoHeight, false));
            visual.UpdateLayout();
            Assert.Equal(0, transport.Margin.Bottom);
            Assert.Equal(new Size(width, height), video.RenderSize);
        });
    }

    [Fact]
    public void SlowBrowserRemainsAvailableAndLaterVideoClearsWaitingState()
    {
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.IsWebViewActive = true;
        player.EmbedUrl = "https://player.example/episode";
        player.IsPlaybackBusy = true;
        player.CheckPlaybackStartup(player.PlaybackStartupGeneration, isWeb: true);
        Assert.False(player.HasPlaybackError);
        Assert.False(player.IsPlaybackBusy);
        Assert.Equal("https://player.example/episode", player.EmbedUrl);
        Assert.Contains("waiting", player.PlaybackStatusText);

        player.ReportWebPlaybackAction("playing", 40, 1440);
        Assert.True(player.IsPlaying);
        Assert.False(player.HasPlaybackError);
        Assert.Equal("Playing", player.PlaybackStatusText);
    }

    [Fact]
    public void OldStartupCheckCannotOverwriteNewPlaybackOrARealNavigationError()
    {
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.IsWebViewActive = true;
        int oldGeneration = player.PlaybackStartupGeneration;
        player.ReportWebPlaybackAction("playing", 3, 1440);
        player.CheckPlaybackStartup(oldGeneration, isWeb: true);
        Assert.Equal("Playing", player.PlaybackStatusText);

        oldGeneration = player.PlaybackStartupGeneration;
        player.ReportPlaybackError("Navigation failed");
        player.CheckPlaybackStartup(oldGeneration, isWeb: true);
        Assert.True(player.HasPlaybackError);
        Assert.Equal("Navigation failed", player.PlaybackErrorText);
    }

    [Fact]
    public void NativeStartupStillReportsFailureWhileLoadedBrowserWaitsForInteraction()
    {
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.CheckPlaybackStartup(player.PlaybackStartupGeneration, isWeb: false);
        Assert.True(player.HasPlaybackError);
        Assert.Contains("30 seconds", player.PlaybackErrorText);

        player.HasPlaybackError = false;
        player.IsWebViewActive = true;
        player.IsPlaybackBusy = true;
        player.ReportWebPageLoaded();
        Assert.False(player.IsPlaybackBusy);
        Assert.False(player.IsPlaying);
        Assert.Equal("Press Play in the page", player.PlaybackStatusText);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void BrowserSurfaceReceivesClicksDuringLoadingWaitingAndErrors(bool busy, bool error)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            using var source = typeof(BrowserPlaybackStartupTests).Assembly.GetManifestResourceStream("PlaybackView.xaml")!;
            var document = XDocument.Load(source);
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var surface = new XElement(document.Descendants(wpf + "Grid").Single(e => (string?)e.Attribute(x + "Name") == "VideoSurface"));
            // Substitute native/browser hosts; retain the production overlay and
            // run WPF hit testing to catch invisible controls swallowing clicks.
            foreach (var host in surface.Elements().Where(e => e.Name.LocalName is "VideoView" or "WebView2CompositionControl").ToArray())
            {
                bool browser = host.Name.LocalName == "WebView2CompositionControl";
                host.ReplaceWith(new XElement(wpf + "Border", new XAttribute(x + "Name", browser ? "Browser" : "Native"),
                    new XAttribute("Background", "Blue"), new XAttribute("Visibility", browser ? "Visible" : "Collapsed")));
            }
            var resources = new XElement(wpf + "Grid.Resources",
                new XElement(wpf + "BooleanToVisibilityConverter", new XAttribute(x + "Key", "BoolToVisibility")),
                document.Descendants().Where(e => (string?)e.Attribute(x + "Key") is "ControlButtonStyle" or "PrimaryControlButtonStyle" or "PlaybackStateOverlayTemplate")
                    .Select(e => new XElement(e)));
            surface.AddFirst(resources);
            surface.Add(new XAttribute(XNamespace.Xmlns + "x", x));
            foreach (var attribute in surface.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "Click" or "MouseMove" or "MouseLeftButtonDown").ToArray()) attribute.Remove();
            var visual = (Grid)XamlReader.Parse(surface.ToString());
            visual.DataContext = new { IsWebViewActive = true, IsPictureInPictureMode = false, IsPlaybackBusy = busy, HasPlaybackError = error };
            visual.Measure(new Size(900, 500));
            visual.Arrange(new Rect(0, 0, 900, 500));
            visual.UpdateLayout();
            var hit = VisualTreeHelper.HitTest(visual, new Point(450, 250));
            Assert.Same(visual.FindName("Browser"), hit?.VisualHit);
            // LibVLC hosts its overlay in another HWND, which can outlive the
            // host's visibility. The native template itself must hide on web.
            var nativeTemplate = (DataTemplate)visual.Resources["PlaybackStateOverlayTemplate"];
            var nativeOverlay = (Grid)nativeTemplate.LoadContent();
            nativeOverlay.DataContext = visual.DataContext;
            visual.Children.Add(nativeOverlay);
            nativeOverlay.Measure(new Size(900, 500));
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Collapsed, nativeOverlay.Visibility);
        });
    }
}
