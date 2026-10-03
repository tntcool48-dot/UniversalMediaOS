using System.IO;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class FavoriteMediaRegressionTests
    {
        [Fact]
        public void AvailabilityRefresh_UpdatesProgressAndStatusWithoutLeavingMyListStale()
        {
            string root = Path.Combine(Path.GetTempPath(), "umos-favorites-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var service = new FavoriteMediaService(Path.Combine(root, "favorites.json"));
                service.Toggle(new MediaResult
                {
                    Id = 42,
                    OfficialTitle = "Example Anime",
                    DisplayStatus = "Releasing",
                    AvailableSubEpisodes = 1
                });

                var alerts = service.UpdateAvailability(new Dictionary<int, FavoriteMediaAvailability>
                {
                    [42] = new(2, "Finished")
                });

                var saved = Assert.Single(service.GetFavorites());
                Assert.Equal("Finished", saved.Status);
                Assert.Equal("Sub 2", saved.Progress);
                Assert.Single(alerts);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
