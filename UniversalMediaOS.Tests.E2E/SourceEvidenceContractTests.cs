using System.IO;
using System.Text.Json;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class SourceEvidenceContractTests
{
    private static AudiovisualIdentity Film => new() { Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature, Title = "Film", Year = 2020, TmdbId = 42 };
    private static SourceSearchRequest Request => new() { Identity = Film };
    private static AudiovisualSourceEvidence Evidence => new() { Origin = SourceEvidenceOrigin.ProviderItem, Identity = Film, Unit = AudiovisualUnit.Feature };
    private static AudioEvidence Audio(string language) => new() { Origin = SourceEvidenceOrigin.ObservedStream, Languages = [language] };
    private static AudiovisualSource Source => new()
    {
        ProviderId = "fixture", Location = new("https://media.example/video.mp4?token=private"),
        AccessMode = AudiovisualSourceAccessMode.DirectMedia, Evidence = Evidence, Identity = Film
    };

    [Fact]
    public void LegacyScraperPayloadIsUnverifiedAndNewEvidenceRoundTrips()
    {
        var old = JsonSerializer.Deserialize<AudiovisualScraperSearchResult>("""{"title":"Film","provider":"fixture","url":"https://media.example/"}""")!;
        Assert.Null(old.Evidence);
        Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(Request, old.Evidence).Status);
        var updated = old with { Evidence = Evidence };
        var roundTrip = JsonSerializer.Deserialize<AudiovisualScraperSearchResult>(JsonSerializer.Serialize(updated))!;
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(Request, roundTrip.Evidence).Status);
    }

    [Theory]
    [InlineData(SourceEvidenceOrigin.Unknown)]
    [InlineData(SourceEvidenceOrigin.RequestEcho)]
    public void EchoedSelectionNeverProvesIdentity(SourceEvidenceOrigin origin)
    {
        Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(Request, Evidence with { Origin = origin }).Status);
    }

    [Fact]
    public void ConflictingIdsCannotBeOverriddenByMatchingTitles()
    {
        var mismatched = Evidence with { Identity = Film with { TmdbId = 43 } };
        Assert.Equal(SourceVerificationStatus.Rejected, ExactAudiovisualMatcher.VerifyEvidence(Request, mismatched).Status);
        Assert.Equal(SourceVerificationStatus.Rejected, ExactAudiovisualMatcher.VerifyEvidence(Request, Evidence with { Identity = Film with { Kind = AudiovisualMediaKind.Television } }).Status);
        Assert.Equal(SourceVerificationStatus.Rejected, ExactAudiovisualMatcher.VerifyEvidence(Request, Evidence with { Identity = Film with { ContentForm = AudiovisualContentForm.Series } }).Status);
        Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(Request, Evidence with { Identity = Film with { ContentForm = AudiovisualContentForm.Unknown } }).Status);
    }

    [Fact]
    public void KeylessExactTitleYearAndFormCanVerifyWithoutExternalIds()
    {
        var identity = Film with { TmdbId = null };
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(Request with { Identity = identity }, Evidence with { Identity = identity }).Status);
        Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(Request with { Identity = identity }, Evidence with { Identity = identity with { Year = null } }).Status);
    }

    [Fact]
    public void SubtitleAndRequestedLanguageDoNotProveDubbedAudio()
    {
        var request = Request with { AudioLanguage = "ar" };
        Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(request, Evidence with { Subtitles = Audio("ar") }).Status);
        Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(request, Evidence with { Audio = Audio("ar") with { Origin = SourceEvidenceOrigin.RequestEcho } }).Status);
        Assert.Equal(SourceVerificationStatus.Rejected, ExactAudiovisualMatcher.VerifyEvidence(request, Evidence with { Audio = Audio("ja"), Subtitles = Audio("ar") }).Status);
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(request, Evidence with { Audio = Audio("ar") }).Status);
        Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(request with { SubtitleLanguage = "en" }, Evidence with { Audio = Audio("ar") }).Status);
    }

    [Fact]
    public void ObservedThreeLetterAudioTagsMatchRequestedTwoLetterLanguages()
    {
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(Request with { AudioLanguage = "en" }, Evidence with { Audio = Audio("eng") }).Status);
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(Request with { AudioLanguage = "ja" }, Evidence with { Audio = Audio("jpn") }).Status);
        Assert.Equal(SourceVerificationStatus.Rejected, ExactAudiovisualMatcher.VerifyEvidence(Request with { AudioLanguage = "en" }, Evidence with { Audio = Audio("jpn") }).Status);
    }

    [Fact]
    public void EpisodesAndSpecialsNeedExactUnitEvidence()
    {
        var series = Film with { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series };
        var unit = new AudiovisualUnit { SeasonNumber = 2, EpisodeNumber = 3 };
        var request = Request with { Identity = series, Unit = unit };
        var evidence = Evidence with { Identity = series, Unit = unit };
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(request, evidence).Status);
        Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(request, evidence with { Unit = null }).Status);
        Assert.Equal(SourceVerificationStatus.Rejected, ExactAudiovisualMatcher.VerifyEvidence(request, evidence with { Unit = unit with { EpisodeNumber = 4 } }).Status);
        var special = unit with { SeasonNumber = 0 };
        Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(request with { Unit = special }, evidence with { Unit = special }).Status);
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(request with { Unit = special }, evidence with { Unit = special, SpecialUnitEstablished = true }).Status);
    }

    [Fact]
    public async Task FastSourcesArePublishedBeforeSlowBatchAndDisposeCancelsRemainingWork()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SourceProviderBatch slow = new("slow", async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.TrySetResult(); }
            return Array.Empty<AudiovisualSource>();
        });
        var request = Request;
        var stream = SourceUpdateStream.ReadAsync(request, [slow, new("fast", _ => Task.FromResult<IReadOnlyList<AudiovisualSource>>([Source]))]);
        await using (var iterator = stream.GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(AudiovisualSourceUpdateKind.CandidateDiscovered, iterator.Current.Kind);
            Assert.Equal(request.OperationId, iterator.Current.OperationId);
            Assert.DoesNotContain("private", iterator.Current.CandidateId);
            Assert.True(await iterator.MoveNextAsync());
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal(AudiovisualSourceUpdateKind.SourceReady, iterator.Current.Kind);
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task UnverifiedAndUnsupportedSourcesAreNotReadyAndFailuresHaveNoSecrets()
    {
        var sources = new[] { Source with { Evidence = null }, Source with { Location = new("file:///C:/video.mp4") }, Source with { Location = new("https://media.example/valid") } };
        var updates = new List<AudiovisualSourceUpdate>();
        await foreach (var update in SourceUpdateStream.ReadAsync(Request,
            [new("good", _ => Task.FromResult<IReadOnlyList<AudiovisualSource>>(sources)), new("failed", _ => throw new InvalidOperationException("secret-token"))])) updates.Add(update);
        Assert.Single(updates, update => update.Kind == AudiovisualSourceUpdateKind.SourceReady);
        var failure = Assert.Single(updates, update => update.Kind == AudiovisualSourceUpdateKind.ProviderFailed);
        Assert.DoesNotContain("secret-token", JsonSerializer.Serialize(failure));
        Assert.Equal(new SourceCompletion(3, 1, 1, 1, 1), updates.Last().Completion);
    }

    [Fact]
    public async Task CancellationDoesNotEmitSuccessfulCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var updates = new List<AudiovisualSourceUpdate>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in SourceUpdateStream.ReadAsync(Request,
                [new("fixture", _ => Task.FromResult<IReadOnlyList<AudiovisualSource>>([Source]))], token: cancellation.Token))
            {
                updates.Add(update);
                cancellation.Cancel();
            }
        });
        Assert.DoesNotContain(updates, update => update.Kind == AudiovisualSourceUpdateKind.Completed);
    }

    [Fact]
    public async Task StreamingProviderPublishesCandidatesBeforeItsOwnSearchFinishesAndDisposalCancelsIt()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<AudiovisualSource> Read([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                yield return Source with { Evidence = null };
                await release.Task.WaitAsync(token);
                yield return Source with { Location = new("https://media.example/confirmed") };
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally { cancelled.TrySetResult(); }
        }
        var provider = new SourceProviderBatch("stream", _ => throw new InvalidOperationException("batch should not run")) { Stream = Read };
        await using (var iterator = SourceUpdateStream.ReadAsync(Request, [provider]).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(AudiovisualSourceUpdateKind.CandidateDiscovered, iterator.Current.Kind);
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal(SourceVerificationStatus.Unverified, iterator.Current.Verification!.Status);
            Assert.False(cancelled.Task.IsCompleted);
            release.SetResult();
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(await iterator.MoveNextAsync());
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal(AudiovisualSourceUpdateKind.SourceReady, iterator.Current.Kind);
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task StreamingProviderFailureRetainsPublishedSourcesAndReportsAccurateCompletion()
    {
        static async IAsyncEnumerable<AudiovisualSource> Read([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            yield return Source;
            await Task.Yield();
            throw new IOException("private-provider-token");
        }
        var provider = new SourceProviderBatch("stream", _ => throw new InvalidOperationException()) { Stream = Read };
        var updates = new List<AudiovisualSourceUpdate>();
        await foreach (var update in SourceUpdateStream.ReadAsync(Request, [provider])) updates.Add(update);
        Assert.Single(updates, update => update.Kind == AudiovisualSourceUpdateKind.SourceReady);
        var failure = Assert.Single(updates, update => update.Kind == AudiovisualSourceUpdateKind.ProviderFailed);
        Assert.DoesNotContain("private-provider-token", JsonSerializer.Serialize(failure));
        Assert.Equal(new SourceCompletion(1, 1, 0, 0, 1), updates.Last().Completion);
    }

    [Fact]
    public async Task ArabicCartoonLaneStillRequiresAdditionalTransportVerification()
    {
        var cartoon = Film with { Kind = AudiovisualMediaKind.Cartoon, IsAnimated = true };
        var request = Request with { Identity = cartoon, RequireArabicCartoonVerification = true };
        var candidate = Source with { Evidence = Evidence with { Identity = cartoon, Audio = Audio("ar") }, IsArabicCartoonVerified = true };
        var updates = new List<AudiovisualSourceUpdate>();
        await foreach (var update in SourceUpdateStream.ReadAsync(request,
            [new("fixture", _ => Task.FromResult<IReadOnlyList<AudiovisualSource>>([candidate]))])) updates.Add(update);
        Assert.DoesNotContain(updates, update => update.Kind == AudiovisualSourceUpdateKind.SourceReady);
        Assert.Equal("arabic_transport_unverified", Assert.Single(updates, update => update.Kind == AudiovisualSourceUpdateKind.VerificationChanged).Verification!.ReasonCode);
    }

    [Fact]
    public async Task CatalogExposesUpdatesAndUnsupportedExplicitProviderDoesNotFallBack()
    {
        IAudiovisualSourceUpdates catalog = new MovieService();
        var updates = new List<AudiovisualSourceUpdate>();
        await foreach (var update in catalog.FindSourceUpdatesAsync(Request with { ProviderId = "unknown-provider" })) updates.Add(update);
        Assert.Equal(ProviderOutcomeStatus.Unsupported, updates[0].Outcome!.Status);
        Assert.Equal(new SourceCompletion(0, 0, 0, 0, 1), updates.Last().Completion);
    }
}
