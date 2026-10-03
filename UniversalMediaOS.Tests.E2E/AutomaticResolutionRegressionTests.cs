using System.IO;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Streaming;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class AutomaticResolutionRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeHlsStartsTheValidatedVariantAndRetainsLegacyMasters(bool validated)
    {
        const string variant = "https://example.com/1080.m3u8";
        var scraper = new StubResolver
        {
            Result = System.Text.Json.JsonSerializer.Deserialize<ScraperStreamResult>(validated
                ? "{\"url\":\"https://example.com/master.m3u8\",\"validated_hls_variant\":\"https://example.com/1080.m3u8\"}"
                : "{\"url\":\"https://example.com/master.m3u8\"}")! with
            {
                Subtitles = [new MediaSubtitleTrack("https://captions.example/en.vtt", "English", "en")]
            }
        };
        await WithRouter(scraper, async router =>
        {
            var source = await router.ResolveBestSourceAsync("Fixture", "1", "");
            Assert.NotNull(source);
            Assert.Equal(SourceTier.Tier1_PythonScraper, source.Tier);
            Assert.Equal(validated, source.UrlOrPath.Contains("&variant="));
            if (validated) Assert.Contains(Uri.EscapeDataString(variant), source.UrlOrPath);
            Assert.DoesNotContain("&url=", source.UrlOrPath); // Keep the master and its rendition groups.
            Assert.Equal("English", Assert.Single(source.Subtitles).Label);
            Assert.Equal(1, scraper.Calls);
        });
    }

    [Theory]
    [InlineData(SourceTier.Tier1_PythonScraper, false)]
    [InlineData(SourceTier.Tier1_PythonScraper, true)]
    [InlineData(SourceTier.Tier2_WebViewEmbed, false)]
    public async Task ExhaustedDiscoveryRunsOnlyOnce(SourceTier tier, bool fail)
    {
        var scraper = new StubResolver { Fail = fail };
        await WithRouter(scraper, async router =>
        {
            Assert.Null(await router.ResolveBestSourceAsync("Fixture", "1", "", minimumTier: tier));
            Assert.Equal(1, scraper.Calls);
        });
    }

    [Fact]
    public async Task ExplicitWebsiteChoiceUsesTheCapturedPageWithoutRepeatingDiscovery()
    {
        var scraper = new StubResolver
        {
            Result = new ScraperStreamResult("https://example.com/watch/fixture", null, null, null, null, null, true, null)
        };
        await WithRouter(scraper, async router =>
        {
            var result = await router.ResolveBestSourceAsync("Fixture", "1", "", minimumTier: SourceTier.Tier2_WebViewEmbed);
            Assert.NotNull(result);
            Assert.Equal(SourceTier.Tier2_WebViewEmbed, result.Tier);
            Assert.Equal(scraper.Result.Url, result.UrlOrPath);
            Assert.Equal(1, scraper.Calls);
            Assert.False(scraper.NativePreferred);
        });
    }

    [Theory]
    [InlineData("sub", "")]
    [InlineData("dub", "")]
    [InlineData("sub", "https://example.invalid/watch/{slug}/{episode}")]
    [InlineData("dub", "https://example.invalid/watch/{slug}/{episode}")]
    public async Task StreamNeverOpensABrowserOnlyResult(string audio, string savedProvider)
    {
        var scraper = new StubResolver
        {
            Result = new ScraperStreamResult("https://example.com/watch/fixture-7", null, null, null, null, null, true, null)
        };
        await WithRouter(scraper, async router =>
        {
            var status = new List<string>();
            Assert.Null(await router.ResolveBestSourceAsync("Fixture", "7", savedProvider, status.Add,
                audioPreference: audio));
            Assert.True(scraper.NativePreferred);
            Assert.Equal(1, scraper.Calls);
            Assert.Contains(status, message => message.Contains("choose Open website"));
        });
    }

    [Theory]
    [InlineData(SourceTier.Tier1_PythonScraper)]
    [InlineData(SourceTier.Tier2_WebViewEmbed)]
    public async Task CancellationDoesNotStartFallback(SourceTier tier)
    {
        var scraper = new StubResolver { Cancel = true };
        await WithRouter(scraper, async router =>
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => router.ResolveBestSourceAsync("Fixture", "1", "", minimumTier: tier));
            Assert.Equal(1, scraper.Calls);
        });
    }

    [Theory]
    [InlineData(SourceTier.Tier1_PythonScraper, "sub")]
    [InlineData(SourceTier.Tier1_PythonScraper, "dub")]
    [InlineData(SourceTier.Tier2_WebViewEmbed, "sub")]
    [InlineData(SourceTier.Tier2_WebViewEmbed, "dub")]
    public async Task AutomaticPlaybackKeepsEpisodeAndAudioAcrossNativeAndBrowserChoices(SourceTier tier, string audio)
    {
        var scraper = new StubResolver
        {
            Result = new ScraperStreamResult("https://example.com/watch/fixture-7", null, null, null, null, null, true, null)
        };
        await WithRouter(scraper, async router =>
        {
            var result = await router.ResolveBestSourceAsync("Fixture", "7", "", minimumTier: tier, audioPreference: audio);
            Assert.Equal(tier == SourceTier.Tier2_WebViewEmbed, result != null);
            Assert.Equal(tier == SourceTier.Tier1_PythonScraper, scraper.NativePreferred);
            Assert.Equal("Fixture", scraper.Query);
            Assert.Equal("7", scraper.Episode);
            Assert.Equal(audio, scraper.Audio);
            Assert.Equal(1, scraper.Calls);
        });
    }

    [Theory]
    [InlineData(SourceTier.Tier1_PythonScraper)]
    [InlineData(SourceTier.Tier2_WebViewEmbed)]
    public async Task CatalogAliasesReachTheSameRequestedEpisodeForBothPlaybackChoices(SourceTier tier)
    {
        const string english = "Frieren: Beyond Journey's End";
        const string romaji = "Sousou no Frieren";
        var scraper = new StubResolver
        {
            Result = tier == SourceTier.Tier2_WebViewEmbed
                ? new ScraperStreamResult("https://example.com/watch/frieren/ep-1", null, null, null,
                    null, null, true, null)
                : new ScraperStreamResult("https://example.com/frieren/ep-1.m3u8", null, null, null,
                    "https://example.com/watch/frieren/ep-1", null, false, null)
                    { AudioLanguages = ["eng"], SelectedAudio = "dub",
                      Subtitles = [new MediaSubtitleTrack("https://example.com/en.vtt", "English", "en")] }
        };
        await WithRouter(scraper, async router =>
        {
            var source = await router.ResolveBestSourceAsync(romaji, "1", "", minimumTier: tier,
                audioPreference: "dub", titleAliases: [english, romaji], aniListId: 154587, malId: 52991,
                titleSynonyms: ["A catalog synonym"]);
            Assert.NotNull(source);
            Assert.Equal(tier, source.Tier);
            Assert.Equal(romaji, scraper.Query);
            Assert.Equal("1", scraper.Episode);
            Assert.Equal("dub", scraper.Audio);
            Assert.Equal([english, romaji], scraper.AliasTitles);
            Assert.Equal(154587, scraper.AniListId);
            Assert.Equal(52991, scraper.MalId);
            Assert.Equal(["A catalog synonym"], scraper.TitleSynonyms);
            Assert.Equal(tier == SourceTier.Tier1_PythonScraper, scraper.NativePreferred);
            Assert.Equal(1, scraper.Calls);
            if (tier == SourceTier.Tier1_PythonScraper)
                Assert.Equal("English", Assert.Single(source.Subtitles).Label);
        });
    }

    [Theory]
    [InlineData(SourceTier.Tier1_PythonScraper)]
    [InlineData(SourceTier.Tier2_WebViewEmbed)]
    public async Task WrongDubCannotBecomeWebsiteSuccess(SourceTier tier)
    {
        var scraper = new StubResolver
        {
            Result = new ScraperStreamResult("https://example.com/file.mp4", null, null, null,
                "https://example.com/watch/fixture-7", null, false, null)
            {
                AudioLanguages = ["jpn"], SelectedAudio = "dub"
            }
        };
        await WithRouter(scraper, async router =>
        {
            Assert.Null(await router.ResolveBestSourceAsync("Fixture", "7", "", minimumTier: tier,
                audioPreference: "dub"));
            Assert.Equal(1, scraper.Calls);
        });
    }

    [Fact]
    public async Task StreamFailureCannotProbeASavedWebsiteUrl()
    {
        var scraper = new StubResolver { Fail = true };
        await WithRouter(scraper, async router =>
        {
            Assert.Null(await router.ResolveBestSourceAsync("Fixture", "7", "https://example.invalid/watch/{slug}/{episode}"));
            Assert.Equal(1, scraper.Calls);
        });
    }

    [Fact]
    public void FreshProfilesHaveNoPinnedProvidersAndSavingOtherSettingsPreservesLegacyUrls()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "config.json");
            var config = new DomainHotSwapper(path);
            Assert.Empty(config.GetCustomSources());
            const string legacy = "[{\"Name\":\"Saved manual URL\",\"Url\":\"https://example.com/search?q={query}\"}]";
            Assert.True(config.SetSettings(new Dictionary<string, string> { ["CustomSources"] = legacy }));
            Assert.True(config.SetSettings(new Dictionary<string, string> { ["DefaultAudioPref"] = "Dub" }));
            Assert.Equal(legacy, new DomainHotSwapper(path).GetSetting("CustomSources"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("jpn", null, false)]
    [InlineData("eng", null, true)]
    [InlineData("", null, false)]
    [InlineData(null, null, false)]
    [InlineData("jpn", "dub", false)]
    [InlineData("", "dub", true)]
    [InlineData("und", "dub", true)]
    [InlineData(null, "sub", false)]
    public async Task NativeDubRetainsProviderSelectionWithoutInventingLanguage(string? language, string? selectedAudio, bool expected)
    {
        var scraper = new StubResolver
        {
            Result = new ScraperStreamResult("https://example.com/episode.mp4", null, null, null, null, null, false, null)
            {
                AudioLanguages = [language!],
                SelectedAudio = selectedAudio,
                Subtitles = [new MediaSubtitleTrack("https://captions.example/en.vtt", "English", "en")]
            }
        };
        await WithRouter(scraper, async router =>
        {
            var source = await router.ResolveBestSourceAsync("Fixture", "1", "", audioPreference: "dub");
            Assert.Equal(expected, source != null);
            Assert.Equal(1, scraper.Calls);
            if (source != null)
            {
                Assert.Equal("English", Assert.Single(source.Subtitles).Label);
                Assert.Equal(language == "eng" ? "" : "Dub server selected · audio language unverified", source.AudioNotice);
            }
        });
    }

    private static async Task WithRouter(StubResolver scraper, Func<TripleNetHandoff, Task> assertion)
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var proxy = new HlsLoopbackProxy();
            var config = new DomainHotSwapper(Path.Combine(root, "config.json"));
            await assertion(new TripleNetHandoff(config, scraper, proxy));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class StubResolver : IScraperResolver
    {
        public bool IsAvailable => true;
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public bool Cancel { get; init; }
        public ScraperStreamResult? Result { get; init; }
        public string? Episode { get; private set; }
        public string? Audio { get; private set; }
        public string? Query { get; private set; }
        public bool NativePreferred { get; private set; }
        public IReadOnlyList<string>? AliasTitles { get; private set; }
        public IReadOnlyList<string>? TitleSynonyms { get; private set; }
        public int AniListId { get; private set; }
        public int MalId { get; private set; }
        public Task EnsureReadyAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ScraperStreamResult?> ResolveAsync(string query, string episodeId, int maxSiteAttempts,
            CancellationToken token = default, Action<string>? progressLog = null, string audioPreference = "sub",
            bool preferNative = true, IReadOnlyList<string>? titleAliases = null,
            int aniListId = 0, int malId = 0, IReadOnlyList<string>? titleSynonyms = null)
        {
            Calls++;
            Episode = episodeId;
            Audio = audioPreference;
            Query = query;
            NativePreferred = preferNative;
            AliasTitles = titleAliases;
            TitleSynonyms = titleSynonyms;
            AniListId = aniListId;
            MalId = malId;
            if (Cancel) throw new OperationCanceledException();
            if (Fail) throw new InvalidOperationException("Fixture failure");
            return Task.FromResult(Result);
        }
    }
}
