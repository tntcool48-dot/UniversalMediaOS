using System.IO;
using System.Text.Json;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.Helpers;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class EpisodeAlertHistoryTests
    {
        [Fact]
        public void AlertHistory_PersistsNewestFirstAndCanBeCleared()
        {
            string root = CreateTemporaryDirectory();
            try
            {
                string path = Path.Combine(root, "episode-alerts.json");
                var history = new EpisodeAlertHistoryService(path);

                history.AddAlerts([
                    new NewEpisodeAlert("First Show", 2, 3),
                    new NewEpisodeAlert("Second Show", 5, 6)
                ]);

                var reloaded = new EpisodeAlertHistoryService(path);
                Assert.Collection(
                    reloaded.GetHistory(),
                    item => Assert.Equal("Second Show", item.Title),
                    item => Assert.Equal("First Show", item.Title));
                Assert.Equal("Episode 6 is available", reloaded.GetHistory()[0].EpisodeText);

                reloaded.Clear();
                Assert.Empty(new EpisodeAlertHistoryService(path).GetHistory());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void AlertHistory_IsBoundedAndIgnoresDuplicateAvailability()
        {
            string root = CreateTemporaryDirectory();
            try
            {
                string path = Path.Combine(root, "episode-alerts.json");
                var history = new EpisodeAlertHistoryService(path, capacity: 3);
                int changeCount = 0;
                history.HistoryChanged += (_, _) => changeCount++;

                for (int episode = 1; episode <= 5; episode++)
                {
                    history.AddAlerts([new NewEpisodeAlert("Bounded Show", episode - 1, episode)]);
                }

                Assert.Equal(new[] { 5, 4, 3 }, history.GetHistory().Select(item => item.AvailableEpisode));
                Assert.Empty(history.AddAlerts([new NewEpisodeAlert("Bounded Show", 4, 5)]));
                Assert.Equal(5, changeCount);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void AlertHistory_RecoversFromMalformedLocalFile()
        {
            string root = CreateTemporaryDirectory();
            try
            {
                string path = Path.Combine(root, "episode-alerts.json");
                File.WriteAllText(path, "{ definitely not json");

                var history = new EpisodeAlertHistoryService(path);
                Assert.Empty(history.GetHistory());

                history.AddAlerts([new NewEpisodeAlert("Recovered Show", 7, 8)]);
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Single(saved.RootElement.EnumerateArray());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void WindowsNotifier_FailsGracefullyForEmptyMessages()
        {
            using var notifier = new WindowsNotificationService();
            Assert.False(notifier.TryShow("New episode", ""));
        }

        private static string CreateTemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "umos-alerts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
