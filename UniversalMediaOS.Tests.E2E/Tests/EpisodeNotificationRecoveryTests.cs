using System.IO;
using System.Net;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class EpisodeNotificationRecoveryTests
{
    [Theory]
    [InlineData(503, "{}")]
    [InlineData(200, "not json")]
    [InlineData(200, "{\"data\":{\"Page\":{}}}")]
    [InlineData(200, "{\"data\":{\"Page\":{\"media\":{}}}}")]
    [InlineData(200, "{\"errors\":[{\"message\":\"Controlled outage\"}],\"data\":{\"Page\":{\"media\":[]}}}")]
    public async Task LaterBatchFailureKeepsEveryFavoriteUntilCompleteRecovery(int status, string body)
    {
        using var profile = new Profile(51);
        int requests = 0;
        bool recovering = false;
        using var server = new MockHttpServer(aniListFeed: request =>
        {
            int[] ids = ReadIds(request);
            int count = Interlocked.Increment(ref requests);
            return !recovering && count == 2 ? (status, body) : (200, Available(ids, 4));
        });
        server.Start();
        var monitor = profile.Monitor(server);
        string original = File.ReadAllText(profile.FavoritesPath);
        string config = File.ReadAllText(profile.ConfigPath);
        Assert.Empty(await monitor.CheckSafelyAsync());
        Assert.Equal(2, requests);
        Assert.Equal(original, File.ReadAllText(profile.FavoritesPath));
        Assert.Equal(config, File.ReadAllText(profile.ConfigPath));
        recovering = true;
        var alerts = await monitor.CheckSafelyAsync();
        Assert.Equal(51, alerts.Count);
        Assert.All(profile.Favorites.GetFavorites(), item => Assert.Equal("Sub 3", item.Progress));
        Assert.Empty(await monitor.CheckSafelyAsync());
        Assert.Equal(6, requests);
    }

    [Fact]
    public async Task SameTitleFavoritesProduceSeparatePersistentAlertsAndNeverDuplicateOnRepeat()
    {
        using var profile = new Profile(2);
        using var server = new MockHttpServer(aniListFeed: request => (200, Available(ReadIds(request), 4)));
        server.Start();
        var monitor = profile.Monitor(server);
        var history = new EpisodeAlertHistoryService(profile.HistoryPath);
        var alerts = await monitor.CheckSafelyAsync();
        Assert.Equal(2, alerts.Count);
        Assert.Equal(new[] { 1, 2 }, alerts.Select(alert => alert.AniListId).Order());
        Assert.Equal(new[] { 1001, 1002 }, alerts.Select(alert => alert.MalId).Order());
        Assert.Equal(2, history.AddAlerts(alerts).Count);
        var reloaded = new EpisodeAlertHistoryService(profile.HistoryPath).GetHistory();
        Assert.Equal(new[] { 1, 2 }, reloaded.Select(alert => alert.AniListId).Order());
        Assert.Equal(new[] { 1001, 1002 }, reloaded.Select(alert => alert.MalId).Order());
        Assert.Equal(new[] { "Anime - 2001", "Anime - 2002" }, reloaded.Select(alert => alert.MediaText).Order());
        Assert.Empty(history.AddAlerts(alerts));
        Assert.Empty(history.AddAlerts(alerts.Select(alert => alert with { Title = "Renamed airing title" })));
        Assert.Empty(await monitor.CheckSafelyAsync());
    }

    [Fact]
    public void LegacyHistoryRemainsIntactWithoutGuessingAnIdentityFromItsTitle()
    {
        using var profile = new Profile(2);
        Guid id = Guid.NewGuid();
        DateTime created = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(profile.HistoryPath, JsonSerializer.Serialize(new[] { new { Id = id,
            Title = "Shared airing title", PreviousEpisode = 2, AvailableEpisode = 3, CreatedAtUtc = created } }));
        string original = File.ReadAllText(profile.HistoryPath);
        var history = new EpisodeAlertHistoryService(profile.HistoryPath);
        Assert.Equal(original, File.ReadAllText(profile.HistoryPath));
        Assert.Equal(0, Assert.Single(history.GetHistory()).AniListId);
        Assert.Equal(2, history.AddAlerts([
            new("Shared airing title", 2, 3) { AniListId = 1, MalId = 1001, Year = "2001" },
            new("Shared airing title", 2, 3) { AniListId = 2, MalId = 1002, Year = "2002" }
        ]).Count);
        var reloaded = new EpisodeAlertHistoryService(profile.HistoryPath).GetHistory();
        Assert.Equal(3, reloaded.Count);
        var legacy = reloaded.Single(item => item.Id == id);
        Assert.Equal(0, legacy.AniListId);
        Assert.Equal(0, legacy.MalId);
        Assert.Equal(created, legacy.CreatedAtUtc);
        Assert.Equal("Shared airing title", legacy.Title);
        Assert.Equal("", legacy.MediaText);
    }

    [Fact]
    public void HistoryPrefersConflictingAniListIdentityAndKeepsDistinctMalOnlyAlerts()
    {
        using var profile = new Profile(0);
        var history = new EpisodeAlertHistoryService(profile.HistoryPath);
        Assert.Equal(4, history.AddAlerts([
            new("Shared title", 2, 3) { AniListId = 1, MalId = 1001 },
            new("Shared title", 2, 3) { AniListId = 2, MalId = 1001 },
            new("Shared title", 2, 3) { MalId = 1001 },
            new("Shared title", 2, 3) { MalId = 1002 }
        ]).Count);
        Assert.Empty(history.AddAlerts([new("Renamed", 2, 3) { AniListId = 1, MalId = 9999 }]));
        Assert.Empty(history.AddAlerts([new("Renamed", 2, 3) { MalId = 1001 }]));
        Assert.Equal(4, new EpisodeAlertHistoryService(profile.HistoryPath).GetHistory().Count);
    }

    [Fact]
    public async Task ResponseForAnIdOutsideItsRequestedBatchCannotUpdateThatFavorite()
    {
        using var profile = new Profile(51);
        int pendingId = profile.Favorites.GetFavorites().Last().AniListId;
        using var server = new MockHttpServer(aniListFeed: request =>
        {
            int[] ids = ReadIds(request);
            return ids.Length == 50 ? (200, Available(ids.Append(pendingId), 4)) : (200, Available([], 4));
        });
        server.Start();
        var alerts = await profile.Monitor(server).CheckSafelyAsync();
        Assert.Equal(50, alerts.Count);
        Assert.Equal("Sub 2", profile.Favorites.GetFavorites().Single(item => item.AniListId == pendingId).Progress);
    }

    [Fact]
    public async Task UnknownAiringCountPreservesKnownAvailabilityWithoutAnAlert()
    {
        using var profile = new Profile(2);
        using var server = new MockHttpServer(aniListFeed: request =>
            (200, JsonSerializer.Serialize(new { data = new { Page = new { media = ReadIds(request)
                .Select(id => new { id, status = "RELEASING", nextAiringEpisode = (object?)null }) } } })));
        server.Start();
        var monitor = profile.Monitor(server);
        string original = File.ReadAllText(profile.FavoritesPath);
        Assert.Empty(await monitor.CheckSafelyAsync());
        Assert.Equal(original, File.ReadAllText(profile.FavoritesPath));
    }

    [Fact]
    public async Task DisabledAlertsAndCallerCancellationDoNotPublishOrChangeTheSavedFavorites()
    {
        using var profile = new Profile(2);
        int requests = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var server = new MockHttpServer(aniListFeed: request =>
        {
            Interlocked.Increment(ref requests);
            int[] ids = ReadIds(request);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return (200, Available(ids, 4));
        });
        server.Start();
        var monitor = profile.Monitor(server);
        Assert.True(profile.Config.SetSettings(new Dictionary<string, string> { ["NewEpisodeAlerts"] = "false" }));
        string original = File.ReadAllText(profile.FavoritesPath);
        Assert.Empty(await monitor.CheckSafelyAsync());
        Assert.Equal(0, requests);
        Assert.True(profile.Config.SetSettings(new Dictionary<string, string> { ["NewEpisodeAlerts"] = "true" }));
        using var cancellation = new CancellationTokenSource();
        var check = monitor.CheckSafelyAsync(cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
            Assert.Equal(original, File.ReadAllText(profile.FavoritesPath));
        }
        finally { release.Set(); }
    }

    internal static int[] ReadIds(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        return document.RootElement.GetProperty("variables").GetProperty("ids")
            .EnumerateArray().Select(id => id.GetInt32()).ToArray();
    }

    internal static string Available(IEnumerable<int> ids, int nextEpisode) => JsonSerializer.Serialize(new
    {
        data = new { Page = new { media = ids.Select(id => new { id, status = "RELEASING", episodes = 12,
            nextAiringEpisode = new { episode = nextEpisode } }) } }
    });

    private sealed class Profile : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UMOS-Notification-Recovery-" + Guid.NewGuid().ToString("N"));
        public string FavoritesPath => Path.Combine(_root, "favorites.json");
        public string ConfigPath => Path.Combine(_root, "config.json");
        public string HistoryPath => Path.Combine(_root, "episode-alert-history.json");
        public DomainHotSwapper Config { get; }
        public FavoriteMediaService Favorites { get; }
        public Profile(int count)
        {
            Directory.CreateDirectory(_root);
            Config = new DomainHotSwapper(ConfigPath);
            Favorites = new FavoriteMediaService(FavoritesPath);
            for (int id = 1; id <= count; id++)
                Assert.True(Favorites.Toggle(new MediaResult { Id = id, IdMal = 1000 + id,
                    OfficialTitle = "Shared airing title", DisplayYear = (2000 + id).ToString(),
                    DisplayStatus = "Releasing", AvailableSubEpisodes = 2 }));
        }
        public NewEpisodeMonitorService Monitor(MockHttpServer server)
        {
            Assert.True(Config.SetSettings(new Dictionary<string, string> { ["AniListUrl"] = server.BaseUrl + "graphql" }));
            return new(Config, Favorites);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
