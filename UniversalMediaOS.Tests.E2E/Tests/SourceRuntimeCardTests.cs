using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

/// <summary>The October 23 probe found Archive uploads of The Lost World (1925) running 68, 76, 92 and 104 minutes
/// against the catalog's 104, all titled and dated as the film. A source's own declared runtime is shown on its
/// card so an abridged print can be seen before playing. It is display only: no threshold, no identity evidence.</summary>
public sealed class SourceRuntimeCardTests
{
    [Fact]
    public async Task ArchiveSourceCarriesItsSelectedFilesDeclaredLength()
    {
        AudiovisualSource abridged = Assert.Single(await FindAsync("""{"name":"lost_world.mp4","format":"MPEG4","source":"original","length":"4085.2"}"""));
        Assert.Equal(4085.2, abridged.DurationSeconds);
        AudiovisualSource undeclared = Assert.Single(await FindAsync("""{"name":"lost_world.mp4","format":"MPEG4","source":"original"}"""));
        Assert.Null(undeclared.DurationSeconds);
    }

    [Theory]
    [InlineData(4085.2, "Runtime 1 h 8 min")]
    [InlineData(6240.0, "Runtime 1 h 44 min")]
    [InlineData(888.08, "Runtime 15 min")]
    [InlineData(45.0, "Runtime 45 s")]
    public void CardStatesTheSourcesRuntime(double seconds, string expected)
    {
        var card = new AudiovisualSourceViewModel(Source(seconds));
        Assert.Equal(expected, card.RuntimeText);
        Assert.True(card.HasRuntime);
    }

    [Fact]
    public void CardWithoutADeclaredRuntimeShowsNone()
    {
        var card = new AudiovisualSourceViewModel(Source(null));
        Assert.Equal(string.Empty, card.RuntimeText);
        Assert.False(card.HasRuntime);
    }

    [Fact]
    public void ProductionSourceCardRendersTheRuntime()
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            var view = AudiovisualCatalogRender.Load(dark: true);
            var sources = Assert.Single(LogicalDescendants(view).OfType<ItemsControl>(),
                control => BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)?.Path.Path == "Sources");
            string[] Shown(AudiovisualSourceViewModel model)
            {
                var card = (FrameworkElement)sources.ItemTemplate.LoadContent();
                card.DataContext = model;
                card.Measure(new Size(900, 400));
                card.Arrange(new Rect(0, 0, 900, 400));
                card.UpdateLayout();
                return AudiovisualCatalogRender.Descendants(card).OfType<TextBlock>()
                    .Where(text => text.IsVisible || text.Visibility == Visibility.Visible && IsShownInTree(text))
                    .Select(text => text.Text).ToArray();
            }

            Assert.Contains("Runtime 1 h 8 min", Shown(new AudiovisualSourceViewModel(Source(4085.2))));
            Assert.DoesNotContain(Shown(new AudiovisualSourceViewModel(Source(null))), text => text.StartsWith("Runtime", StringComparison.Ordinal));
        });
    }

    private static bool IsShownInTree(FrameworkElement element)
    {
        for (DependencyObject? current = element; current != null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (var nested in LogicalDescendants(node)) yield return nested;
        }
    }

    private static AudiovisualSource Source(double? seconds) => new()
    {
        ProviderId = "internet-archive",
        ProviderName = "Internet Archive",
        Location = new Uri("https://archive.example/download/lost_world/lost_world.mp4"),
        AccessMode = AudiovisualSourceAccessMode.DirectMedia,
        DurationSeconds = seconds
    };

    private static async Task<IReadOnlyList<AudiovisualSource>> FindAsync(string file)
    {
        using var http = new HttpClient(new Responses(request => request.RequestUri!.AbsolutePath == "/advancedsearch.php"
            ? """{"response":{"docs":[{"identifier":"lost_world","title":"The Lost World","year":1925}]}}"""
            : $$"""{"metadata":{"identifier":"lost_world","title":"The Lost World","year":"1925"},"files":[{{file}}]}"""));
        var provider = new InternetArchiveSourceProvider(new AudiovisualOptions { InternetArchiveBaseUri = new Uri("https://archive.example/") },
            new ProviderRequestCoordinator(http));
        return await provider.FindSourcesAsync(new AudiovisualIdentity
        {
            Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature, Title = "The Lost World", Year = 1925
        }, AudiovisualUnit.Feature);
    }

    private sealed class Responses(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
        }
    }
}
