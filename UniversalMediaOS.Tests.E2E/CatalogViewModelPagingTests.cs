using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.WPF;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class CatalogViewModelPagingTests
{
    [Fact]
    public async Task PagesAppendInOneNotificationAndKeepDifferentProviderIds()
    {
        var service = new FakeCatalog((request, _) => Task.FromResult(request.ContinuationToken == null
            ? Page([Item("1"), Item("2")], "next") : Page([Item("2"), Item("3")])));
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await vm.LoadPopularCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Items.Count);
        Assert.True(vm.HasMore);
        int updates = 0;
        vm.Items.CollectionChanged += (_, _) => updates++;
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Items.Count);
        Assert.Equal(1, updates);
        Assert.False(vm.HasMore);
        Assert.False(vm.LoadMoreCommand.CanExecute(null));
        Assert.Equal("next", service.Requests[1].ContinuationToken);
    }

    [Fact]
    public async Task LaterFailurePreservesCardsAndRetriesTheSameCursor()
    {
        int laterCalls = 0;
        var service = new FakeCatalog((request, _) => Task.FromResult(request.ContinuationToken == null ? Page([Item("1")], "next")
            : ++laterCalls == 1 ? new([], null, [new("test", ProviderOutcomeStatus.Timeout, TimeSpan.Zero)]) : Page([Item("2")])));
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await vm.LoadPopularCommand.ExecuteAsync(null);
        var first = vm.Items[0];
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Same(first, Assert.Single(vm.Items));
        Assert.True(vm.HasMore);
        Assert.Contains("timed out", vm.StatusText);
        Assert.False(vm.HasNoResults);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(service.Requests[1].ContinuationToken, service.Requests[2].ContinuationToken);
    }

    [Fact]
    public async Task LateUncooperativeProviderCannotOverwriteNewSearch()
    {
        var slow = new TaskCompletionSource<AudiovisualCatalogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeCatalog((request, _) => request.Query == "old" ? slow.Task : Task.FromResult(Page([Item("new")])));
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        vm.SearchQuery = "old";
        Task oldRequest = vm.SearchCommand.ExecuteAsync(null);
        vm.SearchQuery = "new";
        await vm.SearchCommand.ExecuteAsync(null);
        slow.SetResult(Page([Item("old")], "stale"));
        await oldRequest;
        Assert.Equal("new", Assert.Single(vm.Items).Identity.PrimaryId!.Value);
        Assert.False(vm.HasMore);
        Assert.False(vm.IsBusy);
        Assert.Equal("old", service.Requests[0].Query);
    }

    [Fact]
    public async Task EmptyFilteredPageKeepsLoadMoreButFailureIsNotNoResults()
    {
        var service = new FakeCatalog((request, _) => Task.FromResult(request.ContinuationToken == null ? Page([], "next")
            : new([], null, [new("test", ProviderOutcomeStatus.RateLimited, TimeSpan.Zero)])));
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await vm.LoadPopularCommand.ExecuteAsync(null);
        Assert.True(vm.HasMore);
        Assert.False(vm.HasNoResults);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.False(vm.HasNoResults);
        Assert.Contains("busy", vm.StatusText);
        Assert.True(vm.HasMore);
    }

    [Fact]
    public async Task DisposalRejectsLateResultsAndLibrarySwitchClearsContinuation()
    {
        var pending = new TaskCompletionSource<AudiovisualCatalogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeCatalog((request, _) => request.Query == "pending" ? pending.Task : Task.FromResult(Page([Item("1")], "next")));
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await vm.LoadPopularCommand.ExecuteAsync(null);
        await vm.ShowLibraryCommand.ExecuteAsync(null);
        Assert.False(vm.HasMore);
        Assert.Empty(vm.Items);
        vm.SearchQuery = "pending";
        Task task = vm.SearchCommand.ExecuteAsync(null);
        vm.Dispose();
        pending.SetResult(Page([Item("late")], "next"));
        await task;
        Assert.Empty(vm.Items);
        Assert.False(vm.HasMore);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OpeningDifferentKeylessIdentityDoesNotReuseSameTitleAndYear()
    {
        var service = new FakeCatalog((_, _) => Task.FromResult(Page([Item("1")])));
        using var fixture = new Fixture(service);
        await fixture.ViewModel.LoadPopularCommand.ExecuteAsync(null);
        await fixture.ViewModel.OpenItemAsync(Item("2"));
        Assert.Equal("2", fixture.ViewModel.SelectedItem!.Identity.PrimaryId!.Value);
        Assert.Equal(2, fixture.ViewModel.Items.Count);
    }

    [Theory]
    [InlineData("episode")]
    [InlineData("season")]
    [InlineData("language")]
    [InlineData("arabic")]
    [InlineData("title")]
    public async Task SelectionChangesInvalidateSourceButtons(string change)
    {
        var service = new FakeCatalog((_, _) => Task.FromResult(Page([])))
        { Resolve = (_, _, _, _) => Task.FromResult<IReadOnlyList<AudiovisualSource>>([Source("old")]) };
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await OpenWithSourcesAsync(vm, Item("1"));
        var stale = Assert.Single(vm.Sources);
        switch (change)
        {
            case "episode": vm.EpisodeNumberText = "2"; break;
            case "season": vm.SeasonNumberText = "2"; break;
            case "language": vm.PreferredLanguage = "Arabic"; break;
            case "arabic": vm.ArabicOnly = true; break;
            default: vm.SelectedItem = new(Item("2")); break;
        }
        Assert.Empty(vm.Sources);
        await vm.PlaySourceCommand.ExecuteAsync(stale);
        await vm.OpenSourceCommand.ExecuteAsync(stale);
        await vm.DownloadSourceCommand.ExecuteAsync(stale);
        await vm.WatchViaDownloadCommand.ExecuteAsync(stale);
        Assert.Equal(0, fixture.Dialog.ErrorCount);
        Assert.Equal(0, fixture.Launcher.OpenCount);
        Assert.Empty(vm.DownloadStatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteSourceResponseCannotReplaceNewTitleResults(bool fail)
    {
        var oldResponse = new TaskCompletionSource<IReadOnlyList<AudiovisualSource>>();
        var started = new TaskCompletionSource();
        var service = new FakeCatalog((_, _) => Task.FromResult(Page([])))
        { Resolve = (identity, _, _, _) =>
            { if (identity.PrimaryId!.Value == "1") { started.SetResult(); return oldResponse.Task; }
              return Task.FromResult<IReadOnlyList<AudiovisualSource>>([Source("new")]); } };
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        Task old = OpenWithSourcesAsync(vm, Item("1"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.FindSourcesCommand.CanExecute(null)); // A slow old provider cannot block a new selection.
        await OpenWithSourcesAsync(vm, Item("2"));
        if (fail) oldResponse.SetException(new InvalidOperationException("Late provider failure"));
        else oldResponse.SetResult([Source("old")]);
        await old;
        Assert.Equal("new", Assert.Single(vm.Sources).ProviderName);
        Assert.Contains("1 unverified candidate", vm.SourceStatusText);
        Assert.False(vm.IsResolvingSources);
    }

    [Fact]
    public async Task ClosedDetailsRejectLateSourcesAndAllowFreshLookup()
    {
        var response = new TaskCompletionSource<IReadOnlyList<AudiovisualSource>>();
        var started = new TaskCompletionSource();
        var service = new FakeCatalog((_, _) => Task.FromResult(Page([])))
        { Resolve = (_, _, _, _) => { started.TrySetResult(); return response.Task; } };
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        Task opening = OpenWithSourcesAsync(vm, Item("1"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CloseDetailsCommand.Execute(null);
        response.SetResult([Source("old")]);
        await opening;
        Assert.Empty(vm.Sources);
        Assert.Null(vm.SelectedItem);
        Assert.False(vm.IsResolvingSources);
        service.Resolve = (_, _, _, _) => Task.FromResult<IReadOnlyList<AudiovisualSource>>([Source("fresh")]);
        await OpenWithSourcesAsync(vm, Item("1"));
        Assert.Equal("fresh", Assert.Single(vm.Sources).ProviderName);
    }

    [Fact]
    public async Task CurrentSourceReachesSafetyChecksButReplacedAndDisposedSourcesDoNot()
    {
        var service = new FakeCatalog((_, _) => Task.FromResult(Page([])))
        { Resolve = (_, _, _, _) => Task.FromResult<IReadOnlyList<AudiovisualSource>>([Source("current")]) };
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await OpenWithSourcesAsync(vm, Item("1"));
        var original = Assert.Single(vm.Sources);
        await vm.PlaySourceCommand.ExecuteAsync(original);
        Assert.Equal(1, fixture.Dialog.ErrorCount); // The private fixture URL reaches, and fails, network safety.
        await vm.FindSourcesCommand.ExecuteAsync(null);
        await vm.PlaySourceCommand.ExecuteAsync(original);
        Assert.Equal(1, fixture.Dialog.ErrorCount);
        var current = Assert.Single(vm.Sources);
        vm.Dispose();
        await vm.PlaySourceCommand.ExecuteAsync(current);
        await vm.FindSourcesCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Dialog.ErrorCount);
        Assert.Empty(vm.Sources);
    }

    [Fact]
    public async Task WebsiteFallbackIsSeparateFromNativeStreamAction()
    {
        var service = new FakeCatalog((_, _) => Task.FromResult(Page([])))
        { Resolve = (_, _, _, _) => Task.FromResult<IReadOnlyList<AudiovisualSource>>([
            Source("website") with { AccessMode = AudiovisualSourceAccessMode.WebPage },
            Source("stream")]) };
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await OpenWithSourcesAsync(vm, Item("1"));
        var website = Assert.Single(vm.Sources, source => source.ProviderName == "website");
        var stream = Assert.Single(vm.Sources, source => source.ProviderName == "stream");

        Assert.False(website.CanPlay);
        Assert.True(website.CanOpenWebsite);
        Assert.True(stream.CanPlay);
        Assert.False(stream.CanOpenWebsite);
        Assert.Contains("1 native stream option, 1 website fallback", vm.SourceStatusText);

        await vm.PlaySourceCommand.ExecuteAsync(website);
        await vm.WatchViaDownloadCommand.ExecuteAsync(website);
        await vm.OpenWebsiteSourceCommand.ExecuteAsync(stream);
        Assert.Equal(0, fixture.Dialog.ErrorCount);
        await vm.OpenWebsiteSourceCommand.ExecuteAsync(website);
        Assert.Equal(1, fixture.Dialog.ErrorCount); // The test URL reaches the network safety check.
    }

    [Fact]
    public void VerifiedIdentityStillWarnsWhenAudioLanguageHasNoEvidence()
    {
        var source = Source("verified") with { Evidence = new() { Origin = SourceEvidenceOrigin.ProviderItem } };
        var card = new AudiovisualSourceViewModel(source, new(SourceVerificationStatus.Verified, "identity_unit_verified"));
        Assert.Equal("Audio language unverified.", card.PlaybackNotice);
        Assert.Equal("Audio language unverified", card.LanguageText);
    }

    [Fact]
    public async Task NativeStreamMessageCarriesIndependentEvidenceAndOwnCaptionContext()
    {
        var item = Item("caption-handoff");
        var source = Source("native") with { Location = new("https://93.184.216.34/master.m3u8"),
            Identity = item.Identity, Evidence = new() { Origin = SourceEvidenceOrigin.ProviderItem,
                Identity = item.Identity, Unit = AudiovisualUnit.Feature },
            Subtitles = [new("https://captions.example/en.vtt", "English", "en", Cookie: "caption=own")] };
        var service = new UpdateCatalog((_, _) => Task.FromResult(Page([]))) { Updates = Read };
        async IAsyncEnumerable<AudiovisualSourceUpdate> Read(SourceSearchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            yield return new(request.OperationId, "native", AudiovisualSourceUpdateKind.SourceReady, source,
                new(SourceVerificationStatus.Verified, "identity_unit_verified"));
            await Task.Yield();
        }
        using var fixture = new Fixture(service, new DownloadHandler());
        await OpenWithSourcesAsync(fixture.ViewModel, item);
        object recipient = new();
        PlayMediaMessage? sent = null;
        WeakReferenceMessenger.Default.Register<PlayMediaMessage>(recipient, (_, message) =>
        { if (message.AudiovisualContext?.WorkKey == fixture.ViewModel.SelectedItem!.WorkKey) sent = message; });
        try
        {
            await fixture.ViewModel.PlaySourceCommand.ExecuteAsync(Assert.Single(fixture.ViewModel.Sources));
            Assert.NotNull(sent);
            Assert.Equal("caption=own", Assert.Single(sent.Subtitles).Cookie);
            Assert.Equal(SourceEvidenceOrigin.ProviderItem, sent.AudiovisualContext!.Evidence!.Origin);
            Assert.Equal("Audio language unverified.", sent.AudioNotice);
            Assert.False(sent.IsWebView);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task WatchViaDownloadSendsLocalNativeMediaWithIdentityAndSharedLeases()
    {
        var service = new FakeCatalog((_, _) => Task.FromResult(Page([])))
        { Resolve = (_, _, _, _) => Task.FromResult<IReadOnlyList<AudiovisualSource>>([
            Source("download") with { Location = new("https://93.184.216.34/movie.mp4") }]) };
        using var fixture = new Fixture(service, new DownloadHandler(), (_, _) => Task.FromResult("Audio language unverified"));
        var vm = fixture.ViewModel;
        await OpenWithSourcesAsync(vm, Item("temporary-film"));
        object recipient = new();
        var messages = new List<PlayMediaMessage>();
        WeakReferenceMessenger.Default.Register<PlayMediaMessage>(recipient, (_, message) =>
        { if (message.AudiovisualContext?.Identity.PrimaryId?.Value == "temporary-film") messages.Add(message); });
        try
        {
            var source = Assert.Single(vm.Sources);
            await vm.WatchViaDownloadCommand.ExecuteAsync(source);
            await vm.WatchViaDownloadCommand.ExecuteAsync(source);
            Assert.Equal(2, messages.Count);
            var first = messages[0];
            Assert.False(first.IsWebView);
            Assert.True(File.Exists(first.Value));
            Assert.StartsWith(fixture.TemporaryRoot, first.Value, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Same title", first.Title);
            Assert.Equal("temporary-film", first.AudiovisualContext!.Identity.PrimaryId!.Value);
            Assert.Equal("feature", first.AudiovisualContext.UnitKey);
            Assert.Contains("match unverified", first.AudioNotice);
            Assert.Contains("Audio language unverified", first.AudioNotice);
            Assert.Equal(first.Value, messages[1].Value);
            Assert.False(vm.IsDownloading);
            vm.Dispose(); // Closing details does not close its player tabs.
            first.TemporaryWatchLease!.Dispose();
            Assert.True(File.Exists(messages[1].Value));
            messages[1].TemporaryWatchLease!.Dispose();
            Assert.False(File.Exists(first.Value));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
            foreach (var message in messages) message.TemporaryWatchLease?.Dispose();
        }
    }

    [Theory]
    [InlineData("title")]
    [InlineData("cancel")]
    [InlineData("close")]
    public async Task ObsoleteOrCancelledTemporaryDownloadCannotOpenAPlayer(string action)
    {
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeCatalog((_, _) => Task.FromResult(Page([])))
        { Resolve = (_, _, _, _) => Task.FromResult<IReadOnlyList<AudiovisualSource>>([
            Source("download") with { Location = new("https://93.184.216.34/movie.mp4") }]) };
        using var fixture = new Fixture(service, new DownloadHandler(), (_, _) => { probing.TrySetResult(); return release.Task; });
        var vm = fixture.ViewModel;
        await OpenWithSourcesAsync(vm, Item("obsolete-download"));
        object recipient = new();
        int opened = 0;
        WeakReferenceMessenger.Default.Register<PlayMediaMessage>(recipient, (_, message) =>
        { if (message.AudiovisualContext?.Identity.PrimaryId?.Value == "obsolete-download") { opened++; message.TemporaryWatchLease?.Dispose(); } });
        try
        {
            Task download = vm.WatchViaDownloadCommand.ExecuteAsync(Assert.Single(vm.Sources));
            await probing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsDownloading);
            switch (action)
            {
                case "title": vm.SelectedItem = new(Item("new-title")); break;
                case "cancel": vm.CancelDownloadCommand.Execute(null); break;
                default: vm.Dispose(); break;
            }
            release.SetResult("Audio unverified");
            await download;
            Assert.Equal(0, opened);
            Assert.False(vm.IsDownloading);
            Assert.Equal(0, fixture.Dialog.ErrorCount);
            Assert.Empty(Directory.GetDirectories(fixture.TemporaryRoot));
        }
        finally { release.TrySetResult(""); WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    [Fact]
    public async Task VerifiedSourceAppearsBeforeSlowProviderCompletesAndSelectionChangeRemovesIt()
    {
        var released = new TaskCompletionSource();
        var firstReady = new TaskCompletionSource();
        var service = new UpdateCatalog((_, _) => Task.FromResult(Page([])))
        {
            Updates = ReadUpdates
        };
        async IAsyncEnumerable<AudiovisualSourceUpdate> ReadUpdates(SourceSearchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            var source = Source("ready");
            var verified = new SourceVerification(SourceVerificationStatus.Verified, "test_verified");
            yield return new(request.OperationId, "ready", AudiovisualSourceUpdateKind.SourceReady, source, verified);
            firstReady.SetResult();
            await released.Task.WaitAsync(token);
            yield return new(request.OperationId, "", AudiovisualSourceUpdateKind.Completed,
                Completion: new(1, 1, 0, 0, 0));
        }

        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await vm.OpenItemAsync(Item("1"));
        Task search = vm.FindSourcesCommand.ExecuteAsync(null);
        await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ready = Assert.Single(vm.Sources);
        Assert.True(ready.IsVerified);
        Assert.Equal("Stream", ready.StreamActionText);
        Assert.True(vm.IsResolvingSources);
        await vm.PlaySourceCommand.ExecuteAsync(ready);
        Assert.Equal(1, fixture.Dialog.ErrorCount); // Ready result is usable before the other provider ends.

        vm.SelectedItem = new(Item("2"));
        released.SetResult();
        await search;
        Assert.Empty(vm.Sources);
        Assert.False(vm.IsResolvingSources);
    }

    [Fact]
    public async Task UnverifiedCandidatesAreLabelledAndRejectedSourcesStayHidden()
    {
        var service = new UpdateCatalog((_, _) => Task.FromResult(Page([])))
        {
            Updates = (request, _) => ReadUpdates(request)
        };
        static async IAsyncEnumerable<AudiovisualSourceUpdate> ReadUpdates(SourceSearchRequest request)
        {
            yield return new(request.OperationId, "unknown", AudiovisualSourceUpdateKind.VerificationChanged,
                Source("unknown") with { Languages = ["en"] },
                new(SourceVerificationStatus.Unverified, "audio_evidence_missing"));
            yield return new(request.OperationId, "wrong", AudiovisualSourceUpdateKind.VerificationChanged,
                Source("wrong"), new(SourceVerificationStatus.Rejected, "episode_conflict"));
            yield return new(request.OperationId, "", AudiovisualSourceUpdateKind.Completed,
                Completion: new(2, 0, 1, 1, 0));
            await Task.CompletedTask;
        }

        using var fixture = new Fixture(service);
        await OpenWithSourcesAsync(fixture.ViewModel, Item("1"));
        var candidate = Assert.Single(fixture.ViewModel.Sources);
        Assert.Equal("unknown", candidate.ProviderName);
        Assert.False(candidate.IsVerified);
        Assert.Equal("Try stream", candidate.StreamActionText);
        Assert.Equal("Audio language unverified", candidate.LanguageText);
        Assert.Equal("Film/episode match unverified", candidate.UnitText);
        Assert.Contains("1 unverified candidate", fixture.ViewModel.SourceStatusText);
    }

    [Fact]
    public async Task ByteCheckedNativeCandidateIsUsableWhileIdentityRemainsUnverifiedAndAnotherProviderWaits()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new UpdateCatalog((_, _) => Task.FromResult(Page([]))) { Updates = Read };
        async IAsyncEnumerable<AudiovisualSourceUpdate> Read(SourceSearchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            yield return new(request.OperationId, "native", AudiovisualSourceUpdateKind.VerificationChanged,
                Source("native") with { MediaValidated = true }, new(SourceVerificationStatus.Unverified, "identity_missing"));
            waiting.SetResult();
            await release.Task;
        }
        using var fixture = new Fixture(service);
        await fixture.ViewModel.OpenItemAsync(Item("1"));
        Task search = fixture.ViewModel.FindSourcesCommand.ExecuteAsync(null);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var source = Assert.Single(fixture.ViewModel.Sources);
        Assert.True(fixture.ViewModel.IsResolvingSources);
        Assert.False(source.IsVerified);
        Assert.Equal("Try stream", source.StreamActionText);
        await fixture.ViewModel.PlaySourceCommand.ExecuteAsync(source);
        Assert.Equal(1, fixture.Dialog.ErrorCount); // Candidate is current; its private fixture URI reaches safety checks.
        release.SetResult();
        await search;
        Assert.Single(fixture.ViewModel.Sources);
    }

    [Fact]
    public async Task ProviderFailureAfterVerifiedResultKeepsReadySource()
    {
        var service = new UpdateCatalog((_, _) => Task.FromResult(Page([])))
        {
            Updates = (request, _) => ReadUpdates(request)
        };
        static async IAsyncEnumerable<AudiovisualSourceUpdate> ReadUpdates(SourceSearchRequest request)
        {
            yield return new(request.OperationId, "ready", AudiovisualSourceUpdateKind.SourceReady,
                Source("ready"), new(SourceVerificationStatus.Verified, "test_verified"));
            await Task.Yield();
            throw new InvalidDataException("slow provider failed");
        }

        using var fixture = new Fixture(service);
        await OpenWithSourcesAsync(fixture.ViewModel, Item("1"));
        Assert.True(Assert.Single(fixture.ViewModel.Sources).IsVerified);
        Assert.Contains("remain available", fixture.ViewModel.SourceStatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterConfirmedNativeIsPreferredWithoutInventingAudioAndCanUpgradeSameUrl(bool sameUrl)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new UpdateCatalog((_, _) => Task.FromResult(Page([]))) { Updates = Read };
        async IAsyncEnumerable<AudiovisualSourceUpdate> Read(SourceSearchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            var first = Source("early") with { MediaValidated = true };
            yield return new(request.OperationId, "early", AudiovisualSourceUpdateKind.VerificationChanged,
                first, new(SourceVerificationStatus.Unverified, "independent_identity_missing"));
            waiting.SetResult();
            await release.Task.WaitAsync(token);
            var confirmed = first with { ProviderName = "confirmed", ProviderId = "confirmed",
                Location = sameUrl ? first.Location : new("https://cdn.example/confirmed.m3u8"),
                Evidence = new() { Origin = SourceEvidenceOrigin.ProviderItem, Identity = request.Identity, Unit = request.Unit ?? AudiovisualUnit.Feature } };
            yield return new(request.OperationId, "confirmed", AudiovisualSourceUpdateKind.VerificationChanged,
                confirmed, new(SourceVerificationStatus.Unverified, "audio_evidence_missing"));
        }
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await vm.OpenItemAsync(Item("1"));
        Task search = vm.FindSourcesCommand.ExecuteAsync(null);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("early", Assert.Single(vm.Sources).ProviderName);
        Assert.True(vm.IsResolvingSources);
        release.SetResult();
        await search.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sameUrl ? 1 : 2, vm.Sources.Count);
        var preferred = vm.Sources[0];
        Assert.Equal("confirmed", preferred.ProviderName);
        Assert.False(preferred.IsVerified);
        Assert.Equal("Try stream", preferred.StreamActionText);
        Assert.Contains("Audio language unverified", preferred.PlaybackNotice);
        Assert.Empty(preferred.Source.Languages);
    }

    [Fact]
    public async Task SeriesDetailsLoadEpisodesWithoutStartingScrapersAndResolveChosenSeason()
    {
        var service = SeriesCatalog();
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await vm.OpenItemAsync(SeriesItem("526"));
        Assert.True(vm.ShowEpisodePicker);
        Assert.False(vm.ShowManualEpisodeNumbers);
        Assert.Empty(service.ResolvedUnits);
        Assert.Equal(2, vm.Seasons.Count);
        Assert.Null(vm.SelectedEpisode);
        await vm.FindSourcesCommand.ExecuteAsync(null);
        Assert.Empty(service.ResolvedUnits);
        vm.SelectedSeason = vm.Seasons.Single(s => s.Number == 2);
        vm.SelectedEpisode = Assert.Single(vm.Episodes);
        await vm.FindSourcesCommand.ExecuteAsync(null);
        var unit = Assert.Single(service.ResolvedUnits);
        Assert.Equal(2, unit!.SeasonNumber);
        Assert.Equal(1, unit.EpisodeNumber);
        Assert.Equal("Season two premiere", unit.Title);
        var stale = Assert.Single(vm.Sources);
        vm.SelectedSeason = vm.Seasons.Single(s => s.Number == 1);
        Assert.Empty(vm.Sources);
        Assert.Null(vm.SelectedEpisode);
        await vm.PlaySourceCommand.ExecuteAsync(stale);
        Assert.Equal(0, fixture.Dialog.ErrorCount);
    }

    [Fact]
    public async Task UnnumberedSpecialAndStaleEpisodeCannotResolveAsEpisodeOne()
    {
        var service = SeriesCatalog();
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await vm.OpenItemAsync(SeriesItem("526"));
        var oldEpisode = vm.Episodes.First();
        vm.SelectedEpisode = vm.Episodes.Single(e => e.Metadata.IsSpecial);
        await vm.FindSourcesCommand.ExecuteAsync(null);
        Assert.Contains("no exact playback numbering", vm.SourceStatusText);
        Assert.Empty(service.ResolvedUnits);
        vm.SelectedSeason = vm.Seasons.Single(s => s.Number == 2);
        vm.SelectedEpisode = oldEpisode;
        await vm.FindSourcesCommand.ExecuteAsync(null);
        Assert.Empty(service.ResolvedUnits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacedSeriesRejectsLateEpisodeSuccessAndFailure(bool fail)
    {
        var pending = new TaskCompletionSource<AudiovisualUnitsResult>();
        var started = new TaskCompletionSource();
        var service = SeriesCatalog();
        service.Units = (identity, _) =>
        {
            if (identity.PrimaryId!.Value == "526") { started.SetResult(); return pending.Task; }
            return Task.FromResult(Units([Episode("new", 3, 2)]));
        };
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        Task old = vm.OpenItemAsync(SeriesItem("526"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.FindSourcesCommand.ExecuteAsync(null);
        Assert.Contains("Wait", vm.SourceStatusText);
        Assert.Empty(service.ResolvedUnits);
        await vm.OpenItemAsync(SeriesItem("1292"));
        string status = vm.EpisodeStatusText;
        if (fail) pending.SetException(new InvalidOperationException("Late failure"));
        else pending.SetResult(Units([Episode("old", 9, 9)]));
        await old;
        Assert.Equal(3, Assert.Single(vm.Seasons).Number);
        Assert.Equal("new", Assert.Single(vm.Episodes).Metadata.Id.Value);
        Assert.Equal(status, vm.EpisodeStatusText);
        Assert.False(vm.IsLoadingEpisodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseOrDisposeCancelsEpisodeOwnerAndIgnoresLateResult(bool dispose)
    {
        var pending = new TaskCompletionSource<AudiovisualUnitsResult>();
        var started = new TaskCompletionSource<CancellationToken>();
        var service = SeriesCatalog();
        service.Units = (_, token) => { started.SetResult(token); return pending.Task; };
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        Task opening = vm.OpenItemAsync(SeriesItem("526"));
        var ownerToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) vm.Dispose(); else vm.CloseDetailsCommand.Execute(null);
        Assert.True(ownerToken.IsCancellationRequested);
        pending.SetResult(Units([Episode("late", 1, 1)]));
        await opening;
        Assert.Empty(vm.Seasons);
        Assert.Empty(vm.Episodes);
        Assert.False(vm.IsLoadingEpisodes);
    }

    [Fact]
    public async Task EpisodeFailureAllowsExplicitNumbersAndRetryRestoresPicker()
    {
        var service = SeriesCatalog();
        service.Units = (_, _) => Task.FromResult(new AudiovisualUnitsResult([], [new("test", ProviderOutcomeStatus.RateLimited, TimeSpan.Zero)]));
        using var fixture = new Fixture(service);
        var vm = fixture.ViewModel;
        await vm.OpenItemAsync(SeriesItem("526"));
        Assert.True(vm.ShowManualEpisodeNumbers);
        Assert.False(vm.ShowEpisodePicker);
        Assert.Contains("retry", vm.EpisodeStatusText);
        vm.SeasonNumberText = "2";
        vm.EpisodeNumberText = "7";
        await vm.FindSourcesCommand.ExecuteAsync(null);
        Assert.Equal(7, Assert.Single(service.ResolvedUnits)!.EpisodeNumber);
        service.Units = (_, _) => Task.FromResult(Units([Episode("numbered", 2, 7)]));
        await vm.LoadEpisodesCommand.ExecuteAsync(null);
        Assert.Empty(vm.Sources);
        Assert.True(vm.ShowEpisodePicker);
        Assert.False(vm.ShowManualEpisodeNumbers);
    }

    [Fact]
    public async Task AttributionOpensFixedProviderDomainForCurrentSeries()
    {
        using var fixture = new Fixture(SeriesCatalog());
        var vm = fixture.ViewModel;
        vm.OpenMetadataAttributionCommand.Execute(null);
        Assert.Equal("https://www.tvmaze.com", fixture.Launcher.LastUrl);
        await vm.OpenItemAsync(SeriesItem("526"));
        vm.OpenMetadataAttributionCommand.Execute(null);
        Assert.Equal("https://www.tvmaze.com/shows/526", fixture.Launcher.LastUrl);
    }

    [Theory]
    [InlineData(1280, true)]
    [InlineData(900, false)]
    public void EpisodeControlsBindAndRenderAtTwoWidths(int width, bool dark)
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            using var fixture = new Fixture(SeriesCatalog());
            var vm = fixture.ViewModel;
            vm.OpenItemAsync(SeriesItem("526")).GetAwaiter().GetResult();
            var view = AudiovisualCatalogRender.Load(dark);
            view.DataContext = vm;
            view.Width = width;
            view.Height = 800;
            view.Measure(new System.Windows.Size(width, 800));
            view.Arrange(new System.Windows.Rect(0, 0, width, 800));
            view.UpdateLayout();
            var selectors = AudiovisualCatalogRender.Descendants(view).OfType<System.Windows.Controls.ComboBox>().ToArray();
            var seasons = Assert.Single(selectors, c => System.Windows.Automation.AutomationProperties.GetName(c) == "Choose season");
            var episodes = Assert.Single(selectors, c => System.Windows.Automation.AutomationProperties.GetName(c) == "Choose episode");
            seasons.SelectedItem = vm.Seasons.Single(s => s.Number == 2);
            Assert.Equal(2, vm.SelectedSeason!.Number);
            episodes.SelectedIndex = 0;
            Assert.Equal("s2", vm.SelectedEpisode!.Metadata.Id.Value);
            view.UpdateLayout();
            foreach (var selector in new[] { seasons, episodes })
            {
                Assert.True(selector.ActualWidth > 100);
                double right = selector.TranslatePoint(new System.Windows.Point(selector.ActualWidth, 0), view).X;
                Assert.InRange(right, 0, width);
            }
            AudiovisualCatalogRender.Save(view, $"tv-episodes-{width}-{(dark ? "dark" : "light")}.png");
        });
    }

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(9, 1, false)]
    [InlineData(null, 1, false)]
    [InlineData(1, 9, false)]
    public async Task ReopenedTvDetailsSelectOnlyAnEstablishedSavedUnit(int? season, int episode, bool found)
    {
        using var fixture = new Fixture(SeriesCatalog());
        var item = SeriesItem("526");
        await fixture.Library.RecordProgressAsync(AudiovisualLibraryKey.Create(item.Identity), item.Title, "",
            season, episode, 123, 600);
        var vm = fixture.ViewModel;
        await vm.OpenDetailsCommand.ExecuteAsync(new AudiovisualCardViewModel(item));
        if (found)
        {
            Assert.Equal(season, vm.SelectedSeason!.Number);
            Assert.Equal(episode, vm.SelectedEpisode!.Metadata.Unit!.EpisodeNumber);
            Assert.Equal("s2", vm.SelectedEpisode.Metadata.Id.Value);
        }
        else Assert.Null(vm.SelectedEpisode);
        Assert.Contains("2:03", vm.ResumeText);
        if (season == null) Assert.Contains("Episode 1", vm.ResumeText);
    }

    [Fact]
    public async Task AmbiguousSavedTvNumberingDoesNotChooseOneCatalogEpisode()
    {
        var service = SeriesCatalog();
        service.Units = (_, _) => Task.FromResult(Units([Episode("first", 2, 1), Episode("duplicate", 2, 1)]));
        using var fixture = new Fixture(service);
        var item = SeriesItem("526");
        await fixture.Library.RecordProgressAsync(AudiovisualLibraryKey.Create(item.Identity), item.Title, "", 2, 1, 123, 600);
        await fixture.ViewModel.OpenDetailsCommand.ExecuteAsync(new AudiovisualCardViewModel(item));
        Assert.Equal(2, fixture.ViewModel.SelectedSeason!.Number);
        Assert.Null(fixture.ViewModel.SelectedEpisode);
    }

    private static AudiovisualMediaItem SeriesItem(string id) => new()
    { Title = "The Office", Identity = new() { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series,
        PrimaryId = new("tvmaze", "show", id), Title = "The Office", Year = 2005 } };
    private static AudiovisualEpisodeMetadata Episode(string id, int season, int? number, string title = "Pilot") =>
        new(new("test", "episode", id), title, season,
            number.HasValue ? new() { SeasonNumber = season, EpisodeNumber = number, Title = title } : null, !number.HasValue);
    private static AudiovisualUnitsResult Units(IReadOnlyList<AudiovisualEpisodeMetadata> items) =>
        new(items, [new("test", ProviderOutcomeStatus.Success, TimeSpan.Zero)]);
    private static FakeCatalog SeriesCatalog() => new((_, _) => Task.FromResult(Page([])))
    {
        Kind = AudiovisualMediaKind.Television,
        Units = (_, _) => Task.FromResult(Units([Episode("s1", 1, 1), Episode("special", 1, null), Episode("s2", 2, 1, "Season two premiere")])),
        Resolve = (_, _, _, _) => Task.FromResult<IReadOnlyList<AudiovisualSource>>([Source("current")])
    };

    [Fact]
    public async Task MovieDetailsDoNotStartOrWaitForSourceScrapers()
    {
        var service = new FakeCatalog((_, _) => Task.FromResult(Page([])))
        { Resolve = (_, _, _, _) => throw new InvalidOperationException("Details must not resolve sources") };
        using var fixture = new Fixture(service);
        await fixture.ViewModel.OpenItemAsync(Item("film"));
        Assert.True(fixture.ViewModel.IsDetailsOpen);
        Assert.Empty(service.ResolvedUnits);
        Assert.Contains("Find sources", fixture.ViewModel.SourceStatusText);
    }

    private static async Task OpenWithSourcesAsync(AudiovisualCatalogViewModel vm, AudiovisualMediaItem item)
    {
        await vm.OpenItemAsync(item);
        await vm.FindSourcesCommand.ExecuteAsync(null);
    }

    private static AudiovisualSource Source(string provider) => new()
    { ProviderName = provider, AccessMode = AudiovisualSourceAccessMode.DirectMedia,
        Location = new("http://127.0.0.1/blocked.mp4") };

    private static AudiovisualMediaItem Item(string id) => new()
    { Title = "Same title", Identity = new() { Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature,
        PrimaryId = new("test", "movie", id), Title = "Same title", Year = 2020 } };
    private static AudiovisualCatalogPage Page(IReadOnlyList<AudiovisualMediaItem> items, string? next = null) =>
        new(items, next, [new("test", ProviderOutcomeStatus.Success, TimeSpan.Zero)]);

    private class FakeCatalog(Func<AudiovisualCatalogRequest, CancellationToken, Task<AudiovisualCatalogPage>> getPage) : IPagedAudiovisualCatalogService, IAudiovisualEpisodeMetadataClient
    {
        public List<AudiovisualCatalogRequest> Requests { get; } = [];
        public Func<AudiovisualIdentity, AudiovisualUnit?, string?, CancellationToken, Task<IReadOnlyList<AudiovisualSource>>> Resolve { get; set; } =
            (_, _, _, _) => Task.FromResult<IReadOnlyList<AudiovisualSource>>([]);
        public List<AudiovisualUnit?> ResolvedUnits { get; } = [];
        public Func<AudiovisualIdentity, CancellationToken, Task<AudiovisualUnitsResult>> Units { get; set; } =
            (_, _) => Task.FromResult(new AudiovisualUnitsResult([], []));
        public Task<AudiovisualUnitsResult> GetUnitsAsync(AudiovisualIdentity identity, int? season = null, CancellationToken token = default) => Units(identity, token);
        public AudiovisualMediaKind Kind { get; init; } = AudiovisualMediaKind.Movie;
        public AudiovisualCatalogCapabilities Capabilities => AudiovisualCatalogCapabilities.Search | AudiovisualCatalogCapabilities.Continuation;
        public Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default)
        { Requests.Add(request); return getPage(request, token); }
        public Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(string query, CancellationToken token = default) => throw new InvalidOperationException("Must use pages");
        public Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(CancellationToken token = default) => throw new InvalidOperationException("Must use pages");
        public Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(AudiovisualIdentity identity, AudiovisualUnit? unit = null,
            string? preferredLanguage = null, CancellationToken token = default)
        { ResolvedUnits.Add(unit); return Resolve(identity, unit, preferredLanguage, token); }
    }

    private sealed class UpdateCatalog(Func<AudiovisualCatalogRequest, CancellationToken, Task<AudiovisualCatalogPage>> getPage)
        : FakeCatalog(getPage), IAudiovisualSourceUpdates
    {
        public required Func<SourceSearchRequest, CancellationToken, IAsyncEnumerable<AudiovisualSourceUpdate>> Updates { get; init; }
        public IAsyncEnumerable<AudiovisualSourceUpdate> FindSourceUpdatesAsync(SourceSearchRequest request,
            CancellationToken token = default) => Updates(request, token);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.PagingTests", Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        public string TemporaryRoot => Path.Combine(_root, "temporary");
        public TestViewModel ViewModel { get; }
        public AudiovisualLibraryService Library { get; }
        public Dialog Dialog { get; } = new();
        public Launcher Launcher { get; } = new();
        public Fixture(IAudiovisualCatalogService service, HttpMessageHandler? handler = null,
            Func<string, CancellationToken, Task<string>>? probe = null)
        {
            Directory.CreateDirectory(_root);
            _http = handler == null ? new() : new(handler);
            var config = new DomainHotSwapper(Path.Combine(_root, "config.json"));
            Library = new(Path.Combine(_root, "library.json"));
            ViewModel = new(service, Library,
                new(_http, config, TemporaryRoot, probe ?? AuthorizedMediaDownloadService.ProbeDownloadedMediaAsync), new(_http), Dialog, Launcher);
        }
        public void Dispose()
        {
            ViewModel.Dispose(); _http.Dispose();
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.PagingTests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
        }
    }
    private sealed class DownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new("video/mp4");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }
    private sealed class TestViewModel(IAudiovisualCatalogService service, AudiovisualLibraryService library,
        AuthorizedMediaDownloadService download, OtherMediaSourceSafetyService safety, Dialog dialog, Launcher launcher)
        : AudiovisualCatalogViewModel(service, library, download, safety, dialog, launcher);
    private sealed class Dialog : IDialogService
    {
        public int ErrorCount { get; private set; }
        public (bool DialogResult, SelectedSourceTier SelectedTier) ShowSourceSelection() => (false, SelectedSourceTier.None);
        public bool ShowConfirmDialog(string message, string title) => false;
        public void ShowErrorDialog(string message, string title) { ErrorCount++; }
        public void ShowInfoDialog(string message, string title) { }
    }
    private sealed class Launcher : IExternalLauncher
    {
        public int OpenCount { get; private set; }
        public string? LastUrl { get; private set; }
        public bool OpenUrl(string url) { OpenCount++; LastUrl = url; return false; }
        public bool OpenFolder(string folderPath) => false;
        public bool OpenFile(string filePath) => false;
    }
}
