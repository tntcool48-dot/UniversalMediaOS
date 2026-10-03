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

        private void NavigateTo(string tabName)
        {
            var button = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName(tabName))?.AsButton();
            Assert.NotNull(button);
            button.Invoke();
            Thread.Sleep(500);
        }

        [Fact]
        public void T1_Search_01_Basic_Keyword_Search()
        {
            NavigateTo("Search");

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
            NavigateTo("Search");
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
            NavigateTo("Search");
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
            NavigateTo("Search");
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

        [Fact(Skip = "Requires stopping or faulting the mock scraper endpoint; the previous test did not create an outage.")]
        public void T1_Search_05_Safe_Error_Display_On_Scraper_Offline()
        {
            NavigateTo("Search");
            var searchBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
            Assert.NotNull(searchBox);
            searchBox.Text = "Offline";

            var executeBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))?.AsButton();
            Assert.NotNull(executeBtn);
            executeBtn.Invoke();
            Thread.Sleep(1000);

            // Verify no crash occurs
            var resultsList = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List))?.AsListBox();
            Assert.NotNull(resultsList);
        }

        [Fact]
        public void T2_Search_01_Empty_Search_Input()
        {
            NavigateTo("Search");
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
            NavigateTo("Search");
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
            NavigateTo("Search");
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

        [Fact(Skip = "Requires an explicit null/empty mock response; the previous test relied on an uncontrolled query string.")]
        public void T2_Search_04_Scraper_Returns_Empty_Or_Null()
        {
            NavigateTo("Search");
            var searchBox = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))?.AsTextBox();
            Assert.NotNull(searchBox);
            searchBox.Text = "NonExistentShow12345";

            var executeBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("ExecuteSearch"))?.AsButton();
            Assert.NotNull(executeBtn);
            executeBtn.Invoke();
            Thread.Sleep(1000);

            // Verify no crash
            var resultsList = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List))?.AsListBox();
            Assert.NotNull(resultsList);
        }

        [Fact]
        public void T2_Search_05_Rapid_Concurrent_Double_Searches()
        {
            NavigateTo("Search");
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
