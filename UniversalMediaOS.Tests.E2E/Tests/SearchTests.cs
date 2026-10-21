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

        [Fact(Skip = "Requires selecting a media-type filter and asserting filtered identities; the previous test only submitted a query.")]
        public void T1_Search_02_Search_Filter_By_Media_Type()
        {
            // App only has Anime search in SearchView, Manga in Library view. 
            // In SearchView, we search for Anime and verify results load.
            NavigateToSearch();
            var searchBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
            Assert.NotNull(searchBox);
            searchBox.Text = "Monster";

            var executeBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))?.AsButton();
            Assert.NotNull(executeBtn);
            executeBtn.Invoke();
            Thread.Sleep(1000);

            var resultsList = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List))?.AsListBox();
            Assert.NotNull(resultsList);
            Assert.True(resultsList.Items.Length > 0);
        }

        [Fact(Skip = "Requires a paged mock response and a next-page action; the previous test inspected only the first result list.")]
        public void T1_Search_03_Search_Result_Pagination()
        {
            NavigateToSearch();
            // Perform a search that mocks hasNextPage.
            var searchBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
            Assert.NotNull(searchBox);
            searchBox.Text = "Naruto";

            var executeBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))?.AsButton();
            Assert.NotNull(executeBtn);
            executeBtn.Invoke();
            Thread.Sleep(1000);

            var resultsList = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List))?.AsListBox();
            Assert.NotNull(resultsList);
            Assert.True(resultsList.Items.Length > 0);
        }

        [Fact(Skip = "Requires deterministic scored results; the previous test did not inspect fuzzy scores or ordering.")]
        public void T1_Search_04_Fuzzy_Match_Score_Ordering()
        {
            NavigateToSearch();
            var searchBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
            Assert.NotNull(searchBox);
            searchBox.Text = "Slime";

            var executeBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))?.AsButton();
            Assert.NotNull(executeBtn);
            executeBtn.Invoke();
            Thread.Sleep(1000);

            var resultsList = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List))?.AsListBox();
            Assert.NotNull(resultsList);
            Assert.True(resultsList.Items.Length > 0);
        }

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
