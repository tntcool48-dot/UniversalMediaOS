using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class SearchReturnPagingTests
{
    [Fact]
    public void ReturningToScrollableCatalogDoesNotFetchPagesUntilTheUserApproachesTheBottom()
    {
        int pageRequests = 0;
        using var fixture = new AppFixture(mangaPagePng: null, aniListFeed: request =>
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            if (!document.RootElement.TryGetProperty("variables", out var variables) ||
                !variables.TryGetProperty("page", out var pageValue))
                return (200, "{\"data\":{\"GenreCollection\":[],\"MediaTagCollection\":[]}}");
            int page = pageValue.GetInt32();
            Interlocked.Increment(ref pageRequests);
            int count = page == 1 ? 108 : 36;
            int start = page == 1 ? 1 : 109 + (page - 2) * 36;
            return (200, JsonSerializer.Serialize(new { data = new { Page = new
            {
                pageInfo = new { hasNextPage = true },
                media = Enumerable.Range(start, count).Select(id => new
                {
                    id, idMal = id, title = new { english = $"Paging Anime {id:000}", romaji = $"Paging Anime {id:000}" },
                    coverImage = new { extraLarge = "" },
                    episodes = 12, status = "FINISHED", nextAiringEpisode = (object?)null
                })
            } } }));
        });
        Window Window() => fixture.MainWindow;
        void Wait(Func<bool> condition, string reason) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), reason);
        Button[] Tabs() => Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab")).Select(element => element.AsButton()).ToArray();
        Wait(() => Window().FindFirstDescendant(cf => cf.ByName("View Details")) != null, "The initial catalog must render.");
        Window().Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        Thread.Sleep(200);
        Window().Patterns.Transform.Pattern.Resize(1200, 750);
        Thread.Sleep(350);
        int initialRequests = Volatile.Read(ref pageRequests);
        Assert.Equal(1, initialRequests);

        for (int cycle = 0; cycle < 4; cycle++)
        {
            Window().FindFirstDescendant(cf => cf.ByName("View Details"))!.AsButton().Invoke();
            Wait(() => Tabs().Any(tab => tab.Name.StartsWith("Paging Anime", StringComparison.Ordinal)), "Details must open for the selected card.");
            Tabs().Single(tab => tab.Name == "Anime").Invoke();
            Wait(() => Window().FindFirstDescendant(cf => cf.ByAutomationId("ResultsList")) != null, "The retained catalog must return.");
            Window().FindAllDescendants(cf => cf.ByAutomationId("CloseTab")).Last().AsButton().Invoke();
            Thread.Sleep(350); // Allow queued layout and any erroneous localhost page request to complete.
            Assert.Equal(initialRequests, Volatile.Read(ref pageRequests));
        }

        var results = Window().FindFirstDescendant(cf => cf.ByAutomationId("ResultsList"))!;
        Assert.True(results.Patterns.Scroll.Pattern.VerticallyScrollable);
        results.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
        Wait(() => Volatile.Read(ref pageRequests) > initialRequests, "Genuine bottom scrolling must still load another page.");
        Assert.False(fixture.App.HasExited);
    }
}
