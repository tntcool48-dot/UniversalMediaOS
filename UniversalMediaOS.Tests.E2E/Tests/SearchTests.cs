using System;
using System.Threading;
using Xunit;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    [Collection("Application Collection")]
    public class SearchTests
    {
        private readonly AppFixture _fixture;

        public SearchTests(AppFixture fixture)
        {
            _fixture = fixture;
        }

        private void NavigateToSearch()
        {
            var button = _fixture.MainWindow.FindFirstDescendant(cf =>
                cf.ByName("Anime").And(cf.ByControlType(ControlType.Button)))?.AsButton();
            Assert.NotNull(button);
            button.Invoke();
            Thread.Sleep(500);
        }

        [Fact]
        public void Search_EveryInteractiveControlHasAName()
        {
            NavigateToSearch();
            var search = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SearchView"));
            Assert.NotNull(search);
            Assert.True(SpinWait.SpinUntil(() => search.FindFirstDescendant(cf => cf.ByName("View Details")) != null,
                TimeSpan.FromSeconds(10)), "No result card was shown to audit.");
            // A screen reader announces a control by its UI Automation name; an unnamed one only by its type.
            var unnamed = new System.Collections.Generic.List<string>();
            foreach (var control in search.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)
                         .Or(cf.ByControlType(ControlType.Edit)).Or(cf.ByControlType(ControlType.ComboBox))
                         .Or(cf.ByControlType(ControlType.CheckBox)).Or(cf.ByControlType(ControlType.Slider))
                         .Or(cf.ByControlType(ControlType.RadioButton)).Or(cf.ByControlType(ControlType.Hyperlink))))
            {
                // Scroll bar parts take no keyboard focus and are not announced.
                if (control.IsOffscreen || !control.Properties.IsKeyboardFocusable.ValueOrDefault ||
                    !string.IsNullOrWhiteSpace(control.Name)) continue;
                unnamed.Add($"{control.ControlType} id='{control.AutomationId}' help='{control.HelpText}'");
            }
            Assert.True(unnamed.Count == 0, "Unnamed anime search controls:" + Environment.NewLine +
                string.Join(Environment.NewLine, System.Linq.Enumerable.Distinct(unnamed)));
        }

        [Fact]
        public void T1_Search_01_Basic_Keyword_Search()
        {
            NavigateToSearch();

            var searchBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
            Assert.NotNull(searchBox);
            searchBox.Text = "Frieren";

            var executeBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))?.AsButton();
            Assert.NotNull(executeBtn);
            executeBtn.Invoke();

            Thread.Sleep(1000);

            var resultsList = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List))?.AsListBox();
            Assert.NotNull(resultsList);
            Assert.True(resultsList.Items.Length > 0);
        }

        [Fact]
        public void T1_Search_02_Search_Filter_By_Media_Type()
        {
            // Anime search has no media-type filter (films and TV are separate areas). Its Status filter is the
            // type-like filter it offers: choosing one searches again with that status and shows only its titles.
            var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var fixture = new AppFixture(mangaPagePng: null, aniListFeed: request => FilteredAniList(request, received));
            string[] both = { "Airing Show", "Finished Show" };
            OpenAnime(fixture);
            Search(fixture, "FilterProbe");
            Assert.True(ShowsInOrder(fixture, both), string.Join(", ", Titles(fixture, both)));

            Combo(fixture, "Status").Select("Finished");
            Assert.True(ShowsInOrder(fixture, new[] { "Finished Show" }), string.Join(", ", Titles(fixture, both)));
            Assert.Contains("FilterProbe status=FINISHED", received);

            Combo(fixture, "Status").Select("Any");
            Assert.True(ShowsInOrder(fixture, both), string.Join(", ", Titles(fixture, both)));
        }

        [Fact]
        public void T1_Search_03_Search_Result_Pagination()
        {
            // Page 1 is a full 36-card page that reports a next page; page 2 ends the results with five more.
            var pages = new System.Collections.Concurrent.ConcurrentQueue<int>();
            using var fixture = new AppFixture(mangaPagePng: null, aniListFeed: request => PagedAniList(request, pages));
            OpenAnime(fixture);
            Search(fixture, "PagingProbe");
            Assert.True(WaitForText(fixture, "AniList results for \"PagingProbe\""), "The paged search did not finish.");
            Assert.True(SpinWait.SpinUntil(() => ResultCount(fixture) > 0, TimeSpan.FromSeconds(10)), "Page 1 showed no cards.");

            // Scrolling to the end of the list asks for the next page, as a user does.
            var list = MainWindowOf(fixture).FindFirstDescendant(cf => cf.ByAutomationId("ResultsList"))!;
            bool lastShown = SpinWait.SpinUntil(() =>
            {
                list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
                return list.FindFirstDescendant(cf => cf.ByName("Paging 41")) != null;
            }, TimeSpan.FromSeconds(20));
            Assert.True(lastShown, $"Page 2 never appeared; requested pages: {string.Join(", ", pages)}.{Environment.NewLine}{AppLog(fixture)}");

            // The end of the results asks for nothing more, and no page is requested twice.
            list.Patterns.Scroll.Pattern.SetScrollPercent(-1, 100);
            Thread.Sleep(1500);
            Assert.Equal(new[] { 1, 2 }, pages.ToArray());
            Assert.NotNull(list.FindFirstDescendant(cf => cf.ByName("Paging 37")));
        }

        private static (int Status, string Body) PagedAniList(System.Net.HttpListenerRequest request,
            System.Collections.Concurrent.ConcurrentQueue<int> pages)
        {
            using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
            using var json = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            var variables = json.RootElement.TryGetProperty("variables", out var value) ? value : default;
            bool paging = variables.ValueKind == System.Text.Json.JsonValueKind.Object &&
                variables.TryGetProperty("search", out var search) && search.GetString() == "PagingProbe";
            int page = paging && variables.TryGetProperty("page", out var number) ? number.GetInt32() : 1;
            if (paging) pages.Enqueue(page);
            var items = !paging ? System.Linq.Enumerable.Range(1000, 1)
                : page == 1 ? System.Linq.Enumerable.Range(1, 36) : System.Linq.Enumerable.Range(37, 5);
            string media = string.Join(",", System.Linq.Enumerable.Select(items, i =>
                $"{{\"id\":{i},\"idMal\":0,\"title\":{{\"romaji\":\"Paging {i}\",\"english\":\"Paging {i}\"}}," +
                "\"coverImage\":{\"extraLarge\":\"\"},\"description\":\"\"}"));
            string next = paging && page == 1 ? "true" : "false";
            return (200, $"{{\"data\":{{\"Page\":{{\"pageInfo\":{{\"hasNextPage\":{next}}},\"media\":[{media}]}}}}}}");
        }

        [Fact]
        public void T1_Search_04_Fuzzy_Match_Score_Ordering()
        {
            // The app does not score matches itself. A typed query asks AniList for its relevance order
            // (SEARCH_MATCH), another sort asks for that order, and the cards keep the provider's order.
            var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var fixture = new AppFixture(mangaPagePng: null, aniListFeed: request => FilteredAniList(request, received));
            string[] relevance = { "Order One", "Order Two", "Order Three" };
            string[] topRated = { "Order Three", "Order One", "Order Two" };
            OpenAnime(fixture);
            Search(fixture, "OrderProbe");
            Assert.True(ShowsInOrder(fixture, relevance), string.Join(", ", Titles(fixture, relevance)));
            Assert.Contains("OrderProbe sort=SEARCH_MATCH", received);

            Combo(fixture, "Sort").Select("Top Rated");
            Assert.True(ShowsInOrder(fixture, topRated), string.Join(", ", Titles(fixture, relevance)));
            Assert.Contains("OrderProbe sort=SCORE_DESC", received);
        }

        private static (int Status, string Body) FilteredAniList(System.Net.HttpListenerRequest request,
            System.Collections.Concurrent.ConcurrentQueue<string> received)
        {
            using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
            using var json = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            var variables = json.RootElement.TryGetProperty("variables", out var value) ? value : default;
            string Variable(string name) =>
                variables.ValueKind == System.Text.Json.JsonValueKind.Object && variables.TryGetProperty(name, out var item)
                    ? item.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => item.GetString()!,
                        System.Text.Json.JsonValueKind.Array => string.Join("|", System.Linq.Enumerable.Select(item.EnumerateArray(), part => part.ToString())),
                        _ => string.Empty
                    }
                    : string.Empty;
            string search = Variable("search"), status = Variable("status"), sort = Variable("sort");
            string[] titles = search switch
            {
                "FilterProbe" => status == "FINISHED" ? new[] { "Finished Show" } : new[] { "Airing Show", "Finished Show" },
                "OrderProbe" => sort == "SCORE_DESC" ? new[] { "Order Three", "Order One", "Order Two" }
                    : new[] { "Order One", "Order Two", "Order Three" },
                _ => new[] { "Default Show" }
            };
            if (search.Length > 0) received.Enqueue($"{search} status={(status.Length > 0 ? status : "none")}");
            if (search.Length > 0) received.Enqueue($"{search} sort={sort}");
            string media = string.Join(",", System.Linq.Enumerable.Select(titles, (title, index) =>
                $"{{\"id\":{2000 + Array.IndexOf(new[] { "Airing Show", "Finished Show", "Order One", "Order Two", "Order Three", "Default Show" }, title)}," +
                $"\"idMal\":0,\"title\":{{\"romaji\":\"{title}\",\"english\":\"{title}\"}},\"coverImage\":{{\"extraLarge\":\"\"}},\"description\":\"\"}}"));
            return (200, $"{{\"data\":{{\"Page\":{{\"pageInfo\":{{\"hasNextPage\":false}},\"media\":[{media}]}}}}}}");
        }

        private static ComboBox Combo(AppFixture fixture, string name) =>
            MainWindowOf(fixture).FindFirstDescendant(cf => cf.ByAutomationId("SearchView"))!
                .FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.ComboBox)))!.AsComboBox();

        /// <summary>The known titles shown in the results list, in on-screen order.</summary>
        private static string[] Titles(AppFixture fixture, string[] known) => System.Linq.Enumerable.ToArray(
            System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Where(System.Linq.Enumerable.Select(
                MainWindowOf(fixture).FindFirstDescendant(cf => cf.ByAutomationId("ResultsList"))?
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.Text)) ?? Array.Empty<AutomationElement>(),
                text => text.Name), name => Array.IndexOf(known, name) >= 0)));

        private static bool ShowsInOrder(AppFixture fixture, string[] expected) => SpinWait.SpinUntil(() =>
            System.Linq.Enumerable.SequenceEqual(Titles(fixture, new[] { "Airing Show", "Finished Show", "Order One", "Order Two", "Order Three" }), expected),
            TimeSpan.FromSeconds(20));

        [Fact]
        public void T1_Search_05_Safe_Error_Display_On_Scraper_Offline()
        {
            // AniList answers normally except for one query, for which the endpoint reports an outage.
            using var fixture = new AppFixture(mangaPagePng: null, aniListFeed: RoutedAniList);
            OpenAnime(fixture);
            Search(fixture, "Frieren");
            Assert.True(WaitForText(fixture, "AniList results for \"Frieren\""), "The normal search did not finish.");
            int shown = ResultCount(fixture);
            Assert.True(shown > 0, "The normal search showed no cards.");

            // The outage is reported as unavailable, not as "no anime", and the earlier cards stay.
            Search(fixture, "OutageProbe");
            Assert.True(WaitForText(fixture, "Anime catalog unavailable. Previous results retained; retry search."),
                "The outage was not reported.");
            Assert.Equal(shown, ResultCount(fixture));
            Assert.False(Visible(fixture, "No anime found"));

            // Retrying once the provider answers again recovers normally.
            Search(fixture, "Frieren");
            Assert.True(WaitForText(fixture, "AniList results for \"Frieren\""), "The retried search did not recover.");
            Assert.True(ResultCount(fixture) > 0);
        }

        [Fact]
        public void T2_Search_01_Empty_Search_Input()
        {
            NavigateToSearch();
            var searchBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
            Assert.NotNull(searchBox);
            searchBox.Text = "   "; // spaces

            var executeBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))?.AsButton();
            Assert.NotNull(executeBtn);
            executeBtn.Invoke();
            Thread.Sleep(500);

            // Verify search command handles empty input gracefully without throwing
            var resultsList = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List))?.AsListBox();
            Assert.NotNull(resultsList);
        }

        [Fact]
        public void T2_Search_02_Special_Characters_And_SQL_Injection()
        {
            NavigateToSearch();
            var searchBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
            Assert.NotNull(searchBox);
            searchBox.Text = "' OR 1=1; -- <script>alert(1)</script>";

            var executeBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))?.AsButton();
            Assert.NotNull(executeBtn);
            executeBtn.Invoke();
            Thread.Sleep(1000);

            // Verify no crash
            var resultsList = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List))?.AsListBox();
            Assert.NotNull(resultsList);
        }

        [Fact]
        public void T2_Search_03_Ultra_Long_Query_String()
        {
            NavigateToSearch();
            var searchBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
            Assert.NotNull(searchBox);
            searchBox.Text = new string('A', 1000);

            var executeBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))?.AsButton();
            Assert.NotNull(executeBtn);
            executeBtn.Invoke();
            Thread.Sleep(1000);

            // Verify no memory/overflow exception
            var resultsList = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List))?.AsListBox();
            Assert.NotNull(resultsList);
        }

        [Fact]
        public void T2_Search_04_Scraper_Returns_Empty_Or_Null()
        {
            using var fixture = new AppFixture(mangaPagePng: null, aniListFeed: RoutedAniList);
            OpenAnime(fixture);

            // An empty page is a real "no results" answer.
            Search(fixture, "EmptyProbe");
            Assert.True(WaitForText(fixture, "AniList results for \"EmptyProbe\""), "The empty search did not finish.");
            Assert.True(SpinWait.SpinUntil(() => Visible(fixture, "No anime found"), TimeSpan.FromSeconds(5)), "No empty state was shown.");
            Assert.Equal(0, ResultCount(fixture));

            // A response without data is a provider fault, not "no anime".
            Search(fixture, "NullProbe");
            Assert.True(WaitForText(fixture, "Anime catalog unavailable. Check your connection and retry search."),
                "The null response was not reported as unavailable." + Environment.NewLine + AppLog(fixture));
            Assert.False(Visible(fixture, "No anime found"));
            Assert.Equal(0, ResultCount(fixture));
        }

        private static (int Status, string Body) RoutedAniList(System.Net.HttpListenerRequest request)
        {
            using var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
            string body = reader.ReadToEnd();
            if (body.Contains("OutageProbe", StringComparison.Ordinal)) return (503, string.Empty);
            if (body.Contains("NullProbe", StringComparison.Ordinal)) return (200, "{\"data\":null}");
            string media = body.Contains("EmptyProbe", StringComparison.Ordinal) ? string.Empty :
                "{\"id\":1,\"idMal\":1,\"title\":{\"romaji\":\"Mock Romaji\",\"english\":\"Mock English\"}," +
                "\"coverImage\":{\"extraLarge\":\"http://localhost:5000/cover.png\"},\"description\":\"Mock Description\"}";
            return (200, "{\"data\":{\"Page\":{\"pageInfo\":{\"hasNextPage\":false},\"media\":[" + media + "]}}}");
        }

        private static Window MainWindowOf(AppFixture fixture) => System.Linq.Enumerable.Single(
            fixture.App.GetAllTopLevelWindows(fixture.Automation), window => window.Title == AppFixture.ExpectedMainWindowTitle);

        /// <summary>Opens the Anime tab once; opening it again starts a fresh tab with recommendations.</summary>
        private static void OpenAnime(AppFixture fixture)
        {
            var window = MainWindowOf(fixture);
            window.FindFirstDescendant(cf => cf.ByName("Anime").And(cf.ByControlType(ControlType.Button)))!.AsButton().Invoke();
            Assert.True(SpinWait.SpinUntil(() => window.FindFirstDescendant(cf => cf.ByName("ExecuteSearch")) != null,
                TimeSpan.FromSeconds(10)), "The anime search did not open.");
        }

        private static void Search(AppFixture fixture, string query)
        {
            var window = MainWindowOf(fixture);
            window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))!.AsTextBox().Text = query;
            window.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))!.AsButton().Invoke();
        }

        private static string AppLog(AppFixture fixture)
        {
            string path = System.IO.Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
            if (!System.IO.File.Exists(path)) return string.Empty;
            using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            return new System.IO.StreamReader(stream).ReadToEnd();
        }

        private static bool WaitForText(AppFixture fixture, string text) => SpinWait.SpinUntil(
            () => MainWindowOf(fixture).FindFirstDescendant(cf => cf.ByName(text)) != null, TimeSpan.FromSeconds(20));

        private static bool Visible(AppFixture fixture, string text) =>
            MainWindowOf(fixture).FindFirstDescendant(cf => cf.ByName(text)) is { IsOffscreen: false };

        /// <summary>Cards in the results list; each is a "View Details" button (filter combo boxes also expose lists).</summary>
        private static int ResultCount(AppFixture fixture) =>
            MainWindowOf(fixture).FindFirstDescendant(cf => cf.ByAutomationId("ResultsList"))?
                .FindAllDescendants(cf => cf.ByName("View Details").And(cf.ByControlType(ControlType.Button))).Length ?? 0;

        [Fact]
        public void T2_Search_05_Rapid_Concurrent_Double_Searches()
        {
            NavigateToSearch();
            var searchBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
            Assert.NotNull(searchBox);
            searchBox.Text = "Bleach";

            var executeBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))?.AsButton();
            Assert.NotNull(executeBtn);
            
            // The async command disables the button while work is in flight.
            // UI Automation may reject the second invoke; this is expected input gating.
            Assert.True(SpinWait.SpinUntil(() => executeBtn.IsEnabled, TimeSpan.FromSeconds(5)));
            executeBtn.Invoke();
            try { executeBtn.Invoke(); }
            catch (FlaUI.Core.Exceptions.ElementNotEnabledException) { }

            Assert.True(SpinWait.SpinUntil(() => executeBtn.IsEnabled, TimeSpan.FromSeconds(5)),
                "Search should complete and accept input again after a rapid repeat attempt.");

            var resultsList = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List))?.AsListBox();
            Assert.NotNull(resultsList);
            Assert.True(resultsList.Items.Length > 0);
        }
    }
}
