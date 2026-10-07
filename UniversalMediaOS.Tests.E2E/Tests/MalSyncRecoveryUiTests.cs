using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MalSyncRecoveryUiTests
{
    [Fact]
    public async Task OAuthFailureRetryAndCancelKeepAdjacentEntriesAndCredentialsAcrossProcessRestart()
    {
        int mode = 0;
        int requests = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fixture = new AppFixture(mangaPagePng: null, initializeProfile: profile =>
        {
            var config = new DomainHotSwapper(Path.Combine(profile, "Roaming", "UniversalMediaOS", "config.json"));
            Assert.True(config.SetSettings(new Dictionary<string, string> { ["MalOAuthToken"] = "controlled-fixture-token",
                ["NewEpisodeAlerts"] = "false", ["AutoSyncMal"] = "false" }));
            using var db = new DatabaseContext();
            db.EnsureVaDetectSchema();
            db.MalLibraryEntries.AddRange(
                new MalLibraryEntry { MalId = 101, AniListId = 201, DefaultTitle = "Saved title", ListStatus = "Watching", WatchedEpisodes = 2, TotalEpisodes = 12 },
                new MalLibraryEntry { MalId = 202, AniListId = 302, DefaultTitle = "Adjacent title", ListStatus = "Completed", WatchedEpisodes = 5, TotalEpisodes = 5 });
            db.SaveChanges();
        }, malLibraryFeed: request =>
        {
            Interlocked.Increment(ref requests);
            int current = Volatile.Read(ref mode);
            if (current == 0) return (200, "not json");
            if (current == 2) { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); }
            return (200, request.QueryString["status"] == "watching" ? MalSyncRecoveryTests.OAuthEntry : "{\"data\":[]}");
        });
        string configPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "config.json");
        string originalConfig = File.ReadAllText(configPath);
        async Task<string> Snapshot()
        {
            await using var db = new DatabaseContext();
            return JsonSerializer.Serialize(await db.MalLibraryEntries.AsNoTracking().OrderBy(item => item.MalId).ToArrayAsync());
        }
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        bool Has(string name) => Window().FindAllDescendants(cf => cf.ByName(name)).Any(element => !element.IsOffscreen);
        Button Button(string name) => Window().FindAllDescendants(cf => cf.ByName(name))
            .First(element => !element.IsOffscreen && element.ControlType == ControlType.Button).AsButton();
        void WaitFor(string name) => Assert.True(SpinWait.SpinUntil(() => Has(name), TimeSpan.FromSeconds(8)), name);
        void VisualHold(string phase)
        {
            string? directory = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_MAL_SYNC_UI_QA_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "mal-sync-ui-ready.json"), JsonSerializer.Serialize(new
            {
                Phase = phase, Pid = fixture.App.ProcessId, Window = Window().Properties.NativeWindowHandle.Value.ToInt64(),
                Profile = fixture.SandboxPath, Requests = Volatile.Read(ref requests), Utc = DateTime.UtcNow
            }));
            if (int.TryParse(Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_MAL_SYNC_UI_HOLD_MS"), out int hold))
                Thread.Sleep(Math.Clamp(hold, 0, 60_000));
        }
        Button("VA Detect").Invoke();
        WaitFor("2 MAL library entries cached.");
        string original = await Snapshot();
        Button("Sync OAuth").Invoke();
        WaitFor("MAL sync failed.");
        WaitFor("Your saved library was kept. Use Sync OAuth to retry.");
        Assert.True(Button("Sync OAuth").IsEnabled);
        Assert.Equal(original, await Snapshot());
        Assert.Equal(originalConfig, File.ReadAllText(configPath));
        VisualHold("oauth-failure-saved-library-kept");
        Volatile.Write(ref mode, 1);
        Button("Sync OAuth").Invoke();
        WaitFor("OAuth sync complete: 1 anime imported.");
        Assert.False(Has("Your saved library was kept. Use Sync OAuth to retry."));
        await using (var db = new DatabaseContext())
        {
            var saved = await db.MalLibraryEntries.AsNoTracking().OrderBy(item => item.MalId).ToArrayAsync();
            Assert.Equal(2, saved.Length);
            Assert.Equal(201, saved[0].AniListId);
            Assert.Equal(3, saved[0].WatchedEpisodes);
            Assert.Equal(302, saved[1].AniListId);
            Assert.Equal(5, saved[1].WatchedEpisodes);
        }
        Assert.Equal(originalConfig, File.ReadAllText(configPath));
        VisualHold("oauth-retry-updated-only-known-identity");
        string afterRetry = await Snapshot();
        Volatile.Write(ref mode, 2);
        try
        {
            Button("Sync OAuth").Invoke();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "The controlled request must be active before cancellation.");
            WaitFor("Cancel sync");
            Assert.False(Button("Sync OAuth").IsEnabled);
            Button("Cancel sync").Invoke();
            WaitFor("MAL sync cancelled.");
            Assert.Equal(afterRetry, await Snapshot());
            Assert.True(Button("Sync OAuth").IsEnabled);
        }
        finally { release.Set(); }
        int oldPid = fixture.App.ProcessId;
        fixture.Restart();
        Assert.NotEqual(oldPid, fixture.App.ProcessId);
        Button("VA Detect").Invoke();
        WaitFor("2 MAL library entries cached.");
        Assert.Equal(afterRetry, await Snapshot());
        Assert.Equal(originalConfig, File.ReadAllText(configPath));
        VisualHold("saved-library-after-cancel-and-process-restart");
        Assert.False(fixture.App.HasExited);
    }
}
