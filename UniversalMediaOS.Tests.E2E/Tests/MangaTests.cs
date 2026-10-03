using System;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    [Collection("Application Collection")]
    public sealed class MangaTests
    {
        private readonly AppFixture _fixture;

        public MangaTests(AppFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void MangaWorkspace_ExposesAnEditableSearchAndWiredSearchCommand()
        {
            Button library = Assert.IsType<Button>(
                _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Library"))?.AsButton());
            library.Invoke();

            Button searchButton = WaitForButton("ExecuteSearch");
            TextBox? searchBox = _fixture.MainWindow
                .FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))
                ?.AsTextBox();

            Assert.NotNull(searchBox);
            searchBox.Text = "Monster";

            Assert.Equal("Monster", searchBox.Text);
            Assert.True(searchButton.IsEnabled);
            Assert.True(searchButton.IsOffscreen is false);
        }

        private Button WaitForButton(string name)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                Button? button = _fixture.MainWindow
                    .FindFirstDescendant(cf => cf.ByName(name))
                    ?.AsButton();
                if (button != null)
                {
                    return button;
                }

                Thread.Sleep(50);
            }
            while (DateTime.UtcNow < deadline);

            throw new Xunit.Sdk.XunitException($"Button '{name}' did not appear in the active manga workspace.");
        }
    }
}
