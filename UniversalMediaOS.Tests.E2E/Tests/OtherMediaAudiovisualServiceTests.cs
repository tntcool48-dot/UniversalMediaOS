using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class OtherMediaAudiovisualServiceTests
{
    [Fact]
    public void ExactMatcher_RequiresExactYearKindAndEpisode()
    {
        var requested = new AudiovisualIdentity
        {
            Kind = AudiovisualMediaKind.Television,
            Title = "The Example Show",
            AlternateTitles = ["Example Show"],
            Year = 2024
        };
        var exactAlias = new AudiovisualIdentity
        {
            Kind = AudiovisualMediaKind.Television,
            Title = "Example Show",
            Year = 2024
        };
        var wrongYear = exactAlias with { Year = 2023 };
        var wrongKind = exactAlias with { Kind = AudiovisualMediaKind.Cartoon };
        var requestedUnit = new AudiovisualUnit { SeasonNumber = 2, EpisodeNumber = 4 };

        Assert.True(ExactAudiovisualMatcher.IsExactMatch(
            requested,
            requestedUnit,
            exactAlias,
            new AudiovisualUnit { SeasonNumber = 2, EpisodeNumber = 4 }));
        Assert.False(ExactAudiovisualMatcher.IsExactMatch(
            requested,
            requestedUnit,
            wrongYear,
            new AudiovisualUnit { SeasonNumber = 2, EpisodeNumber = 4 }));
        Assert.False(ExactAudiovisualMatcher.IsExactMatch(
            requested,
            requestedUnit,
            wrongKind,
            new AudiovisualUnit { SeasonNumber = 2, EpisodeNumber = 4 }));
        Assert.False(ExactAudiovisualMatcher.IsExactMatch(
            requested,
            requestedUnit,
            exactAlias,
            new AudiovisualUnit { SeasonNumber = 2, EpisodeNumber = 5 }));
    }

    [Fact]
    public async Task ManifestLoader_PreservesAuthorizationWithoutImposingALicenseGate()
    {
        string valid = ManifestJson(new
        {
            id = "licensed.example",
            name = "Licensed Example",
            searchUrlTemplate = "https://provider.example/search?q={query}",
            mediaKinds = new[] { "Movie" },
            authorization = "UserAuthorized",
            rightsStatement = "The user confirms this provider is authorized.",
            requestsPerMinute = 0,
            cacheSeconds = 60,
            timeoutSeconds = 5,
            maxResponseBytes = 4096
        });
        var loader = new AudiovisualProviderManifestLoader(
            new HttpClient(new StubHttpHandler((_, _) =>
                throw new InvalidOperationException("Inline manifests do not use the network."))));

        AudiovisualProviderManifest manifest = await loader.LoadAsync(valid);

        AudiovisualProviderDefinition provider = Assert.Single(manifest.Providers);
        Assert.Equal("licensed.example", provider.Id);
        Assert.Equal(ProviderAuthorization.UserAuthorized, provider.Authorization);

        string missingRights = ManifestJson(new
        {
            id = "invalid.example",
            name = "Invalid",
            searchUrlTemplate = "https://provider.example/search?q={query}",
            mediaKinds = new[] { "Movie" },
            authorization = "Unspecified",
            rightsStatement = "",
            timeoutSeconds = 5,
            maxResponseBytes = 4096
        });
        AudiovisualProviderManifest unlicensedManifest = await loader.LoadAsync(missingRights);
        AudiovisualProviderDefinition unlicensedProvider = Assert.Single(unlicensedManifest.Providers);
        Assert.Equal(ProviderAuthorization.Unspecified, unlicensedProvider.Authorization);
        Assert.Equal(string.Empty, unlicensedProvider.RightsStatement);
    }

    [Fact]
    public async Task ManifestLoader_RejectsPrivateRemoteLocationsAndRemotePrivateNetworkGrants()
    {
        var handler = new StubHttpHandler((_, _) =>
            Task.FromResult(JsonResponse(ManifestJson(new
            {
                id = "remote.private-grant",
                name = "Remote private grant",
                baseUrl = "http://127.0.0.1/",
                searchUrlTemplate = "http://127.0.0.1/search?q={query}",
                mediaKinds = new[] { "Movie" },
                authorization = "UserAuthorized",
                rightsStatement = "User-authorized local provider.",
                allowPrivateNetwork = true,
                timeoutSeconds = 5,
                maxResponseBytes = 4096
            }))));
        using var httpClient = new HttpClient(handler);
        var loader = new AudiovisualProviderManifestLoader(httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            loader.LoadAsync("http://127.0.0.1/provider-index.json"));
        Assert.Equal(0, handler.RequestCount);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            loader.LoadAsync("https://93.184.216.34/provider-index.json"));
        Assert.Equal(1, handler.RequestCount);

        string localManifest = ManifestJson(new
        {
            id = "local.private-provider",
            name = "Local private provider",
            baseUrl = "http://127.0.0.1/",
            searchUrlTemplate = "http://127.0.0.1/search?q={query}",
            mediaKinds = new[] { "Movie" },
            authorization = "UserAuthorized",
            rightsStatement = "Explicitly configured local provider.",
            allowPrivateNetwork = true,
            timeoutSeconds = 5,
            maxResponseBytes = 4096
        });
        AudiovisualProviderManifest local = await loader.LoadAsync(localManifest);
        Assert.True(Assert.Single(local.Providers).AllowPrivateNetwork);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void ManifestAccessMode_MissingOrUnknownDefaultsToNonDownloadableWebPage(string? value)
    {
        Assert.Equal(
            AudiovisualSourceAccessMode.WebPage,
            ManifestAudiovisualSourceProvider.ParseAccessMode(value));
    }

    [Fact]
    public async Task ManifestProvider_ReturnsOnlyExactIdentityAndUnit()
    {
        var provider = CreateProvider(
            mediaKinds: [AudiovisualMediaKind.Television],
            languages: ["en"],
            allowedHosts: ["media.example"]);
        var manifest = new AudiovisualProviderManifest { Providers = [provider] };
        string payload = JsonSerializer.Serialize(new
        {
            items = new object[]
            {
                ProviderItem("Example Show", 2024, 1, 3, "https://media.example/wrong-episode.mp4"),
                ProviderItem("Example Show", 2023, 1, 2, "https://media.example/wrong-year.mp4"),
                ProviderItem("Example Show", 2024, 1, 2, "https://media.example/exact.mp4")
            }
        });
        var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("provider.example", request.RequestUri?.Host);
            return Task.FromResult(JsonResponse(payload));
        });
        using var httpClient = new HttpClient(handler);
        var requests = new ProviderRequestCoordinator(httpClient);
        var sourceProvider = new ManifestAudiovisualSourceProvider(manifest, requests);

        IReadOnlyList<AudiovisualSource> sources = await sourceProvider.FindSourcesAsync(
            new AudiovisualIdentity
            {
                Kind = AudiovisualMediaKind.Television,
                Title = "Example Show",
                Year = 2024
            },
            new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 2 },
            "en");

        AudiovisualSource source = Assert.Single(sources);
        Assert.Equal("https://media.example/exact.mp4", source.Location.AbsoluteUri);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ProviderCoordinator_CachesSuccessAndBacksOffRateLimitedProvider()
    {
        AudiovisualProviderDefinition provider = CreateProvider(
            mediaKinds: [AudiovisualMediaKind.Movie],
            languages: []);
        int successCalls = 0;
        var successHandler = new StubHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref successCalls);
            return Task.FromResult(JsonResponse("""{"items":[]}"""));
        });
        using var successClient = new HttpClient(successHandler);
        var successful = new ProviderRequestCoordinator(successClient);
        var uri = new Uri("https://provider.example/search?q=Example");

        string? first = await successful.GetStringAsync(provider, uri);
        string? second = await successful.GetStringAsync(provider, uri);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, successCalls);
        ProviderHealthSnapshot healthy = Assert.Single(successful.GetHealthSnapshots());
        Assert.Equal(ProviderHealthState.Healthy, healthy.State);
        Assert.Equal(1, healthy.CachedResponseCount);

        var limitedHandler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        });
        using var limitedClient = new HttpClient(limitedHandler);
        var limited = new ProviderRequestCoordinator(limitedClient);

        Assert.Null(await limited.GetStringAsync(provider, uri));
        Assert.Null(await limited.GetStringAsync(provider, uri));
        Assert.Equal(1, limitedHandler.RequestCount);
        Assert.Equal(
            ProviderHealthState.BackingOff,
            Assert.Single(limited.GetHealthSnapshots()).State);
    }

    [Fact]
    public async Task TmdbClient_UsesConfiguredMetadataAndFiltersCartoonsByAnimationGenre()
    {
        string response = JsonSerializer.Serialize(new
        {
            results = new object[]
            {
                new
                {
                    id = 11,
                    media_type = "movie",
                    title = "Animated Example",
                    original_title = "Animated Example",
                    release_date = "2025-02-03",
                    genre_ids = new[] { 16, 10751 },
                    vote_average = 8.1,
                    original_language = "en",
                    poster_path = "/poster.jpg",
                    backdrop_path = "/backdrop.jpg",
                    overview = "Animated."
                },
                new
                {
                    id = 12,
                    media_type = "movie",
                    title = "Live Action Example",
                    release_date = "2025-02-03",
                    genre_ids = new[] { 28 },
                    vote_average = 7.0,
                    original_language = "en"
                },
                new
                {
                    id = 13,
                    media_type = "person",
                    name = "Not Media",
                    genre_ids = new[] { 16 }
                }
            }
        });
        var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("/3/search/multi", request.RequestUri?.AbsolutePath);
            Assert.Contains(
                "api_key=test-key",
                request.RequestUri?.Query ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Contains(
                "language=en-US",
                request.RequestUri?.Query ?? string.Empty,
                StringComparison.Ordinal);
            return Task.FromResult(JsonResponse(response));
        });
        using var httpClient = new HttpClient(handler);
        var options = new AudiovisualOptions
        {
            TmdbApiKey = "test-key",
            TmdbBaseUri = new Uri("https://tmdb.example/3/")
        };
        var requests = new ProviderRequestCoordinator(httpClient);
        var client = new TmdbMetadataClient(options, requests);

        IReadOnlyList<AudiovisualMediaItem> results = await client.SearchAsync(
            AudiovisualMediaKind.Cartoon,
            "example");

        AudiovisualMediaItem item = Assert.Single(results);
        Assert.Equal(AudiovisualMediaKind.Cartoon, item.Identity.Kind);
        Assert.Equal(11, item.Identity.TmdbId);
        Assert.Equal(2025, item.Identity.Year);
        Assert.Contains("Animation", item.Genres);
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", item.PosterUrl);
    }

    [Fact]
    public async Task TmdbClient_DoesNotReplaceAValidEmptyCartoonResultWithAnotherCatalog()
    {
        string response = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new
                {
                    id = 22,
                    media_type = "movie",
                    title = "Live Action Only",
                    release_date = "2025-02-03",
                    genre_ids = new[] { 28 }
                }
            }
        });
        var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("tmdb.example", request.RequestUri?.Host);
            return Task.FromResult(JsonResponse(response));
        });
        using var httpClient = new HttpClient(handler);
        var client = new TmdbMetadataClient(
            new AudiovisualOptions
            {
                TmdbApiKey = "test-key",
                TmdbBaseUri = new Uri("https://tmdb.example/3/"),
                EnableInternetArchive = true,
                InternetArchiveBaseUri = new Uri("https://archive.example/")
            },
            new ProviderRequestCoordinator(httpClient));

        IReadOnlyList<AudiovisualMediaItem> results = await client.SearchAsync(
            AudiovisualMediaKind.Cartoon,
            "live action");

        Assert.Empty(results);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task InternetArchive_AcceptsExactVideoAndPreservesRightsMetadata()
    {
        var handler = new StubHttpHandler((request, _) =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/advancedsearch.php")
            {
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    response = new
                    {
                        docs = new object[]
                        {
                            new
                            {
                                identifier = "exact-item",
                                title = "Public Example",
                                year = 1940,
                                subject = new[] { "Animation", "Cartoons" },
                                language = "Arabic"
                            },
                            new
                            {
                                identifier = "wrong-year",
                                title = "Public Example",
                                year = 1941,
                                subject = "Animation",
                                language = "Arabic"
                            }
                        }
                    }
                })));
            }

            Assert.Equal("/metadata/exact-item", path);
            return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
            {
                metadata = new
                {
                    title = "Public Example",
                    year = 1940,
                    subject = new[] { "Animation" },
                    language = new[] { "ara" },
                    licenseurl = "https://creativecommons.org/publicdomain/mark/1.0/",
                    rights = "Public Domain"
                },
                files = new object[]
                {
                    new { name = "thumbnail.jpg", format = "JPEG", size = "100" },
                    new { name = "public-example.mp4", format = "MPEG4", source = "original", size = "10000" }
                }
            })));
        });
        using var httpClient = new HttpClient(handler);
        var options = new AudiovisualOptions
        {
            EnableInternetArchive = true,
            InternetArchiveBaseUri = new Uri("https://archive.example/")
        };
        var requests = new ProviderRequestCoordinator(httpClient);
        var provider = new InternetArchiveSourceProvider(options, requests);

        IReadOnlyList<AudiovisualSource> sources = await provider.FindSourcesAsync(
            new AudiovisualIdentity
            {
                Kind = AudiovisualMediaKind.Cartoon,
                Title = "Public Example",
                Year = 1940
            },
            preferredLanguage: "ar");

        AudiovisualSource source = Assert.Single(sources);
        Assert.Equal(ProviderAuthorization.Unspecified, source.Authorization);
        Assert.Equal("Public Domain", source.Rights);
        Assert.Equal("https://creativecommons.org/publicdomain/mark/1.0/", source.License);
        Assert.Equal("arabic-cartoon", source.Lane);
        Assert.Equal(
            "https://archive.example/download/exact-item/public-example.mp4",
            source.Location.AbsoluteUri);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task InternetArchive_AcceptsExactItemWhenRightsMetadataIsMissing()
    {
        var handler = new StubHttpHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/advancedsearch.php")
            {
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    response = new
                    {
                        docs = new[]
                        {
                            new { identifier = "unknown-rights", title = "Example Film", year = 1950 }
                        }
                    }
                })));
            }

            return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
            {
                metadata = new { title = "Example Film", year = 1950 },
                files = new[] { new { name = "film.mp4", format = "MPEG4" } }
            })));
        });
        using var httpClient = new HttpClient(handler);
        var options = new AudiovisualOptions
        {
            EnableInternetArchive = true,
            InternetArchiveBaseUri = new Uri("https://archive.example/")
        };
        var provider = new InternetArchiveSourceProvider(
            options,
            new ProviderRequestCoordinator(httpClient));

        IReadOnlyList<AudiovisualSource> sources = await provider.FindSourcesAsync(
            new AudiovisualIdentity
            {
                Kind = AudiovisualMediaKind.Movie,
                Title = "Example Film",
                Year = 1950
            });

        AudiovisualSource source = Assert.Single(sources);
        Assert.Equal(ProviderAuthorization.Unspecified, source.Authorization);
        Assert.Equal(string.Empty, source.License);
        Assert.Equal(string.Empty, source.Rights);
        Assert.Null(source.Evidence); // Archive's broad movies category alone cannot prove a feature film.
    }

    [Fact]
    public async Task InternetArchive_ExplicitFeatureFilmItemProvidesIndependentMatchEvidence()
    {
        var handler = new StubHttpHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri?.AbsolutePath == "/advancedsearch.php"
                ? "{\"response\":{\"docs\":[{\"identifier\":\"feature-item\",\"title\":\"Example Film\",\"year\":1950}]}}"
                : "{\"metadata\":{\"title\":\"Example Film\",\"year\":1950,\"subject\":[\"Feature films\"]}," +
                  "\"files\":[{\"name\":\"film.mp4\",\"format\":\"MPEG4\"}]}")));
        using var httpClient = new HttpClient(handler);
        var provider = new InternetArchiveSourceProvider(new AudiovisualOptions
        {
            EnableInternetArchive = true,
            InternetArchiveBaseUri = new Uri("https://archive.example/")
        }, new ProviderRequestCoordinator(httpClient));
        var identity = new AudiovisualIdentity
        {
            Kind = AudiovisualMediaKind.Movie,
            ContentForm = AudiovisualContentForm.Feature,
            Title = "Example Film",
            Year = 1950
        };

        AudiovisualSource source = Assert.Single(await provider.FindSourcesAsync(identity));
        Assert.Equal(SourceEvidenceOrigin.ProviderItem, source.Evidence?.Origin);
        Assert.Equal(SourceVerificationStatus.Verified,
            ExactAudiovisualMatcher.VerifyEvidence(new SourceSearchRequest { Identity = identity }, source.Evidence).Status);
        Assert.Empty(source.Evidence!.Audio?.Languages ?? []); // Item language is not audio-track proof.
    }

    [Fact]
    public async Task CartoonService_ArabicLaneRequiresDeclarationLanguageExactUnitAndMediaProbe()
    {
        string manifest = ManifestJson(new
        {
            id = "arabic.cartoon",
            name = "Authorized Arabic Cartoons",
            searchUrlTemplate = "https://provider.example/search?q={query}&season={season}&episode={episode}",
            mediaKinds = new[] { "Cartoon" },
            languages = new[] { "ar" },
            authorization = "UserAuthorized",
            rightsStatement = "User-authorized cartoon catalog.",
            requestsPerMinute = 0,
            cacheSeconds = 60,
            timeoutSeconds = 5,
            maxResponseBytes = 4096,
            arabicCartoonLane = true,
            allowedHosts = new[] { "media.example" }
        });
        using var config = new TempConfig(new Dictionary<string, string>
        {
            [AudiovisualOptions.ProviderIndexesConfigKey] = manifest,
            [AudiovisualOptions.InternetArchiveEnabledConfigKey] = "false",
            [AudiovisualOptions.TmdbApiKeyConfigKey] = string.Empty
        });
        var handler = new StubHttpHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x00])
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                return Task.FromResult(response);
            }

            string payload = JsonSerializer.Serialize(new
            {
                items = new[]
                {
                    new
                    {
                        kind = "Cartoon",
                        title = "Arabic Example",
                        year = 2026,
                        season = 1,
                        episode = 2,
                        url = "https://media.example/arabic-example-s01e02.mp4",
                        accessMode = "DirectMedia",
                        languages = new[] { "ara" },
                        lane = "arabic-cartoon"
                    }
                }
            });
            return Task.FromResult(JsonResponse(payload));
        });
        using var httpClient = new HttpClient(handler);
        var service = new CartoonService(config.Value, httpClient);

        IReadOnlyList<AudiovisualSource> sources = await service.FindArabicSourcesAsync(
            new AudiovisualIdentity
            {
                Kind = AudiovisualMediaKind.Cartoon,
                Title = "Arabic Example",
                Year = 2026
            },
            new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 2 });

        AudiovisualSource source = Assert.Single(sources);
        Assert.True(source.IsArabicCartoonVerified);
        Assert.NotEqual(default, source.VerifiedAtUtc);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CatalogServices_UseInternetArchiveFallbackWithoutTmdbKey()
    {
        using var config = new TempConfig(new Dictionary<string, string>
        {
            [AudiovisualOptions.TmdbApiKeyConfigKey] = string.Empty,
            [AudiovisualOptions.InternetArchiveEnabledConfigKey] = "true",
            [AudiovisualOptions.InternetArchiveUrlConfigKey] = "https://archive.example/"
        });
        var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("/advancedsearch.php", request.RequestUri?.AbsolutePath);
            return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
            {
                response = new
                {
                    docs = new object[]
                    {
                        new
                        {
                            identifier = "licensed-cartoon",
                            title = "Fallback Cartoon",
                            year = 1942,
                            subject = new[] { "Animation", "Family" },
                            language = "en",
                            licenseurl = "https://creativecommons.org/licenses/by/4.0/",
                            rights = "Creative Commons Attribution",
                            description = "A licensed animated short."
                        },
                        new
                        {
                            identifier = "unknown-rights",
                            title = "Fallback Cartoon",
                            year = 1942,
                            subject = new[] { "Animation" }
                        },
                        new
                        {
                            identifier = "licensed-live-action",
                            title = "Fallback Cartoon",
                            year = 1942,
                            subject = new[] { "Drama" },
                            licenseurl = "https://creativecommons.org/licenses/by/4.0/",
                            rights = "Creative Commons Attribution"
                        }
                    }
                }
            })));
        });
        using var httpClient = new HttpClient(handler);
        var service = new CartoonService(config.Value, httpClient);

        IReadOnlyList<AudiovisualMediaItem> results = await service.SearchAsync("Fallback Cartoon");

        AudiovisualMediaItem item = Assert.Single(results);
        Assert.Equal(AudiovisualMediaKind.Cartoon, item.Identity.Kind);
        Assert.Equal(1942, item.Identity.Year);
        Assert.Equal("Fallback Cartoon", item.Title);
        Assert.Equal(
            "https://archive.example/services/img/licensed-cartoon",
            item.PosterUrl);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task IndependentCatalogServices_DoNotCrossMediaKindsWithoutTmdb()
    {
        using var config = new TempConfig(new Dictionary<string, string>
        {
            [AudiovisualOptions.TmdbApiKeyConfigKey] = string.Empty,
            [AudiovisualOptions.InternetArchiveEnabledConfigKey] = "false"
        });
        var handler = new StubHttpHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "www.wikidata.org")
                return Task.FromResult(JsonResponse("""{"query":{"search":[]}}"""));
            Assert.Equal("api.tvmaze.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var client = new HttpClient(handler);
        var movie = new MovieService(config.Value, client);
        var television = new TvService(config.Value, client);
        var cartoon = new CartoonService(config.Value, client);

        Assert.Equal(AudiovisualMediaKind.Movie, movie.Kind);
        Assert.Equal(AudiovisualMediaKind.Television, television.Kind);
        Assert.Equal(AudiovisualMediaKind.Cartoon, cartoon.Kind);
        Assert.Empty(await movie.SearchAsync("Example"));
        Assert.Empty(await television.GetPopularAsync());
        Assert.Empty(await cartoon.SearchAsync("Example"));
        Assert.True(television.Capabilities.HasFlag(AudiovisualCatalogCapabilities.Episodes));
        Assert.True(movie.Capabilities.HasFlag(AudiovisualCatalogCapabilities.Search));
        Assert.False(movie.Capabilities.HasFlag(AudiovisualCatalogCapabilities.Episodes));

        IReadOnlyList<AudiovisualSource> crossKind = await movie.FindSourcesAsync(
            new AudiovisualIdentity
            {
                Kind = AudiovisualMediaKind.Television,
                Title = "Wrong lane"
            });
        Assert.Empty(crossKind);
        Assert.Equal(2, handler.RequestCount);
    }

    private static object ProviderItem(
        string title,
        int year,
        int season,
        int episode,
        string url)
    {
        return new
        {
            kind = "Television",
            title,
            year,
            season,
            episode,
            url,
            accessMode = "DirectMedia",
            languages = new[] { "en" },
            license = "User-authorized"
        };
    }

    private static AudiovisualProviderDefinition CreateProvider(
        IReadOnlyList<AudiovisualMediaKind> mediaKinds,
        IReadOnlyList<string> languages,
        IReadOnlyList<string>? allowedHosts = null)
    {
        return new AudiovisualProviderDefinition
        {
            Id = "provider.example",
            Name = "Provider Example",
            BaseUrl = "https://provider.example/",
            SearchUrlTemplate = "https://provider.example/search?q={query}",
            MediaKinds = mediaKinds,
            Languages = languages,
            Authorization = ProviderAuthorization.UserAuthorized,
            RightsStatement = "User-authorized test provider.",
            RequestsPerMinute = 0,
            CacheSeconds = 60,
            TimeoutSeconds = 5,
            MaxResponseBytes = 64 * 1024,
            AllowedHosts = allowedHosts ?? Array.Empty<string>()
        };
    }

    private static string ManifestJson(object provider)
    {
        return JsonSerializer.Serialize(new
        {
            version = 1,
            providers = new[] { provider }
        });
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        private int _requestCount;

        public StubHttpHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return _handler(request, cancellationToken);
        }
    }

    private sealed class TempConfig : IDisposable
    {
        public TempConfig(IReadOnlyDictionary<string, string> settings)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "UniversalMediaOS.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            string configPath = Path.Combine(Root, "config.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(settings));
            Value = new DomainHotSwapper(configPath);
        }

        private string Root { get; }

        public DomainHotSwapper Value { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
