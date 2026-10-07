using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class VoiceCastRecoveryUiTests
{
    [Fact]
    public async Task ManualCastOutageRetryCancelAndModeChangePreserveSeparateCachesAcrossRestart()
    {
        int mode = 0;
        int requests = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fixture = new AppFixture(mangaPagePng: null, initializeProfile: root =>
        {
            var config = new DomainHotSwapper(Path.Combine(root, "Roaming", "UniversalMediaOS", "config.json"));
            Assert.True(config.SetSettings(new Dictionary<string, string> { ["NewEpisodeAlerts"] = "false",
                ["AutoSyncMal"] = "false", ["VaDetectDefaultMode"] = "Sub" }));
            using var db = new DatabaseContext();
            db.EnsureVaDetectSchema();
            db.MalLibraryEntries.AddRange(new MalLibraryEntry { MalId = 101, AniListId = 201, DefaultTitle = "Controlled cast work", ListStatus = "Watching" },
                new MalLibraryEntry { MalId = 202, AniListId = 302, DefaultTitle = "Adjacent title", ListStatus = "Completed", UserScore = 9 });
            db.VoiceCastRecords.AddRange(
                new VoiceCastRecord { MediaKey = "mal:101", MalId = 101, AniListId = 201, ShowTitle = "Controlled cast work", LanguageMode = "Sub", CharacterName = "Saved character", VoiceActorName = "Saved Japanese actor", Source = "Saved", FetchedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new VoiceCastRecord { MediaKey = "mal:101", MalId = 101, AniListId = 201, ShowTitle = "Controlled cast work", LanguageMode = "Dub", CharacterName = "Saved character", VoiceActorName = "Saved English actor", Source = "Saved", FetchedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new VoiceCastRecord { MediaKey = "mal:202", MalId = 202, AniListId = 302, ShowTitle = "Adjacent title", LanguageMode = "Sub", CharacterName = "Adjacent character", VoiceActorName = "Local Japanese actor", Source = "Saved", FetchedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }, aniListFeed: _ => (200, "{\"data\":{\"Page\":{\"pageInfo\":{\"hasNextPage\":false,\"currentPage\":1},\"media\":[{\"id\":201,\"idMal\":101,\"title\":{\"romaji\":\"Controlled cast work\",\"english\":\"Controlled cast work\"},\"coverImage\":{}}]}}}"),
            mangaChapterFeed: _ =>
            {
                Interlocked.Increment(ref requests);
                int current = Volatile.Read(ref mode);
                if (current == 0) return (503, "controlled unavailable page");
                if (current == 2) { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); }
                return (200, "<table><tr><td>Controlled character</td><td>Local Japanese actor</td></tr></table>");
            });
        string configPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "config.json");
        var config = new DomainHotSwapper(configPath);
        string manualUrl = new Uri(config.GetSetting("AniListUrl")).GetLeftPart(UriPartial.Authority) + "/manga/controlled/feed";
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        bool Has(string text) => Window().FindAllDescendants(cf => cf.ByName(text)).Any(element => !element.IsOffscreen);
        Button Button(string text) => Window().FindAllDescendants(cf => cf.ByName(text))
            .First(element => !element.IsOffscreen && element.ControlType == ControlType.Button).AsButton();
        TextBox Textbox(int index) => Window().FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
            .Where(element => !element.IsOffscreen).ElementAt(index).AsTextBox();
        void WaitFor(string text) => Assert.True(SpinWait.SpinUntil(() => Has(text), TimeSpan.FromSeconds(8)), text);
        void Mode(string text) => Window().FindAllDescendants(cf => cf.ByName(text))
            .Single(element => !element.IsOffscreen && element.ControlType == ControlType.Button && element.Patterns.Toggle.IsSupported)
            .Patterns.Toggle.Pattern.Toggle();
        async Task<string> Snapshot(bool adjacentOnly = false)
        {
            await using var db = new DatabaseContext();
            return JsonSerializer.Serialize(await db.VoiceCastRecords.AsNoTracking()
                .Where(row => !adjacentOnly || row.MediaKey != "mal:101" || row.LanguageMode != "Sub").OrderBy(row => row.Id).ToArrayAsync());
        }
        void Hold(string phase)
        {
            string? directory = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_VOICE_CAST_UI_QA_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            string? onlyPhase = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_VOICE_CAST_UI_OBSERVE_PHASE");
            if (!string.IsNullOrWhiteSpace(onlyPhase) && onlyPhase != phase) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "voice-cast-ui-ready.json"), JsonSerializer.Serialize(new
            {
                Phase = phase, Pid = fixture.App.ProcessId, Window = Window().Properties.NativeWindowHandle.Value.ToInt64(),
                Profile = fixture.SandboxPath, Requests = Volatile.Read(ref requests), Utc = DateTime.UtcNow
            }));
            if (int.TryParse(Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_VOICE_CAST_UI_HOLD_MS"), out int hold))
                Thread.Sleep(Math.Clamp(hold, 0, 60_000));
        }
        Button("VA Detect").Invoke();
        WaitFor("2 MAL library entries cached.");
        string originalConfig = File.ReadAllText(configPath);
        string before = await Snapshot();
        string adjacent = await Snapshot(adjacentOnly: true);
        Textbox(0).Text = "Controlled cast work";
        Textbox(1).Text = manualUrl;
        Button("Find VAs").Invoke();
        WaitFor("VA search failed.");
        Assert.Equal(1, Volatile.Read(ref requests));
        WaitFor("Your saved cast was kept. Use Find VAs to retry.");
        Assert.Equal(before, await Snapshot());
        Assert.Equal(originalConfig, File.ReadAllText(configPath));
        Assert.True(Button("Find VAs").IsEnabled);
        Hold("cast-outage-kept-saved-language-and-adjacent-records");
        Volatile.Write(ref mode, 1);
        Button("Find VAs").Invoke();
        WaitFor("Found 1 Sub cast roles and 1 familiar VA matches.");
        WaitFor("Local Japanese actor");
        Assert.False(Has("Saved English actor"));
        Assert.False(Has("Your saved cast was kept. Use Find VAs to retry."));
        Assert.Equal(adjacent, await Snapshot(adjacentOnly: true));
        string recovered = await Snapshot();
        Hold("cast-retry-showed-only-current-sub-cast-and-known-role");
        Volatile.Write(ref mode, 0);
        Button("Find VAs").Invoke();
        WaitFor("VA search failed.");
        WaitFor("Local Japanese actor");
        Assert.Equal(recovered, await Snapshot());
        Volatile.Write(ref mode, 2);
        try
        {
            Button("Find VAs").Invoke();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            WaitFor("Cancel search");
            Assert.False(Button("Find VAs").IsEnabled);
            Button("Cancel search").Invoke();
            WaitFor("VA search cancelled.");
            Assert.Equal(recovered, await Snapshot());
        }
        finally { release.Set(); }
        release.Reset();
        entered.Reset();
        try
        {
            Button("Find VAs").Invoke();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Mode("Dub");
            WaitFor("VA Detect mode: Dub. Search again to refresh matches.");
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => Button("Find VAs").IsEnabled, TimeSpan.FromSeconds(5)));
            Assert.False(Has("Local Japanese actor"));
            Assert.Equal(recovered, await Snapshot());
        }
        finally { release.Set(); }
        Mode("Sub");
        int oldPid = fixture.App.ProcessId;
        fixture.Restart();
        Assert.NotEqual(oldPid, fixture.App.ProcessId);
        Button("VA Detect").Invoke();
        WaitFor("2 MAL library entries cached.");
        Textbox(0).Text = "Controlled cast work";
        int beforeCachedSearch = Volatile.Read(ref requests);
        Button("Find VAs").Invoke();
        WaitFor("Found 1 Sub cast roles and 1 familiar VA matches.");
        Assert.Equal(beforeCachedSearch, Volatile.Read(ref requests));
        Assert.Equal(recovered, await Snapshot());
        Assert.Equal(adjacent, await Snapshot(adjacentOnly: true));
        static string OrderedSettings(string json) => JsonSerializer.Serialize(JsonSerializer.Deserialize<Dictionary<string, string>>(json)!
            .OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
        // Selecting Dub and then Sub legitimately rewrites configuration. Keep
        // every original key/value; the unchanged outage/cancel paths above
        // still compare the original bytes.
        Assert.Equal(OrderedSettings(originalConfig), OrderedSettings(File.ReadAllText(configPath)));
        Hold("cast-cache-and-independent-language-survived-process-restart");
    }
}
