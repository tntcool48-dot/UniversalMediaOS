using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class EpisodeNotificationRecoveryUiTests
{
    [Fact]
    public void AppMonitorOutageRecoveryAndRestartKeepTwoSameTitleAlertsAndLegacyHistorySeparate()
    {
        int recovering = 0;
        int checks = 0;
        Guid legacyId = Guid.NewGuid();
        DateTime legacyDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var fixture = new AppFixture(mangaPagePng: null, initializeProfile: profile =>
        {
            var config = new DomainHotSwapper(Path.Combine(profile, "Roaming", "UniversalMediaOS", "config.json"));
            Assert.True(config.SetSettings(new Dictionary<string, string>
                { ["NewEpisodeAlerts"] = "true", ["AutoSyncMal"] = "false" }));
            var favorites = new FavoriteMediaService(Path.Combine(profile, "Roaming", "UniversalMediaOS", "favorites.json"));
            foreach (int id in new[] { 101, 102 })
                Assert.True(favorites.Toggle(new MediaResult { Id = id, IdMal = id + 100,
                    OfficialTitle = "Shared airing title", DisplayYear = id == 101 ? "2010" : "2020",
                    DisplayStatus = "Releasing", AvailableSubEpisodes = 2 }));
            Assert.True(favorites.Toggle(new MediaResult { Id = 103, IdMal = 203, OfficialTitle = "Adjacent finished title",
                DisplayYear = "2000", DisplayStatus = "Finished", AvailableSubEpisodes = 5 }));
            string historyPath = Path.Combine(profile, "Local", "UniversalMediaOS", "episode-alert-history.json");
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            File.WriteAllText(historyPath, JsonSerializer.Serialize(new[] { new { Id = legacyId,
                Title = "Legacy saved alert", PreviousEpisode = 1, AvailableEpisode = 2, CreatedAtUtc = legacyDate } }));
        }, aniListFeed: request =>
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            if (!document.RootElement.TryGetProperty("variables", out var variables) ||
                !variables.TryGetProperty("ids", out var requested))
                return (200, "{\"data\":{\"Page\":{\"media\":[]}}}");
            Interlocked.Increment(ref checks);
            if (Volatile.Read(ref recovering) == 0) return (503, "{}");
            return (200, JsonSerializer.Serialize(new { data = new { Page = new { media = requested.EnumerateArray()
                .Select(item => new { id = item.GetInt32(), status = item.GetInt32() == 103 ? "FINISHED" : "RELEASING",
                    episodes = item.GetInt32() == 103 ? 5 : 12,
                    nextAiringEpisode = item.GetInt32() == 103 ? null : new { episode = 4 } }) } } }));
        });
        string favoritesPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "favorites.json");
        string configPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "config.json");
        string historyFile = Path.Combine(fixture.SandboxPath, "Local", "UniversalMediaOS", "episode-alert-history.json");
        string originalFavorites = File.ReadAllText(favoritesPath);
        string originalConfig = File.ReadAllText(configPath);
        string originalHistory = File.ReadAllText(historyFile);
        var favoritesService = new FavoriteMediaService(favoritesPath);
        string adjacent = JsonSerializer.Serialize(favoritesService.GetFavorites().Single(item => item.AniListId == 103));
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        bool Has(string name) => Window().FindAllDescendants(cf => cf.ByName(name)).Any(element => !element.IsOffscreen);
        Button Button(string name) => Window().FindAllDescendants(cf => cf.ByName(name))
            .First(element => !element.IsOffscreen && element.ControlType == ControlType.Button).AsButton();
        void WaitFor(string name) => Assert.True(SpinWait.SpinUntil(() => Has(name), TimeSpan.FromSeconds(8)), name);
        void WaitForCheck(int count) => Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref checks) >= count,
            TimeSpan.FromSeconds(10)), "The app's background availability monitor must check the owned localhost endpoint.");
        void Notifications()
        {
            Window().FindFirstDescendant(cf => cf.ByAutomationId("OpenSettings"))!.AsButton().Invoke();
            Button("Notifications").Invoke();
            WaitFor("Recent episode alerts");
        }
        int HistoryRows() => Window().FindFirstDescendant(cf => cf.ByName("Episode alert history"))!
            .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length;
        void VisualHold(string phase)
        {
            string? directory = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_NOTIFICATION_UI_QA_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "notification-ui-ready.json"), JsonSerializer.Serialize(new
            {
                Phase = phase, Pid = fixture.App.ProcessId, Window = Window().Properties.NativeWindowHandle.Value.ToInt64(),
                Profile = fixture.SandboxPath, Checks = Volatile.Read(ref checks), Utc = DateTime.UtcNow
            }));
            if (int.TryParse(Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_NOTIFICATION_UI_HOLD_MS"), out int hold))
                Thread.Sleep(Math.Clamp(hold, 0, 60_000));
        }
        WaitForCheck(1);
        Button("My List").Invoke();
        WaitFor("All (3)");
        WaitFor("Available: Sub 2");
        Assert.Equal(originalFavorites, File.ReadAllText(favoritesPath));
        Assert.Equal(originalHistory, File.ReadAllText(historyFile));
        Notifications();
        WaitFor("Legacy saved alert");
        Assert.Equal(1, HistoryRows());
        Assert.False(Has("Shared airing title"));
        VisualHold("outage-kept-favorites-and-legacy-history");
        int oldPid = fixture.App.ProcessId;
        Volatile.Write(ref recovering, 1);
        fixture.Restart();
        Assert.NotEqual(oldPid, fixture.App.ProcessId);
        Notifications();
        WaitForCheck(2);
        WaitFor("Anime - 2010");
        WaitFor("Anime - 2020");
        WaitFor("Episode 3 is available");
        Assert.Equal(3, HistoryRows());
        var recovered = new EpisodeAlertHistoryService(historyFile).GetHistory();
        Assert.Equal(new[] { 101, 102 }, recovered.Where(item => item.AniListId > 0).Select(item => item.AniListId).Order());
        Assert.Equal(new[] { 201, 202 }, recovered.Where(item => item.AniListId > 0).Select(item => item.MalId).Order());
        var legacy = recovered.Single(item => item.Id == legacyId);
        Assert.Equal(legacyDate, legacy.CreatedAtUtc);
        Assert.Equal(0, legacy.AniListId);
        Assert.Equal(adjacent, JsonSerializer.Serialize(favoritesService.GetFavorites().Single(item => item.AniListId == 103)));
        Assert.Equal(originalConfig, File.ReadAllText(configPath));
        VisualHold("two-known-identities-recovered-with-legacy-history");
        string recoveredHistory = File.ReadAllText(historyFile);
        string recoveredFavorites = File.ReadAllText(favoritesPath);
        oldPid = fixture.App.ProcessId;
        fixture.Restart();
        Assert.NotEqual(oldPid, fixture.App.ProcessId);
        Notifications();
        WaitFor("Anime - 2010");
        WaitFor("Anime - 2020");
        WaitForCheck(3);
        Assert.Equal(3, HistoryRows());
        Assert.Equal(recoveredHistory, File.ReadAllText(historyFile));
        Assert.Equal(recoveredFavorites, File.ReadAllText(favoritesPath));
        Assert.Equal(originalConfig, File.ReadAllText(configPath));
        VisualHold("same-identities-restored-without-repeat-alerts");
        Button("My List").Invoke();
        WaitFor("All (3)");
        WaitFor("Available: Sub 3");
        WaitFor("Available: Sub 5");
        WaitFor("In progress (0)");
        WaitFor("Completed (0)");
        Assert.Equal(recoveredHistory, File.ReadAllText(historyFile));
        Assert.False(fixture.App.HasExited);
    }
}
