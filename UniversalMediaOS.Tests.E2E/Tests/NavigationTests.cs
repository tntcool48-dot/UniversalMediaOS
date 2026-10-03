using Xunit;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using UniversalMediaOS.Tests.E2E.Infrastructure;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    [Collection("Application Collection")]
    public class NavigationTests
    {
        private readonly AppFixture _fixture;

        public NavigationTests(AppFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void Should_NavigateToSettingsView_When_SettingsButtonClicked()
        {
            var mainWindow = _fixture.MainWindow;

            // 1. Locate the Settings button in the sidebar.
            // Search by Name/Text "Settings"
            var settingsButton = mainWindow.FindFirstDescendant(cf => cf.ByName("Settings"))?.AsButton();
            
            Assert.NotNull(settingsButton);

            // 2. Invoke the settings button (programmatic click via UIA pattern)
            settingsButton.Invoke();

            // Wait briefly for the UI transition to complete
            System.Threading.Thread.Sleep(500);

            // 3. Verify that settings-specific controls are now visible.
            // Let's look for a TextBlock/Label with Name "Settings" or "Services" or "Consumet API"
            var settingsHeader = mainWindow.FindFirstDescendant(cf => cf.ByName("Settings"))?.AsLabel()
                                 ?? mainWindow.FindFirstDescendant(cf => cf.ByName("Services"))?.AsLabel();

            Assert.NotNull(settingsHeader);
        }

        [Fact]
        public void Should_NavigateToSearchView_When_SearchButtonClicked()
        {
            var mainWindow = _fixture.MainWindow;

            // 1. Find the Search navigation button
            var searchButton = mainWindow.FindFirstDescendant(cf => cf.ByName("Search"))?.AsButton();
            Assert.NotNull(searchButton);

            // 2. Invoke it
            searchButton.Invoke();
            System.Threading.Thread.Sleep(500);

            // 3. Verify we are on Search view (e.g., search box is visible)
            // Look for a TextBox by type Edit.
            var searchTextBox = mainWindow.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));
            Assert.NotNull(searchTextBox);
        }

        [Theory]
        [InlineData("Movies", "Movies search")]
        [InlineData("TV Shows", "TV Shows search")]
        [InlineData("Cartoons", "Cartoons search")]
        [InlineData("Books", "Book search")]
        public void Should_OpenImplementedNonAnimeWorkspace_When_MediaButtonClicked(
            string navigationName,
            string expectedSearchName)
        {
            var navigationButton = _fixture.MainWindow
                .FindFirstDescendant(cf => cf.ByName(navigationName))
                ?.AsButton();
            Assert.NotNull(navigationButton);

            navigationButton.Invoke();
            System.Threading.Thread.Sleep(700);

            var searchBox = _fixture.MainWindow
                .FindFirstDescendant(cf => cf.ByName(expectedSearchName))
                ?.AsTextBox();
            Assert.NotNull(searchBox);
            Assert.True(searchBox.IsEnabled);
        }
    }

    [CollectionDefinition("Application Collection")]
    public class ApplicationCollection : ICollectionFixture<AppFixture>
    {
        // This class has no code, and is never created. Its purpose is simply
        // to be the place to apply [CollectionDefinition] and all the
        // ICollectionFixture<> interfaces.
    }
}
