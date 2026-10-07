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

public sealed class MalSyncRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuthNetworkOrJsonFailureIsRecoverableAndKeepsTheSavedLibrary(bool networkFailure)
    {
        using var profile = new Profile();
        using var client = new HttpClient(new Handler((_, _) => networkFailure
            ? throw new HttpRequestException("Controlled network failure")
            : Task.FromResult(Response("not json"))));
        using var view = profile.View(client);
        await Ready(view);
        string original = await profile.LibrarySnapshot();
        string config = File.ReadAllText(profile.ConfigPath);
        var error = await Record.ExceptionAsync(() => view.SyncLibraryCommand.ExecuteAsync(null));
        Assert.Null(error);
        Assert.Contains("failed", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", view.WarningText, StringComparison.OrdinalIgnoreCase);
        Assert.False(view.IsBusy);
        Assert.True(view.SyncLibraryCommand.CanExecute(null));
        Assert.Equal(original, await profile.LibrarySnapshot());
        Assert.Equal(config, File.ReadAllText(profile.ConfigPath));
    }

    [Fact]
    public async Task OAuthDeniedConnectionSuggestsReconnectAndKeepsLibraryAndCredentials()
    {
        using var profile = new Profile();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response("access denied", HttpStatusCode.Unauthorized))));
        using var view = profile.View(client);
        await Ready(view);
        string original = await profile.LibrarySnapshot();
        string config = File.ReadAllText(profile.ConfigPath);
        await view.SyncLibraryCommand.ExecuteAsync(null);
        Assert.Equal("MAL sync failed.", view.StatusText);
        Assert.Contains("Reconnect MyAnimeList in Settings", view.WarningText);
        Assert.Equal(original, await profile.LibrarySnapshot());
        Assert.Equal(config, File.ReadAllText(profile.ConfigPath));
        Assert.True(view.SyncLibraryCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task OAuthHttpFailureRetainsSavedRecordsAndAllowsAnExplicitRetry(HttpStatusCode status)
    {
        using var profile = new Profile();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response("not available", status))));
        using var view = profile.View(client);
        await Ready(view);
        string original = await profile.LibrarySnapshot();
        await view.SyncLibraryCommand.ExecuteAsync(null);
        Assert.Equal("MAL sync failed.", view.StatusText);
        Assert.Contains("retry", view.WarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await profile.LibrarySnapshot());
        Assert.True(view.SyncLibraryCommand.CanExecute(null));
    }

    [Fact]
    public async Task OAuthRetryAfterInvalidResponseUpdatesOnlyTheReturnedKnownIdentity()
    {
        using var profile = new Profile();
        bool fail = true;
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(Response(fail ? "not json" :
            request.RequestUri!.Query.Contains("status=watching", StringComparison.Ordinal) ? OAuthEntry : "{\"data\":[]}"))));
        using var view = profile.View(client);
        await Ready(view);
        string original = await profile.LibrarySnapshot();
        await view.SyncLibraryCommand.ExecuteAsync(null);
        Assert.Equal(original, await profile.LibrarySnapshot());
        fail = false;
        await view.SyncLibraryCommand.ExecuteAsync(null);
        Assert.Equal("OAuth sync complete: 1 anime imported.", view.StatusText);
        Assert.Empty(view.WarningText);
        var cached = await profile.Sync(client).GetLibraryAsync();
        Assert.Equal(2, cached.Count);
        Assert.Equal(3, cached.Single(item => item.MalId == 101).WatchedEpisodes);
        Assert.Equal(201, cached.Single(item => item.MalId == 101).AniListId);
        Assert.Equal(5, cached.Single(item => item.MalId == 202).WatchedEpisodes);
        Assert.Equal(302, cached.Single(item => item.MalId == 202).AniListId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SyncCanBeCancelledOrReachDeadlineWithoutPublishingOrStartingASecondOperation(bool publicList, bool deadline)
    {
        using var profile = new Profile();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            requested.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Response("[]");
        }));
        using var view = profile.View(client, deadline ? TimeSpan.FromSeconds(1) : null);
        await Ready(view);
        view.PublicFallbackUsername = "controlled-fixture";
        string original = await profile.LibrarySnapshot();
        string config = File.ReadAllText(profile.ConfigPath);
        var pending = publicList ? view.SyncPublicFallbackCommand.ExecuteAsync(null) : view.SyncLibraryCommand.ExecuteAsync(null);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(view.IsBusy);
        Assert.True(view.IsSyncing);
        Assert.True(view.CancelMalSyncCommand.CanExecute(null));
        Assert.False(view.SyncLibraryCommand.CanExecute(null));
        Assert.False(view.SyncPublicFallbackCommand.CanExecute(null));
        Assert.False(view.SearchCommand.CanExecute(null));
        if (!deadline) view.CancelMalSyncCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(deadline ? "timed out" : "cancelled", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", view.WarningText, StringComparison.OrdinalIgnoreCase);
        Assert.False(view.IsBusy);
        Assert.False(view.IsSyncing);
        Assert.False(view.CancelMalSyncCommand.CanExecute(null));
        Assert.True(view.SyncLibraryCommand.CanExecute(null));
        Assert.True(view.SyncPublicFallbackCommand.CanExecute(null));
        Assert.True(view.SearchCommand.CanExecute(null));
        Assert.Equal(original, await profile.LibrarySnapshot());
        Assert.Equal(config, File.ReadAllText(profile.ConfigPath));
    }

    [Fact]
    public async Task ValidEmptyOAuthListHasAnExplicitSuccessOutcomeAndRetainsCachedEntries()
    {
        using var profile = new Profile();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response("{\"data\":[]}"))));
        using var view = profile.View(client);
        await Ready(view);
        string original = await profile.LibrarySnapshot();
        await view.SyncLibraryCommand.ExecuteAsync(null);
        Assert.Contains("complete", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0 anime imported", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(view.WarningText);
        Assert.Equal(original, await profile.LibrarySnapshot());
    }

    [Theory]
    [InlineData("{\"error\":\"Unavailable\"}")]
    [InlineData("{\"data\":{}}")]
    public async Task InvalidOAuthShapeAfterAnEarlierStatusCannotPublishPartialLibrary(string failedPage)
    {
        using var profile = new Profile();
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(Response(
            request.RequestUri!.Query.Contains("status=watching", StringComparison.Ordinal)
                ? OAuthEntry : failedPage))));
        var sync = profile.Sync(client);
        string original = await profile.LibrarySnapshot();
        await Assert.ThrowsAsync<InvalidDataException>(() => sync.SyncOAuthAsync());
        Assert.Equal(original, await profile.LibrarySnapshot());
    }

    [Fact]
    public async Task PublicListHttpFailureAfterAnEarlierStatusCannotPublishPartialLibraryAndCanRetry()
    {
        using var profile = new Profile();
        bool fail = true;
        var handler = new Handler((request, _) => Task.FromResult(
            fail && request.RequestUri!.Query.Contains("status=2", StringComparison.Ordinal)
                ? Response("temporarily unavailable", HttpStatusCode.ServiceUnavailable)
                : Response(request.RequestUri!.Query.Contains("status=1&offset=0", StringComparison.Ordinal)
                    ? PublicEntry : "[]")));
        using var client = new HttpClient(handler);
        using var view = profile.View(client);
        await Ready(view);
        string original = await profile.LibrarySnapshot();
        string originalConfig = File.ReadAllText(profile.ConfigPath);
        view.PublicFallbackUsername = "controlled-fixture";
        var error = await Record.ExceptionAsync(() => view.SyncPublicFallbackCommand.ExecuteAsync(null));
        Assert.Null(error);
        Assert.Equal(original, await profile.LibrarySnapshot());
        Assert.Contains("failed", view.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", view.WarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await profile.LibrarySnapshot());
        Assert.Equal(originalConfig, File.ReadAllText(profile.ConfigPath));
        fail = false;
        await view.SyncPublicFallbackCommand.ExecuteAsync(null);
        Assert.Contains("sync complete: 1 anime imported", view.StatusText, StringComparison.OrdinalIgnoreCase);
        var cached = await profile.Sync(client).GetLibraryAsync();
        Assert.Equal(3, cached.Single(item => item.MalId == 101).WatchedEpisodes);
        Assert.Equal(201, cached.Single(item => item.MalId == 101).AniListId);
        Assert.Equal(5, cached.Single(item => item.MalId == 202).WatchedEpisodes);
        Assert.Equal("controlled-fixture", profile.Config.GetSetting("VaDetectPublicMalFallbackUsername"));
    }

    internal const string OAuthEntry = """
        {"data":[{"node":{"id":101,"title":"Saved title","num_episodes":12},"list_status":{"status":"watching","num_episodes_watched":3,"score":8}}]}
        """;
    private const string PublicEntry = """
        [{"anime_id":101,"anime_title":"Saved title","anime_num_episodes":12,"num_watched_episodes":3,"score":8}]
        """;
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static async Task Ready(VaDetectViewModel view)
    {
        for (int attempt = 0; attempt < 200 && view.LibrarySummary != "2 MAL library entries cached."; attempt++) await Task.Delay(10);
        Assert.Equal("2 MAL library entries cached.", view.LibrarySummary);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
    private sealed class Profile : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UMOS-Mal-Recovery-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousRoot = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
        public string ConfigPath => Path.Combine(_root, "Roaming", "UniversalMediaOS", "config.json");
        public DomainHotSwapper Config { get; }
        public Profile()
        {
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _root);
            Config = new DomainHotSwapper(ConfigPath);
            Assert.True(Config.SetSettings(new Dictionary<string, string> { ["MalOAuthToken"] = "controlled-fixture-token",
                ["MalApiUrl"] = "https://controlled.invalid", ["DatabasePath"] = Path.Combine(_root, "media_os.db") }));
            using var db = new DatabaseContext();
            db.Database.EnsureCreated();
            db.EnsureVaDetectSchema();
            db.MalLibraryEntries.AddRange(
                new MalLibraryEntry { MalId = 101, AniListId = 201, DefaultTitle = "Saved title", ListStatus = "Watching", WatchedEpisodes = 2, TotalEpisodes = 12 },
                new MalLibraryEntry { MalId = 202, AniListId = 302, DefaultTitle = "Adjacent title", ListStatus = "Completed", WatchedEpisodes = 5, TotalEpisodes = 5 });
            db.SaveChanges();
        }
        public MalLibrarySyncService Sync(HttpClient client) => new(Config, new MalPublicListFallbackService(client), client);
        public VaDetectViewModel View(HttpClient client, TimeSpan? deadline = null)
        {
            var cast = new VoiceCastService(Config, client);
            return new(Config, new FuzzyShieldSearch(Config), Sync(client),
                new VoiceActorIndexService(cast, new FavoriteMediaService()), new BackgroundVoiceCastPrefetchService(cast))
                { MalSyncTimeout = deadline ?? TimeSpan.FromMinutes(2) };
        }
        public async Task<string> LibrarySnapshot()
        {
            await using var db = new DatabaseContext();
            return JsonSerializer.Serialize(await db.MalLibraryEntries.AsNoTracking().OrderBy(item => item.MalId).ToArrayAsync());
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _previousRoot);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
