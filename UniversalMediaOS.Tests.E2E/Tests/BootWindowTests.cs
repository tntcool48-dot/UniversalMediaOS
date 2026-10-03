using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    [Collection("Application Collection")]
    public class BootWindowTests
    {
        private readonly AppFixture _fixture;

        public BootWindowTests(AppFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void MainWindow_IsFoundByExpectedTitle()
        {
            Assert.NotNull(_fixture.MainWindow);
            Assert.Equal(AppFixture.ExpectedMainWindowTitle, _fixture.MainWindow.Title);
        }
    }
}
