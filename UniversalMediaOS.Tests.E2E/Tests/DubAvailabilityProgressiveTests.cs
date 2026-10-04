using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class DubAvailabilityProgressiveTests
{
    [Fact]
    public async Task FastSecondProviderPublishesBeforeSlowFirstAndSurvivesFailure()
    {
        var slow = Signal<HttpResponseMessage>();
        var useful = Signal<DubAvailabilityUpdate>();
        using var fixture = new Fixture([Badge("slow"), Badge("fast")], async (request, token) =>
            request.RequestUri!.Host.StartsWith("slow")
                ? await slow.Task.WaitAsync(token) : Html(Badges("Mock Anime", 5)));
        var task = fixture.Service.CheckAsync(Media(), progress: Observe(update =>
        {
            if (update.Result.DubEpisodes == 5) useful.TrySetResult(update);
        }));
        var partial = await useful.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(task.IsCompleted);
        Assert.False(partial.IsComplete);
        Assert.Equal(1, partial.PendingProviders);
        Assert.Equal(DubAvailabilityConfidence.Summary, partial.Result.Confidence);
        slow.SetResult(new(HttpStatusCode.ServiceUnavailable));
        var final = await task;
        Assert.Equal(5, final.DubEpisodes);
        Assert.Contains(final.ProviderOutcomes, p => p.Provider == "slow" && p.Outcome == DubProviderOutcome.Unavailable);
        Assert.Contains(final.ProviderOutcomes, p => p.Provider == "fast" && p.Outcome == DubProviderOutcome.Completed);
    }

    [Fact]
    public async Task SummarySurvivesVerificationBodyDeadlineAndCacheReportsIncompleteOutcome()
    {
        var summary = Signal<DubAvailabilityUpdate>();
        var stream = new WaitingStream();
        using var fixture = new Fixture([Ani(timeout: 1)], (request, _) => Task.FromResult(
            IsEpisodes(request) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) } : Html(Card())));
        var task = fixture.Service.CheckAsync(Media(), progress: Observe(update =>
        {
            if (update.Result.DubEpisodes == 8) summary.TrySetResult(update);
        }));
        Assert.False((await summary.Task.WaitAsync(TimeSpan.FromSeconds(3))).IsComplete);
        var final = await task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(8, final.DubEpisodes);
        Assert.False(final.Verified);
        Assert.Equal(0, final.HighestContiguousDubEpisode);
        Assert.Empty(final.DubbedEpisodeNumbers);
        Assert.Equal(DubProviderOutcome.TimedOut, Assert.Single(final.ProviderOutcomes).Outcome);
        Assert.True(stream.Canceled);
        int requests = fixture.Requests;
        DubAvailabilityUpdate? cached = null;
        await fixture.Service.CheckAsync(Media(), progress: Observe(update => cached = update));
        Assert.Equal(requests, fixture.Requests);
        Assert.NotNull(cached);
        Assert.True(cached.IsComplete);
        Assert.Equal(DubProviderOutcome.TimedOut, Assert.Single(cached.Providers).Outcome);
    }

    [Fact]
    public async Task KnownEpisodeIdentityConflictWithdrawsProvisionalSummary()
    {
        var episode = Signal<HttpResponseMessage>();
        var summary = Signal<DubAvailabilityUpdate>();
        using var fixture = AniFixture(episode);
        var task = fixture.Service.CheckAsync(Media(), progress: Observe(update =>
        {
            if (update.Result.DubEpisodes == 8) summary.TrySetResult(update);
        }));
        await summary.Task.WaitAsync(TimeSpan.FromSeconds(3));
        episode.SetResult(Episodes(mal: 999));
        var final = await task;
        Assert.False(final.Checked);
        Assert.Equal(0, final.DubEpisodes);
        Assert.Equal(DubProviderOutcome.IdentityRejected, Assert.Single(final.ProviderOutcomes).Outcome);
    }

    [Fact]
    public async Task LateBadgeCannotDowngradeVerifiedEpisodeFlags()
    {
        var badge = Signal<HttpResponseMessage>();
        var verified = Signal<DubAvailabilityUpdate>();
        using var fixture = new Fixture([Ani(), Badge("late")], async (request, token) =>
            request.RequestUri!.Host.StartsWith("late") ? await badge.Task.WaitAsync(token)
                : IsEpisodes(request) ? Episodes() : Html(Card()));
        var task = fixture.Service.CheckAsync(Media(), progress: Observe(update =>
        {
            if (update.Result.Verified) verified.TrySetResult(update);
        }));
        Assert.False((await verified.Task.WaitAsync(TimeSpan.FromSeconds(3))).IsComplete);
        badge.SetResult(Html(Badges("Mock Anime", 9)));
        var final = await task;
        Assert.True(final.Verified);
        Assert.Equal(2, final.DubEpisodes);
        Assert.Equal(new decimal[] { 1, 2 }, final.DubbedEpisodeNumbers);
    }

    [Fact]
    public async Task InterimVerifiedZeroDoesNotDeclareAbsenceWhileOtherProviderIsPending()
    {
        var badge = Signal<HttpResponseMessage>();
        var completedZero = Signal<DubAvailabilityUpdate>();
        using var fixture = new Fixture([Ani(), Badge("late")], async (request, token) =>
            request.RequestUri!.Host.StartsWith("late") ? await badge.Task.WaitAsync(token)
                : IsEpisodes(request) ? Episodes(dub: 0) : Html(Card()));
        var task = fixture.Service.CheckAsync(Media(), progress: Observe(update =>
        {
            if (update.Providers[0].Outcome == DubProviderOutcome.Completed) completedZero.TrySetResult(update);
        }));
        var partial = await completedZero.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(partial.Result.Verified);
        Assert.False(partial.IsComplete);
        badge.SetResult(Html(Badges("Mock Anime", 3)));
        Assert.Equal(3, (await task).DubEpisodes);
    }

    [Fact]
    public async Task LateSharedSubscriberReplaysSummaryAndCancelingOneKeepsOtherOwner()
    {
        var episode = Signal<HttpResponseMessage>();
        var firstSummary = Signal<DubAvailabilityUpdate>();
        using var fixture = AniFixture(episode);
        using var caller = new CancellationTokenSource();
        var first = fixture.Service.CheckAsync(Media(), caller.Token, progress: Observe(update =>
        {
            if (update.Result.DubEpisodes == 8) firstSummary.TrySetResult(update);
        }));
        var partial = await firstSummary.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var secondSummary = Signal<DubAvailabilityUpdate>();
        var second = fixture.Service.CheckAsync(Media(), progress: Observe(update => secondSummary.TrySetResult(update)));
        Assert.Equal(partial.Sequence, (await secondSummary.Task).Sequence);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);
        episode.SetResult(Episodes());
        Assert.True((await second).Verified);
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task CancelingLastOwnerReleasesBodyAndNextLookupDoesNotInheritOfflineStatus()
    {
        var stream = new WaitingStream();
        int episodeRequests = 0;
        using var fixture = new Fixture([Ani()], (request, _) => Task.FromResult(!IsEpisodes(request)
            ? Html(Card()) : Interlocked.Increment(ref episodeRequests) == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) } : Episodes()));
        using var caller = new CancellationTokenSource();
        var first = fixture.Service.CheckAsync(Media(), caller.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Eventually(() => stream.Canceled);
        var final = await fixture.Service.CheckAsync(Media()).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(final.Verified);
        Assert.Equal(2, episodeRequests);
    }

    [Fact]
    public async Task ProgressConsumerFailureDoesNotFailFreshOrCachedLookupAndCanceledCacheCallerThrows()
    {
        using var fixture = new Fixture([Badge("mock")], (_, _) => Task.FromResult(Html(Badges("Mock Anime", 5))));
        var badConsumer = Observe(_ => throw new InvalidOperationException("consumer failure"));
        Assert.Equal(5, (await fixture.Service.CheckAsync(Media(), progress: badConsumer)).DubEpisodes);
        Assert.Equal(5, (await fixture.Service.CheckAsync(Media(), progress: badConsumer)).DubEpisodes);
        Assert.Equal(1, fixture.Requests);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.CheckAsync(Media(), caller.Token));
    }

    [Fact]
    public async Task ConfiguredProvidersHaveBoundedConcurrencyAndStableTieOrdering()
    {
        var release = Signal();
        var twoStarted = Signal();
        int active = 0, maximum = 0;
        using var fixture = new Fixture(Enumerable.Range(0, 6).Select(i => Badge("p" + i)).ToArray(), async (_, token) =>
        {
            int count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            if (count == 2) twoStarted.TrySetResult();
            try { await release.Task.WaitAsync(token); return Html(Badges("Mock Anime", 5)); }
            finally { Interlocked.Decrement(ref active); }
        });
        var task = fixture.Service.CheckAsync(Media());
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, fixture.Requests);
        release.SetResult();
        var final = await task;
        Assert.Equal(2, maximum);
        Assert.Equal(6, fixture.Requests);
        Assert.Equal("p0", final.Source);
    }

    [Fact]
    public async Task DetailsCancelRetainsFastSummaryAndRejectsLateVerification()
    {
        var episode = Signal<HttpResponseMessage>();
        using var fixture = AniFixture(episode);
        using var vm = fixture.Details();
        vm.Media = Media();
        await Eventually(() => vm.Media.DubAvailabilityState == MediaDubAvailabilityState.Summary && vm.DubCheckStatus.Contains("Badge summary"));
        Assert.True(vm.IsDubChecking);
        Assert.Equal(MediaDubAvailabilityState.Summary, vm.Media.DubAvailabilityState);
        Assert.Contains("Badge summary", vm.DubCheckStatus);
        vm.CancelDubCheckCommand.Execute(null);
        await Eventually(() => !vm.IsDubChecking);
        episode.TrySetResult(Episodes());
        Assert.Equal(8, vm.Media.AvailableDubEpisodes);
        Assert.False(vm.Media.IsDubAvailabilityVerified);
        Assert.Contains("canceled", vm.DubCheckStatus);
    }

    [Fact]
    public async Task DetailsKnownConflictClearsSummary()
    {
        var episode = Signal<HttpResponseMessage>();
        using var fixture = AniFixture(episode);
        using var vm = fixture.Details();
        vm.Media = Media();
        await Eventually(() => vm.Media.AvailableDubEpisodes == 8);
        episode.SetResult(Episodes(mal: 999));
        await Eventually(() => !vm.IsDubChecking);
        Assert.Equal(0, vm.Media.AvailableDubEpisodes);
        Assert.False(vm.Media.DubAvailabilityChecked);
        Assert.Equal(MediaDubAvailabilityState.Unknown, vm.Media.DubAvailabilityState);
        Assert.Contains("identity conflict", vm.DubCheckStatus);
    }

    [Fact]
    public async Task DetailsOldLateFaultCannotReplaceNewMediaOrPoisonItsProvider()
    {
        var old = Signal<HttpResponseMessage>();
        var oldStarted = Signal();
        using var fixture = new Fixture([Badge("mock")], async (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("Old"))
            {
                oldStarted.TrySetResult();
                return await old.Task; // Deliberately ignores cancellation.
            }
            return Html(Badges("New Anime", 4));
        });
        using var vm = fixture.Details();
        var previous = Media("Old Anime", id: 1);
        vm.Media = previous;
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var current = Media("New Anime", id: 2);
        vm.Media = current;
        old.SetException(new HttpRequestException("late old failure"));
        await Eventually(() => !vm.IsDubChecking);
        Assert.Same(current, vm.Media);
        Assert.Equal(4, current.AvailableDubEpisodes);
        Assert.False(previous.DubAvailabilityChecked);
        Assert.DoesNotContain("canceled", vm.DubCheckStatus);
    }

    [Fact]
    public async Task AutomaticDubFilterPublishesFastAndUnknownTitlesBeforeSlowLookupAndCanCancel()
    {
        var slowStarted = Signal();
        using var fixture = new Fixture([Badge("mock")], async (request, token) =>
        {
            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            if (query.Contains("Slow"))
            {
                slowStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            string title = query.Contains("Zero") ? "Zero Anime" : "Fast Anime";
            return Html(Badges(title, title.StartsWith("Zero") ? 0 : 5));
        });
        var catalog = new Catalog();
        using var vm = fixture.Search(catalog);
        vm.SearchQuery = "mock";
        vm.SelectedAudio = "Dub"; // Runs the automatic filter refresh, outside SearchCommand.
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Eventually(() => vm.SearchResults.FirstOrDefault()?.AvailableDubEpisodes == 5 && vm.SearchResults.Count == 2);
        Assert.Equal(new[] { "Fast Anime", "Slow Anime" }, vm.SearchResults.Select(r => r.OfficialTitle));
        Assert.True(vm.IsSearching);
        Assert.True(vm.CancelActiveSearchCommand.CanExecute(null));
        Assert.False(vm.SearchCommand.IsRunning);
        Assert.Contains("unknown titles retained", vm.ResultsDescription);
        vm.CancelActiveSearchCommand.Execute(null);
        await Eventually(() => !vm.IsSearching);
        Assert.Contains("canceled", vm.ResultsDescription);
        Assert.Equal(2, vm.SearchResults.Count);
        Assert.False(vm.SearchResults[1].DubAvailabilityChecked);
        await Task.Delay(350);
        Assert.Equal(1, catalog.Calls);
    }

    [Fact]
    public async Task CanceledCatalogIgnoringTokenCannotOverwriteNextSearch()
    {
        using var fixture = new Fixture([], (_, _) => throw new InvalidOperationException());
        var old = Signal<MediaSearchPage>();
        var catalog = new Catalog { Override = query => query == "old" ? old.Task
            : Task.FromResult(new MediaSearchPage([Media("New Anime")], false)) };
        using var vm = fixture.Search(catalog);
        vm.SearchQuery = "old";
        var first = vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.IsSearching);
        vm.CancelActiveSearchCommand.Execute(null);
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        vm.SearchQuery = "new";
        await vm.SearchCommand.ExecuteAsync(null);
        old.SetResult(new([Media("Old Anime")], false));
        Assert.Equal("New Anime", Assert.Single(vm.SearchResults).OfficialTitle);
        Assert.DoesNotContain("old", vm.ResultsDescription);
    }

    [Fact]
    public async Task PagingCancelRetainsEarlierResultsAndRetryReplacesExactPartialIdentities()
    {
        var slowStarted = Signal();
        var slowRelease = Signal();
        int slowRequests = 0;
        using var fixture = new Fixture([Badge("mock")], async (request, token) =>
        {
            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            if (query.Contains("Slow"))
            {
                Interlocked.Increment(ref slowRequests);
                slowStarted.TrySetResult();
                await slowRelease.Task.WaitAsync(token);
            }
            string title = query.Contains("Slow") ? "Slow Anime" : query.Contains("Fast") ? "Fast Anime" : "Saved Anime";
            return Html(Badges(title, 5));
        });
        var catalog = new Catalog
        {
            PageOverride = page => Task.FromResult(page == 1
                ? new MediaSearchPage([Media("Saved Anime", 1)], true)
                : new MediaSearchPage([Media("Fast Anime", 2), Media("Slow Anime", 3)], false))
        };
        using var vm = fixture.Search(catalog);
        vm.SearchQuery = "mock";
        vm.SelectedAudio = "Dub";
        await Eventually(() => catalog.Calls == 1 && !vm.IsSearching);
        var paging = vm.LoadMoreAnimeCommand.ExecuteAsync(null);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Eventually(() => vm.SearchResults.Count == 3 && vm.SearchResults[1].DubAvailabilityChecked);
        Assert.True(vm.CanCancelSearch);
        Assert.False(vm.IsSearching);
        vm.CancelActiveSearchCommand.Execute(null);
        await paging.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 1, 2, 3 }, vm.SearchResults.Select(media => media.Id));
        Assert.Contains("Page lookup canceled", vm.ResultsDescription);
        slowRelease.SetResult();
        await vm.LoadMoreAnimeCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 1, 2, 3 }, vm.SearchResults.Select(media => media.Id));
        Assert.All(vm.SearchResults, media => Assert.True(media.DubAvailabilityChecked));
        Assert.Equal(2, slowRequests);
        Assert.Contains("Dub checks complete", vm.ResultsDescription);
    }

    private static Fixture AniFixture(TaskCompletionSource<HttpResponseMessage> episode) => new([Ani()], async (request, token) =>
        IsEpisodes(request) ? await episode.Task.WaitAsync(token) : Html(Card()));
    private static bool IsEpisodes(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.Contains("/episode/list/");
    private static MediaResult Media(string title = "Mock Anime", int id = 123) => new()
        { Id = id, IdMal = 123, OfficialTitle = title, AvailableSubEpisodes = 12, TotalEpisodes = 12 };
    private static DubAvailabilityProviderConfig Badge(string name) => new()
        { Name = name, SuggestUrlTemplate = $"https://{name}.example/search?q={{query}}", ParserType = "Badge" };
    private static DubAvailabilityProviderConfig Ani(int timeout = 8) => new()
        { Name = "AniKoto", SuggestUrlTemplate = "https://ani.example", AdapterType = "AniKoto", TimeoutSeconds = timeout };
    private static string Card() => """
        <div class="item"><div class="ani poster tip" data-tip="77"></div><a class="name d-title">Mock Anime</a>
        <span class="ep-status sub"><span>12</span></span><span class="ep-status dub"><span>8</span></span></div>
        """;
    private static string Badges(string title, int dub) => $"<div><a title=\"{title}\">{title}</a><span class=\"tick-sub\">12</span><span class=\"tick-dub\">{dub}</span></div>";
    private static HttpResponseMessage Html(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    private static HttpResponseMessage Episodes(int mal = 123, int dub = 1) => Html(JsonSerializer.Serialize(new
        { status = 200, result = $"<a data-num=\"1\" data-mal=\"{mal}\" data-sub=\"1\" data-dub=\"{dub}\"></a><a data-num=\"2\" data-mal=\"{mal}\" data-sub=\"1\" data-dub=\"{dub}\"></a>" }));
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IProgress<DubAvailabilityUpdate> Observe(Action<DubAvailabilityUpdate> action) => new Observer(action);
    private sealed class Observer(Action<DubAvailabilityUpdate> action) : IProgress<DubAvailabilityUpdate>
        { public void Report(DubAvailabilityUpdate value) => action(value); }
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "umos-dub-progress-" + Guid.NewGuid().ToString("N"));
        public DomainHotSwapper Config { get; }
        public DubAvailabilityService Service { get; }
        public int Requests => _handler.Requests;
        private readonly Handler _handler;
        private readonly HttpClient _http;
        public Fixture(DubAvailabilityProviderConfig[] providers, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            Directory.CreateDirectory(Root);
            string path = Path.Combine(Root, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
                { [DubAvailabilityService.ProviderConfigKey] = JsonSerializer.Serialize(providers) }));
            Config = new(path);
            _handler = new(handler);
            _http = new(_handler);
            Service = new(Config, _http);
        }
        public AnimeDetailsViewModel Details() => new(Config, null!, null!, null!, null!, Service, new MalOAuthService(Config));
        public SearchViewModel Search(FuzzyShieldSearch catalog) => new(catalog, Service, new FavoriteMediaService(Path.Combine(Root, "favorites.json")));
        public void Dispose()
        {
            _http.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Requests); return handler(request, token); }
    }
    private sealed class Catalog : FuzzyShieldSearch
    {
        public int Calls;
        public Func<string, Task<MediaSearchPage>>? Override;
        public Func<int, Task<MediaSearchPage>>? PageOverride;
        public override Task<MediaSearchPage> SearchAnimePageAsync(string query, int page, int perPage,
            AnimeSearchFilters? filters = null, CancellationToken token = default)
        {
            Interlocked.Increment(ref Calls);
            return PageOverride?.Invoke(page) ?? Override?.Invoke(query) ?? Task.FromResult(new MediaSearchPage(
                [Media("Fast Anime", 1), Media("Zero Anime", 2), Media("Slow Anime", 3)], false));
        }
    }
    private sealed class WaitingStream : Stream
    {
        public TaskCompletionSource Started { get; } = Signal();
        public bool Canceled;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return 0; }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
