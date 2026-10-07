using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class VoiceCastRecoveryTests
{
    [Fact]
    public async Task EmptySuccessfulManualRefreshKeepsUsefulSavedCastAndReportsItsFallback()
    {
        using var profile = new Profile();
        string before = await profile.Snapshot();
        using var client = new HttpClient(new Feed((_, _) => Task.FromResult(Response(200, "<html><body>No roles supplied.</body></html>"))));
        var result = await new VoiceCastService(profile.Config, client).FetchAndCacheCastAsync(profile.Media,
            VoiceLanguageMode.Sub, "https://controlled.invalid/manual", bypassCache: true);
        Assert.True(result.FromCache);
        Assert.False(result.NotFound);
        Assert.Equal("Saved Japanese actor", Assert.Single(result.Cast).VoiceActorName);
        Assert.Contains("saved cast was kept", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await profile.Snapshot());
    }

    [Fact]
    public async Task PrefetchNoticeCannotReplaceCurrentCastSearchStatusAndIdleActionsAreHonest()
    {
        using var profile = new Profile();
        using var client = new HttpClient(new Feed((_, _) => throw new InvalidOperationException("The saved cast and empty library must not make a network request.")));
        using var view = await profile.View(client);
        view.LoadMedia(new MediaResult { Id = 201, IdMal = 101, OfficialTitle = "Target A" });
        await view.SearchCommand.ExecuteAsync(null);
        string resultStatus = view.StatusText;
        Assert.Contains("Found 1 Sub", resultStatus);
        Assert.True(view.StartPrefetchCommand.CanExecute(null));
        Assert.False(view.StopPrefetchCommand.CanExecute(null));
        await view.StartPrefetchCommand.ExecuteAsync(null);
        var service = (BackgroundVoiceCastPrefetchService)typeof(VaDetectViewModel).GetField("_prefetchService",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
        await PrefetchTask(service).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(resultStatus, view.StatusText);
        Assert.Contains("VA prefetch complete", view.PrefetchStatusText);
        Assert.False(view.IsPrefetching);
        Assert.True(view.StartPrefetchCommand.CanExecute(null));
        Assert.False(view.StopPrefetchCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SearchCancelDeadlineAndCloseKeepAllSavedCastAndConfiguration(int finish)
    {
        using var profile = new Profile();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Feed(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancellation must end the controlled request.");
        }));
        using var view = await profile.View(client, finish == 1 ? TimeSpan.FromMilliseconds(250) : null);
        view.LoadMedia(new MediaResult { Id = 201, IdMal = 101, OfficialTitle = "Target A" });
        view.ManualUrl = "https://controlled.invalid/manual";
        string before = await profile.Snapshot();
        string config = File.ReadAllText(profile.ConfigPath);
        Task search = view.SearchCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(view.IsSearching);
        Assert.True(view.CancelCastSearchCommand.CanExecute(null));
        Assert.False(view.SyncLibraryCommand.CanExecute(null));
        string statusBeforeClose = view.StatusText;
        if (finish == 0) view.CancelCastSearchCommand.Execute(null);
        if (finish == 2) view.Dispose();
        await search.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(view.IsSearching);
        Assert.Equal(before, await profile.Snapshot());
        Assert.Equal(config, File.ReadAllText(profile.ConfigPath));
        Assert.Empty(view.TargetCast);
        Assert.Equal(finish switch { 0 => "VA search cancelled.", 1 => "VA search timed out.", _ => statusBeforeClose }, view.StatusText);
    }

    [Fact]
    public async Task PrefetchContinuesPastUnavailableWorkAndRetriesAnOldNegativeCache()
    {
        using var profile = new Profile();
        await profile.PrepareMissingLibrary();
        using var client = new HttpClient(new Feed(async (request, token) =>
        {
            if (request.Method != HttpMethod.Post) return Response(503, "unavailable");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            int id = body.RootElement.GetProperty("variables").GetProperty("id").GetInt32();
            return id == 201 ? Response(503, "unavailable") : Response(200, CastBody(302, 202));
        }));
        var cast = new VoiceCastService(profile.Config, client);
        using var prefetch = new BackgroundVoiceCastPrefetchService(cast);
        VoiceCastPrefetchProgressEventArgs? final = null;
        prefetch.ProgressChanged += (_, progress) => { if (!progress.IsRunning) final = progress; };
        await prefetch.StartAsync(VoiceLanguageMode.Sub, 2, TimeSpan.Zero);
        await PrefetchTask(prefetch).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(final);
        Assert.Equal(2, final.Processed);
        Assert.Equal(1, final.Found);
        Assert.Equal(1, final.Failed);
        await using var db = new DatabaseContext();
        Assert.Equal("Japanese actor", (await db.VoiceCastRecords.SingleAsync(row => row.MediaKey == "mal:202")).VoiceActorName);
        Assert.Equal("Saved English actor", (await db.VoiceCastRecords.SingleAsync(row => row.MediaKey == "mal:101" && row.LanguageMode == "Dub")).VoiceActorName);
        Assert.True((await db.VoiceCastRecords.SingleAsync(row => row.MediaKey == "mal:101" && row.LanguageMode == "Sub")).NotFound);
    }

    [Fact]
    public async Task DisposedPrefetchCancelsItsRequestAndCannotRestartAnotherWorker()
    {
        using var profile = new Profile();
        await profile.PrepareMissingLibrary();
        int requests = 0;
        bool recovered = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Feed(async (_, token) =>
        {
            Interlocked.Increment(ref requests);
            if (!recovered) { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return Response(200, CastBody(201, 101));
        }));
        using var prefetch = new BackgroundVoiceCastPrefetchService(new VoiceCastService(profile.Config, client));
        await prefetch.StartAsync(VoiceLanguageMode.Sub, 1, TimeSpan.Zero);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        prefetch.Dispose();
        await PrefetchTask(prefetch).WaitAsync(TimeSpan.FromSeconds(3));
        int stopped = Volatile.Read(ref requests);
        recovered = true;
        await prefetch.StartAsync(VoiceLanguageMode.Sub, 1, TimeSpan.Zero);
        await PrefetchTask(prefetch).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(stopped, Volatile.Read(ref requests));
        Assert.False(prefetch.IsRunning);
    }

    private static Task PrefetchTask(BackgroundVoiceCastPrefetchService service) =>
        (Task?)typeof(BackgroundVoiceCastPrefetchService).GetField("_activeTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service) ?? Task.CompletedTask;

    [Fact]
    public async Task FailedManualRefreshPreservesSavedCastAndIndependentLanguageAndTitle()
    {
        using var profile = new Profile();
        string before = await profile.Snapshot();
        using var client = new HttpClient(new Feed((_, _) => Task.FromResult(Response(503, "unavailable"))));
        var cast = new VoiceCastService(profile.Config, client);
        await Assert.ThrowsAnyAsync<Exception>(() => cast.FetchAndCacheCastAsync(profile.Media, VoiceLanguageMode.Sub,
            "https://controlled.invalid/manual", bypassCache: true));
        Assert.Equal(before, await profile.Snapshot());
    }

    [Fact]
    public async Task IncompleteLookupCannotBecomePermanentNotFoundAndHealthyRetryCanRecover()
    {
        using var profile = new Profile();
        bool recovered = false;
        using var client = new HttpClient(new Feed((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post ? Response(200, recovered ? CastBody(201, 101) :
                "{\"errors\":[{\"message\":\"controlled outage\"}],\"data\":{\"Media\":null}}") : Response(503, "unavailable"))));
        var cast = new VoiceCastService(profile.Config, client);
        string before = await profile.Snapshot();
        await Assert.ThrowsAnyAsync<Exception>(() => cast.FetchAndCacheCastAsync(profile.Media, VoiceLanguageMode.Sub, bypassCache: true));
        Assert.Equal(before, await profile.Snapshot());
        recovered = true;
        var result = await cast.FetchAndCacheCastAsync(profile.Media, VoiceLanguageMode.Sub, bypassCache: true);
        Assert.False(result.NotFound);
        Assert.Equal("Japanese actor", Assert.Single(result.Cast).VoiceActorName);
    }

    [Theory]
    [InlineData(202, 101)]
    [InlineData(201, 102)]
    public async Task ConflictingCastIdentityCannotReplaceTheRequestedWork(int returnedAniList, int returnedMal)
    {
        using var profile = new Profile();
        string before = await profile.Snapshot();
        using var client = new HttpClient(new Feed((_, _) => Task.FromResult(Response(200, CastBody(returnedAniList, returnedMal)))));
        var cast = new VoiceCastService(profile.Config, client);
        await Assert.ThrowsAnyAsync<Exception>(() => cast.FetchAndCacheCastAsync(profile.Media, VoiceLanguageMode.Sub, bypassCache: true));
        Assert.Equal(before, await profile.Snapshot());
    }

    [Theory]
    [InlineData(VoiceLanguageMode.Sub, "Japanese actor")]
    [InlineData(VoiceLanguageMode.Dub, "English actor")]
    public async Task ExplicitVoiceActorLanguagesStaySeparateEvenWhenAResponseContainsBoth(VoiceLanguageMode mode, string expected)
    {
        using var profile = new Profile();
        using var client = new HttpClient(new Feed((_, _) => Task.FromResult(Response(200, CastBody(201, 101)))));
        var cast = new VoiceCastService(profile.Config, client);
        var result = await cast.FetchAndCacheCastAsync(profile.Media, mode, bypassCache: true);
        Assert.Equal(expected, Assert.Single(result.Cast).VoiceActorName);
        Assert.Equal(mode.ToString(), result.Cast[0].LanguageMode);
        await using var db = new DatabaseContext();
        Assert.Equal("Adjacent actor", (await db.VoiceCastRecords.SingleAsync(row => row.MediaKey == "mal:202")).VoiceActorName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelayedCastCannotPublishUnderAChangedLanguageOrTarget(bool changeLanguage)
    {
        using var profile = new Profile();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Feed(async (_, _) =>
        {
            entered.TrySetResult();
            // Deliberately ignore cancellation to check the final publication guard too.
            await release.Task;
            return Response(200, CastBody(201, 101));
        }));
        using var view = await profile.View(client);
        view.LoadMedia(new MediaResult { Id = 201, IdMal = 101, OfficialTitle = "Target A", EnglishTitle = "Target A", RomajiTitle = "Target A" });
        view.ManualUrl = "";
        // The seeded cache is for Sub, so Dub must reach the controlled request.
        view.SelectedMode = "Dub";
        await using (var db = new DatabaseContext())
        {
            var oldDub = await db.VoiceCastRecords.SingleAsync(row => row.MediaKey == "mal:101" && row.LanguageMode == "Dub");
            db.VoiceCastRecords.Remove(oldDub);
            await db.SaveChangesAsync();
        }
        Task search = view.SearchCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (changeLanguage) view.SelectedMode = "Sub";
            else view.LoadMedia(new MediaResult { Id = 302, IdMal = 202, OfficialTitle = "Target B", EnglishTitle = "Target B", RomajiTitle = "Target B" });
            release.TrySetResult();
            await search.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(view.TargetCast);
            Assert.Empty(view.Matches);
            if (!changeLanguage) Assert.Equal("Target B", view.TargetTitle);
            Assert.DoesNotContain("Found", view.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally { release.TrySetResult(); await search.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    internal static string CastBody(int aniList, int mal) => JsonSerializer.Serialize(new
    {
        data = new { Media = new { id = aniList, idMal = mal, characters = new { edges = new[] { new
        {
            role = "MAIN", node = new { name = new { full = "Controlled character" } },
            voiceActors = new[] { new { name = new { full = "Japanese actor" }, language = "JAPANESE" },
                new { name = new { full = "English actor" }, language = "ENGLISH" } }
        } } } } }
    });
    private static HttpResponseMessage Response(int code, string body) => new((HttpStatusCode)code)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Feed(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handler(request, token);
    }
    private sealed class Profile : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UMOS-Voice-Cast-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previous = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
        public DomainHotSwapper Config { get; }
        public string ConfigPath => Path.Combine(_root, "Roaming", "UniversalMediaOS", "config.json");
        public VoiceCastMedia Media { get; } = new("mal:101", 101, 201, "Target A", "Target A", "", "", "");
        public Profile()
        {
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _root);
            Config = new DomainHotSwapper(Path.Combine(_root, "Roaming", "UniversalMediaOS", "config.json"));
            Assert.True(Config.SetSettings(new Dictionary<string, string> { ["DatabasePath"] = Path.Combine(_root, "voice.db"),
                ["AniListUrl"] = "https://controlled.invalid/graphql", ["VaDetectDefaultMode"] = "Sub" }));
            using var db = new DatabaseContext();
            db.Database.EnsureCreated();
            db.EnsureVaDetectSchema();
            db.VoiceCastRecords.AddRange(
                new VoiceCastRecord { MediaKey = "mal:101", MalId = 101, AniListId = 201, ShowTitle = "Target A", LanguageMode = "Sub", CharacterName = "Saved character", VoiceActorName = "Saved Japanese actor", Source = "Saved", FetchedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new VoiceCastRecord { MediaKey = "mal:101", MalId = 101, AniListId = 201, ShowTitle = "Target A", LanguageMode = "Dub", CharacterName = "Saved character", VoiceActorName = "Saved English actor", Source = "Saved", FetchedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new VoiceCastRecord { MediaKey = "mal:202", MalId = 202, AniListId = 302, ShowTitle = "Adjacent title", LanguageMode = "Sub", CharacterName = "Adjacent character", VoiceActorName = "Adjacent actor", Source = "Saved", FetchedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }
        public async Task<VaDetectViewModel> View(HttpClient client, TimeSpan? deadline = null)
        {
            var cast = new VoiceCastService(Config, client);
            var sync = new MalLibrarySyncService(Config, new MalPublicListFallbackService(client), client);
            var view = new VaDetectViewModel(Config, new FuzzyShieldSearch(Config), sync,
                new VoiceActorIndexService(cast, new FavoriteMediaService()), new BackgroundVoiceCastPrefetchService(cast))
                { CastSearchTimeout = deadline ?? TimeSpan.FromSeconds(45) };
            var summaryDeadline = DateTime.UtcNow.AddSeconds(3);
            while (view.LibrarySummary == "Library not synced in this session." && DateTime.UtcNow < summaryDeadline) await Task.Delay(10);
            Assert.NotEqual("Library not synced in this session.", view.LibrarySummary);
            return view;
        }
        public async Task PrepareMissingLibrary()
        {
            await using var db = new DatabaseContext();
            db.VoiceCastRecords.RemoveRange(await db.VoiceCastRecords.Where(row => row.LanguageMode == "Sub").ToArrayAsync());
            await db.SaveChangesAsync();
            db.MalLibraryEntries.AddRange(new MalLibraryEntry { MalId = 101, AniListId = 201, DefaultTitle = "Target A", UserScore = 9 },
                new MalLibraryEntry { MalId = 202, AniListId = 302, DefaultTitle = "Adjacent title", UserScore = 8 });
            db.VoiceCastRecords.Add(new VoiceCastRecord { MediaKey = "mal:101", MalId = 101, AniListId = 201, ShowTitle = "Target A", LanguageMode = "Sub", NotFound = true, Source = "Old incomplete lookup" });
            await db.SaveChangesAsync();
        }
        public async Task<string> Snapshot()
        {
            await using var db = new DatabaseContext();
            return JsonSerializer.Serialize(await db.VoiceCastRecords.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _previous);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
