using System.IO;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class ScraperAudiovisualNativeTests
{
    [Fact]
    public async Task ExcludedFailedMediaDoesNotCancelAnotherIndependentlyVerifiedProvider()
    {
        using var fixture = new Fixture(async (url, token) =>
        {
            if (url.EndsWith("slow")) await Task.Delay(30, token);
            return Native() with { Url = url.EndsWith("slow") ? "https://cdn.example/replacement.mpd" : "https://cdn.example/video.m3u8?token=rotated",
                Evidence = new() { Origin = SourceEvidenceOrigin.ProviderItem, Identity = Film, Unit = AudiovisualUnit.Feature } };
        });
        var sources = new List<AudiovisualSource>();
        await foreach (var source in fixture.Provider.FindSourceCandidatesAsync(new()
        { Identity = Film, RequireVerifiedSource = true, ExcludedMediaPaths = ["https://cdn.example/video.m3u8"] }))
            sources.Add(source);
        var selected = Assert.Single(sources);
        Assert.Equal("scraper-slow", selected.ProviderId);
        Assert.Equal("/replacement.mpd", selected.Location.AbsolutePath);
    }

    [Fact]
    public async Task StrictDownloadRequestKeepsLookingWhenTheFirstNativeHasUnknownAudio()
    {
        using var fixture = new Fixture(async (url, token) =>
        {
            if (url.EndsWith("slow")) await Task.Delay(30, token);
            return Native() with { Evidence = new() { Origin = SourceEvidenceOrigin.ProviderItem, Identity = Film,
                Unit = AudiovisualUnit.Feature, Audio = url.EndsWith("slow")
                    ? new() { Origin = SourceEvidenceOrigin.ObservedStream, Languages = ["en"] } : null } };
        });
        var request = new SourceSearchRequest { Identity = Film, AudioLanguage = "en", RequireVerifiedSource = true };
        var sources = new List<AudiovisualSource>();
        await foreach (var source in fixture.Provider.FindSourceCandidatesAsync(request)) sources.Add(source);
        var selected = Assert.Single(sources);
        Assert.Equal("scraper-slow", selected.ProviderId);
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(request, selected.Evidence).Status);
    }

    [Fact]
    public async Task AllExcludedMediaFinishWithoutPublishingOrRetryingTheSameFile()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult<AudiovisualScraperStreamResult?>(Native() with
        { Evidence = new() { Origin = SourceEvidenceOrigin.ProviderItem, Identity = Film, Unit = AudiovisualUnit.Feature } }));
        int sources = 0;
        await foreach (var _ in fixture.Provider.FindSourceCandidatesAsync(new()
        { Identity = Film, RequireVerifiedSource = true, ExcludedMediaPaths = ["https://cdn.example/master.m3u8"] })) sources++;
        Assert.Equal(0, sources);
    }

    [Fact]
    public async Task IndependentlyMatchedNativeCancelsSlowAlternativeAndDoesNotInventEnglishAudio()
    {
        var identity = Film;
        var evidence = new AudiovisualSourceEvidence { Origin = SourceEvidenceOrigin.ProviderItem,
            Identity = identity, Unit = AudiovisualUnit.Feature };
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (url, token) =>
        {
            if (url.EndsWith("slow"))
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cancelled.TrySetResult(); }
            }
            return Native() with { Evidence = evidence };
        });
        var sources = await fixture.Provider.FindSourcesAsync(identity, preferredLanguage: "en").WaitAsync(TimeSpan.FromSeconds(5));
        var source = Assert.Single(sources);
        Assert.True(source.MediaValidated);
        Assert.Equal(AudiovisualSourceAccessMode.DirectMedia, source.AccessMode);
        Assert.Equal("https://cdn.example/1080/index.m3u8", source.ValidatedHlsVariant);
        Assert.Empty(source.Languages);
        Assert.Equal(evidence, source.Evidence);
        Assert.Equal("audio_evidence_missing", ExactAudiovisualMatcher.VerifyEvidence(
            new() { Identity = identity, AudioLanguage = "en" }, source.Evidence).ReasonCode);
        Assert.Equal("imdb:tt1160419", fixture.Engine.Query);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(SourceEvidenceOrigin.Unknown)]
    [InlineData(SourceEvidenceOrigin.RequestEcho)]
    public async Task EarlyUnverifiedNativeIsPublishedWithoutCancellingLaterIndependentMatch(SourceEvidenceOrigin origin)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new AudiovisualSourceEvidence { Origin = SourceEvidenceOrigin.ProviderItem,
            Identity = Film, Unit = AudiovisualUnit.Feature };
        using var fixture = new Fixture(async (url, token) =>
        {
            if (url.EndsWith("slow"))
            {
                await release.Task.WaitAsync(token);
                return Native() with { Evidence = evidence };
            }
            return Native() with { Evidence = origin == SourceEvidenceOrigin.Unknown ? null : evidence with { Origin = origin } };
        });
        var request = new SourceSearchRequest { Identity = Film, AudioLanguage = "en" };
        await using var iterator = fixture.Provider.FindSourceCandidatesAsync(request).GetAsyncEnumerator();
        try
        {
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("scraper-native", iterator.Current.ProviderId);
            Assert.Equal(SourceVerificationStatus.Unverified, ExactAudiovisualMatcher.VerifyEvidence(request, iterator.Current.Evidence).Status);
            release.SetResult();
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("scraper-slow", iterator.Current.ProviderId);
            Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(
                request with { AudioLanguage = null }, iterator.Current.Evidence).Status);
            Assert.Empty(iterator.Current.Languages);
            Assert.False(await iterator.MoveNextAsync());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ListResultPrefersLaterIndependentNativeMatch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var early = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (url, token) =>
        {
            if (url.EndsWith("slow"))
            {
                await release.Task.WaitAsync(token);
                return Native() with { Evidence = new() { Origin = SourceEvidenceOrigin.ProviderItem,
                    Identity = Film, Unit = AudiovisualUnit.Feature } };
            }
            early.SetResult();
            return Native();
        });
        var search = fixture.Provider.FindSourcesAsync(Film);
        await early.Task.WaitAsync(TimeSpan.FromSeconds(3));
        release.SetResult();
        var sources = await search.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, sources.Count);
        Assert.Equal("scraper-slow", sources[0].ProviderId);
        Assert.Equal("scraper-native", sources[1].ProviderId);
    }

    [Fact]
    public async Task DisposingAfterUnverifiedCandidateCancelsOwnedAlternative()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (url, token) =>
        {
            if (url.EndsWith("slow"))
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cancelled.TrySetResult(); }
            }
            return Native();
        });
        await using (var iterator = fixture.Provider.FindSourceCandidatesAsync(new() { Identity = Film }).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(cancelled.Task.IsCompleted);
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData("episode")]
    [InlineData("audio")]
    public async Task KnownUnitOrAudioConflictCannotCancelMatchingAlternative(string conflict)
    {
        var identity = Film with { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series };
        var unit = new AudiovisualUnit { SeasonNumber = 2, EpisodeNumber = 1 };
        var evidence = new AudiovisualSourceEvidence { Origin = SourceEvidenceOrigin.ProviderItem, Identity = identity,
            Unit = unit, Audio = new() { Origin = SourceEvidenceOrigin.ObservedStream, Languages = ["en"] } };
        using var fixture = new Fixture(async (url, token) =>
        {
            if (!url.EndsWith("slow")) await Task.Delay(25, token);
            return Native() with { Evidence = !url.EndsWith("slow") ? evidence : conflict == "episode"
                ? evidence with { Unit = unit with { SeasonNumber = 1 } }
                : evidence with { Audio = evidence.Audio with { Languages = ["jpn"] } } };
        });
        var source = Assert.Single(await fixture.Provider.FindSourcesAsync(identity, unit, "en"));
        Assert.Equal("scraper-native", source.ProviderId);
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(
            new() { Identity = identity, Unit = unit, AudioLanguage = "en" }, source.Evidence).Status);
    }

    [Fact]
    public async Task BrowserConcurrencyIsBoundedWithoutDroppingLaterCandidates()
    {
        int active = 0, peak = 0, visited = 0;
        using var fixture = new Fixture(async (url, token) =>
        {
            int count = Interlocked.Increment(ref active);
            peak = Math.Max(peak, count);
            Interlocked.Increment(ref visited);
            try
            {
                await Task.Delay(30, token);
                return url.EndsWith("4") ? Native() : null;
            }
            finally { Interlocked.Decrement(ref active); }
        });
        fixture.Engine.Results = Enumerable.Range(0, 5)
            .Select(index => new AudiovisualScraperSearchResult("Film", "Provider", "https://player.example/" + index)).ToArray();
        Assert.Single(await fixture.Provider.FindSourcesAsync(new() { Title = "Film" }));
        Assert.Equal(5, visited);
        Assert.InRange(peak, 1, 2);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task FailedAlternativeCannotDiscardNativeSuccess()
    {
        using var fixture = new Fixture((url, _) => url.EndsWith("slow")
            ? Task.FromException<AudiovisualScraperStreamResult?>(new IOException("offline"))
            : Task.FromResult<AudiovisualScraperStreamResult?>(Native() with { AudioLanguages = ["jpn"] }));
        var source = Assert.Single(await fixture.Provider.FindSourcesAsync(new() { Title = "Film" }));
        Assert.Equal("jpn", Assert.Single(source.Languages));
        Assert.True(source.MediaValidated);
    }

    [Fact]
    public async Task UncheckedDirectResultIsNotPresentedAsNativeMedia()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult<AudiovisualScraperStreamResult?>(Native() with { MediaValidated = false }));
        Assert.Empty(await fixture.Provider.FindSourcesAsync(new() { Title = "Film" }));
    }

    [Fact]
    public async Task WebsiteResultRemainsAnExplicitFallbackWhenNoNativeCandidateWorks()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult<AudiovisualScraperStreamResult?>(Native() with { RequiresWebView = true, MediaValidated = false }));
        var sources = await fixture.Provider.FindSourcesAsync(new() { Title = "Film" });
        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.Equal(AudiovisualSourceAccessMode.WebPage, source.AccessMode));
    }

    [Fact]
    public async Task ConflictingFirstNativeDoesNotCancelMatchingAlternative()
    {
        var identity = new AudiovisualIdentity { Title = "Dune", Year = 2021, ImdbId = "tt1160419",
            Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature };
        var evidence = new AudiovisualSourceEvidence { Origin = SourceEvidenceOrigin.ProviderItem,
            Identity = identity, Unit = AudiovisualUnit.Feature };
        using var fixture = new Fixture(async (url, token) =>
        {
            if (!url.EndsWith("slow")) await Task.Delay(25, token);
            return Native() with { Evidence = evidence with { Identity = url.EndsWith("slow")
                ? identity with { ImdbId = "tt0087182", Year = 1984 } : identity } };
        });
        var source = Assert.Single(await fixture.Provider.FindSourcesAsync(identity));
        Assert.Equal("scraper-native", source.ProviderId);
        Assert.Equal("tt1160419", source.Evidence!.Identity!.ImdbId);
    }

    [Fact]
    public async Task ResolvedItemEvidenceAndCaptionContextOverrideRequestEcho()
    {
        var identity = new AudiovisualIdentity { Title = "Dune", Year = 2021, ImdbId = "tt1160419",
            Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature };
        var evidence = new AudiovisualSourceEvidence { Origin = SourceEvidenceOrigin.ProviderItem,
            Identity = identity, Unit = AudiovisualUnit.Feature,
            Subtitles = new() { Origin = SourceEvidenceOrigin.ProviderItem, Languages = ["en"] } };
        var header = new Dictionary<string, string> { ["X-Caption"] = "own" };
        using var fixture = new Fixture((_, _) => Task.FromResult<AudiovisualScraperStreamResult?>(Native() with {
            Evidence = evidence, Subtitles = [new("https://captions.example/en.vtt", "English", "en", RequestHeaders: header)] }));
        fixture.Engine.Results = [new("Dune", "Provider", "https://player.example/movie") {
            Evidence = evidence with { Origin = SourceEvidenceOrigin.RequestEcho } }];
        var source = Assert.Single(await fixture.Provider.FindSourcesAsync(identity));
        header["X-Caption"] = "changed";
        Assert.Equal(SourceEvidenceOrigin.ProviderItem, source.Evidence!.Origin);
        Assert.Equal("own", Assert.Single(source.Subtitles).RequestHeaders!["X-Caption"]);
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(new() { Identity = identity }, source.Evidence).Status);
        Assert.Equal("audio_evidence_missing", ExactAudiovisualMatcher.VerifyEvidence(new() { Identity = identity, AudioLanguage = "en" }, source.Evidence).ReasonCode);
    }

    [Fact]
    public async Task CheckedExtensionlessDashKeepsItsTypeAndExactUnitWithoutCertifyingAudio()
    {
        var identity = Film;
        var evidence = new AudiovisualSourceEvidence { Origin = SourceEvidenceOrigin.ProviderItem,
            Identity = identity, Unit = AudiovisualUnit.Feature };
        var checkedDash = JsonSerializer.Deserialize<AudiovisualScraperStreamResult>("""
            {"url":"https://cdn.example/manifest","media_validated":true,
             "content_type":"application/dash+xml","audio_languages":[],"requires_webview":false}
            """)! with { Evidence = evidence };
        using var fixture = new Fixture((_, _) => Task.FromResult<AudiovisualScraperStreamResult?>(checkedDash));
        fixture.Engine.Results = [new("Film", "Provider", "https://player.example/movie")];
        var source = Assert.Single(await fixture.Provider.FindSourcesAsync(identity));
        Assert.Equal("application/dash+xml", source.ContentType);
        Assert.Equal(AudiovisualSourceAccessMode.DirectMedia, source.AccessMode);
        Assert.Equal(SourceVerificationStatus.Verified, ExactAudiovisualMatcher.VerifyEvidence(new() { Identity = identity }, source.Evidence).Status);
        Assert.Equal("audio_evidence_missing", ExactAudiovisualMatcher.VerifyEvidence(new() { Identity = identity, AudioLanguage = "en" }, source.Evidence).ReasonCode);
        Assert.Empty(source.Languages);
    }

    [Fact]
    public void CliCarriesCheckedRenditionAndRealAudioEvidence()
    {
        var result = JsonSerializer.Deserialize<AudiovisualScraperStreamResult>("""
            {"url":"https://cdn.example/master.m3u8","media_validated":true,
             "validated_hls_variant":"https://cdn.example/1080/index.m3u8","audio_languages":["jpn"],
             "evidence":{"Origin":"ProviderItem","Identity":{"Title":"Dune","Year":2021,
                "Kind":"Movie","ContentForm":"Feature","ImdbId":"tt1160419"},"Unit":{}},
             "subtitles":[{"url":"https://captions.example/en.vtt","language":"en","label":"English",
                "inline_vtt":"WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nEnglish caption"}]}
            """)!;
        Assert.True(result.MediaValidated);
        Assert.Equal("jpn", Assert.Single(result.AudioLanguages!));
        Assert.Contains("1080", result.ValidatedHlsVariant);
        Assert.Equal("tt1160419", result.Evidence!.Identity!.ImdbId);
        Assert.True(Assert.Single(result.Subtitles!).IsEnglish);
        Assert.Contains("English caption", Assert.Single(result.Subtitles!).InlineVtt);
    }

    private static AudiovisualScraperStreamResult Native() => new("https://cdn.example/master.m3u8", "agent", null,
        "https://player.example/episode", null, false, null)
    { MediaValidated = true, ValidatedHlsVariant = "https://cdn.example/1080/index.m3u8" };

    private static AudiovisualIdentity Film => new() { Title = "Film", Kind = AudiovisualMediaKind.Movie,
        ContentForm = AudiovisualContentForm.Feature, Year = 2021, ImdbId = "tt1160419", TmdbId = 438631 };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.NativeScraperTests", Guid.NewGuid().ToString("N"));
        private readonly PythonBootstrapper _python = new();
        public FakeEngine Engine { get; }
        public ScraperAudiovisualSourceProvider Provider { get; }
        public Fixture(Func<string, CancellationToken, Task<AudiovisualScraperStreamResult?>> resolve)
        {
            Directory.CreateDirectory(_root);
            Engine = new(_python, resolve);
            Provider = new(Engine, new DomainHotSwapper(Path.Combine(_root, "config.json")));
        }
        public void Dispose()
        {
            _python.Dispose();
            string parent = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.NativeScraperTests") + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
        }
    }
    private sealed class FakeEngine(PythonBootstrapper python,
        Func<string, CancellationToken, Task<AudiovisualScraperStreamResult?>> resolve) : AudiovisualScraperEngine(python)
    {
        public string? Query { get; private set; }
        public AudiovisualScraperSearchResult[] Results { get; set; } = [
            new("Film", "Slow", "https://player.example/slow"), new("Film", "Native", "https://player.example/native")];
        public override Task<AudiovisualScraperSearchResult[]> SearchAsync(string query, string kind, int? year,
            int? season, int? episode, string mirrorUrl, CancellationToken token = default)
        {
            Query = query;
            return Task.FromResult(Results);
        }
        public override Task<AudiovisualScraperStreamResult?> ResolveAsync(string embedUrl, CancellationToken token = default) => resolve(embedUrl, token);
    }
}
