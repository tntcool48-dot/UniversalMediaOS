using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class DubAvailabilityServiceTests
    {
        [Fact]
        public async Task NoProvidersConfigured_ReturnsUnknownWithoutNetworkCalls()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = "[]"
            });
            using var httpClient = new HttpClient(new StubHttpHandler((_, _) =>
                throw new InvalidOperationException("No network call should be made.")));
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                AvailableSubEpisodes = 12
            });

            Assert.False(result.Checked);
            Assert.Equal(DubAvailabilityService.UnknownDetail, result.Detail);
        }

        [Fact]
        public async Task DeadProviderTimeout_ReturnsUnknownAndCachesTemporaryOfflineStatus()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("DeadDub", "https://dead.example/search?q={query}", timeoutSeconds: 1)
            });
            var handler = new StubHttpHandler(async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var first = await service.CheckAsync(new MediaResult { OfficialTitle = "First Anime" });
            var stopwatch = Stopwatch.StartNew();
            var second = await service.CheckAsync(new MediaResult { OfficialTitle = "Second Anime" });
            stopwatch.Stop();

            Assert.False(first.Checked);
            Assert.False(second.Checked);
            Assert.Equal(DubAvailabilityService.UnknownDetail, first.Detail);
            Assert.Equal(DubAvailabilityService.UnknownDetail, second.Detail);
            Assert.Equal(1, handler.RequestCount);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "Second lookup should skip the temporarily offline provider.");
        }

        [Fact]
        public async Task MockProviderBadges_ReturnConfiguredProviderNameAndDubCounts()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("MockDub", "https://mock.example/suggest?keyword={query}")
            });

            var html = """
                <div class="film-detail">
                  <a title="Mock Anime">Mock Anime</a>
                  <span class="tick-item tick-sub">12</span>
                  <span class="tick-item tick-dub">4</span>
                </div>
                """;
            var handler = new StubHttpHandler((_, _) =>
            {
                string payload = JsonSerializer.Serialize(new { html });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                AvailableSubEpisodes = 1
            });

            Assert.True(result.Checked);
            Assert.Equal(12, result.SubEpisodes);
            Assert.Equal(4, result.DubEpisodes);
            Assert.Equal("MockDub", result.Source);
        }

        [Fact]
        public async Task ProviderRootWithoutQueryPlaceholder_ProbesCommonSuggestUrls()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("AutoProbeDub", "https://mock.example")
            });

            var handler = new StubHttpHandler((request, _) =>
            {
                string pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
                if (pathAndQuery.Equals("/ajax/search/suggest?keyword=Mock%20Anime", StringComparison.OrdinalIgnoreCase))
                {
                    string payload = JsonSerializer.Serialize(new
                    {
                        html = """
                            <div>
                              <a title="Mock Anime">Mock Anime</a>
                              <span class="tick-item tick-sub">12</span>
                              <span class="tick-item tick-dub">7</span>
                            </div>
                            """
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                AvailableSubEpisodes = 1
            });

            Assert.True(result.Checked);
            Assert.Equal("AutoProbeDub", result.Source);
            Assert.Equal(7, result.DubEpisodes);
        }

        [Fact]
        public async Task AutoProbeProvider_BoundsSpeculativeRequestsAcrossTitleAliases()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("BoundedDub", "https://mock.example")
            });
            var handler = new StubHttpHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Primary Title",
                EnglishTitle = "English Title",
                RomajiTitle = "Romaji Title",
                NativeTitle = "Native Title",
                Synonyms = ["Alias One", "Alias Two", "Alias Three", "Alias Four"]
            });

            Assert.False(result.Checked);
            Assert.Equal(24, handler.RequestCount);
        }

        [Fact]
        public async Task RateLimitedProvider_StopsCurrentProbeAndBacksOffSubsequentLookups()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("LimitedDub", "https://mock.example")
            });
            var handler = new StubHttpHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                return Task.FromResult(response);
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var first = await service.CheckAsync(new MediaResult { OfficialTitle = "First Anime" });
            var second = await service.CheckAsync(new MediaResult { OfficialTitle = "Second Anime" });

            Assert.False(first.Checked);
            Assert.False(second.Checked);
            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public async Task ProviderConfigSettingChange_ReloadsProvidersAndClearsUnknownState()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = "[]"
            });

            var handler = new StubHttpHandler((request, _) =>
            {
                string pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
                if (pathAndQuery.Equals("/search?keyword=Mock%20Anime", StringComparison.OrdinalIgnoreCase))
                {
                    string payload = JsonSerializer.Serialize(new
                    {
                        html = """
                            <div>
                              <a title="Mock Anime">Mock Anime</a>
                              <span class="ep-status sub"><span> 12</span></span>
                              <span class="ep-status dub"><span> 6</span></span>
                            </div>
                            """
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);
            var media = new MediaResult
            {
                OfficialTitle = "Mock Anime",
                AvailableSubEpisodes = 1
            };

            var beforeSave = await service.CheckAsync(media);
            config.Value.SetSetting(DubAvailabilityService.ProviderConfigKey, ProviderJson("LiveReloadDub", "https://mock.example/search?keyword="));
            var afterSave = await service.CheckAsync(media);

            Assert.False(beforeSave.Checked);
            Assert.True(afterSave.Checked);
            Assert.Equal("LiveReloadDub", afterSave.Source);
            Assert.Equal(6, afterSave.DubEpisodes);
        }

        [Fact]
        public async Task CallerCancellation_DoesNotMarkProviderOffline()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("CancelSafeDub", "https://mock.example/search?keyword={query}", timeoutSeconds: 30)
            });

            int requestCount = 0;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new StubHttpHandler(async (_, token) =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    started.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }

                string payload = JsonSerializer.Serialize(new
                {
                    html = """
                        <div>
                          <a title="Mock Anime">Mock Anime</a>
                          <span class="ep-status sub"><span> 12</span></span>
                          <span class="ep-status dub"><span> 5</span></span>
                        </div>
                        """
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);
            using var cts = new CancellationTokenSource();

            Task cancelledCheck = service.CheckAsync(new MediaResult { OfficialTitle = "Mock Anime" }, cts.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCheck);

            var result = await service.CheckAsync(new MediaResult { OfficialTitle = "Mock Anime" });

            Assert.True(result.Checked);
            Assert.Equal(5, result.DubEpisodes);
            Assert.Equal(2, requestCount);
        }

        [Fact]
        public async Task UnknownProviderResult_IsCachedBrieflyAndBypassRefreshesIt()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("RetryDub", "https://mock.example/search?keyword={query}")
            });

            int requestCount = 0;
            var handler = new StubHttpHandler((_, _) =>
            {
                requestCount++;
                string html = requestCount == 1
                    ? "<div>No matching badge data yet</div>"
                    : """
                        <div>
                          <a title="Mock Anime">Mock Anime</a>
                          <span class="ep-status sub"><span> 12</span></span>
                          <span class="ep-status dub"><span> 8</span></span>
                        </div>
                        """;

                string payload = JsonSerializer.Serialize(new { html });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var first = await service.CheckAsync(new MediaResult { OfficialTitle = "Mock Anime" });
            var second = await service.CheckAsync(new MediaResult { OfficialTitle = "Mock Anime" });
            var refreshed = await service.CheckAsync(
                new MediaResult { OfficialTitle = "Mock Anime" },
                bypassCache: true);

            Assert.False(first.Checked);
            Assert.False(second.Checked);
            Assert.True(refreshed.Checked);
            Assert.Equal(8, refreshed.DubEpisodes);
            Assert.Equal(2, requestCount);
        }

        [Fact]
        public async Task ClassBasedSubDubBadges_UpdateDubCounts()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("ClassBadgeDub", "https://mock.example/search?keyword={query}")
            });

            var handler = new StubHttpHandler((_, _) =>
            {
                string payload = JsonSerializer.Serialize(new
                {
                    html = """
                        <div class="item">
                          <a class="name d-title">Dr. Stone: Science Future Part 3</a>
                          <span class="ep-status sub"><span> 13</span></span>
                          <span class="ep-status dub"><span> 13</span></span>
                        </div>
                        """
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Dr. Stone: Science Future Part 3",
                AvailableSubEpisodes = 1
            });

            Assert.True(result.Checked);
            Assert.Equal(13, result.SubEpisodes);
            Assert.Equal(13, result.DubEpisodes);
        }

        [Fact]
        public async Task SearchPageBadges_AreScopedToMatchingResultCard()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("ScopedDub", "https://mock.example/search?keyword={query}")
            });

            var handler = new StubHttpHandler((_, _) =>
            {
                string payload = JsonSerializer.Serialize(new
                {
                    html = """
                        <div class="item">
                          <a class="name d-title">Mock Anime</a>
                          <span class="ep-status sub"><span> 13</span></span>
                          <span class="ep-status dub"><span> 13</span></span>
                        </div>
                        <div class="item">
                          <a class="name d-title">Other Anime</a>
                          <span class="ep-status sub"><span> 103</span></span>
                          <span class="ep-status dub"><span> 103</span></span>
                        </div>
                        """
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                AvailableSubEpisodes = 1
            });

            Assert.True(result.Checked);
            Assert.Equal(13, result.SubEpisodes);
            Assert.Equal(13, result.DubEpisodes);
        }

        [Fact]
        public async Task ExactTitleCard_IsPreferredOverEarlierRelatedSeason()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("ExactDub", "https://mock.example/filter?keyword={query}")
            });

            var handler = new StubHttpHandler((_, _) =>
            {
                string payload = JsonSerializer.Serialize(new
                {
                    html = """
                        <div class="item">
                          <a class="name d-title" data-jp="Sousou no Frieren 2nd Season">Frieren: Beyond Journey's End Season 2</a>
                          <span class="ep-status sub"><span> 10</span></span>
                          <span class="ep-status dub"><span> 10</span></span>
                        </div>
                        <div class="item">
                          <a class="name d-title" data-jp="Sousou no Frieren">Frieren: Beyond Journey's End</a>
                          <span class="ep-status sub"><span> 28</span></span>
                          <span class="ep-status dub"><span> 28</span></span>
                        </div>
                        """
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Frieren: Beyond Journey's End",
                EnglishTitle = "Frieren: Beyond Journey's End",
                RomajiTitle = "Sousou no Frieren",
                AvailableSubEpisodes = 28,
                TotalEpisodes = 28
            });

            Assert.True(result.Checked);
            Assert.Equal(28, result.DubEpisodes);
        }

        [Fact]
        public async Task AlternateRomajiTitle_IsUsedForProviderLookupAndMatch()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("RomajiDub", "https://mock.example/filter?keyword={query}")
            });

            var handler = new StubHttpHandler((request, _) =>
            {
                string query = request.RequestUri?.Query ?? string.Empty;
                string html = query.Contains("Sousou%20no%20Frieren", StringComparison.OrdinalIgnoreCase)
                    ? """
                        <div class="item">
                          <a class="name d-title" data-jp="Sousou no Frieren">Sousou no Frieren</a>
                          <span class="ep-status sub"><span> 28</span></span>
                          <span class="ep-status dub"><span> 28</span></span>
                        </div>
                        """
                    : """
                        <div class="item">
                          <a class="name d-title">Unrelated Anime</a>
                          <span class="ep-status sub"><span> 12</span></span>
                          <span class="ep-status dub"><span> 12</span></span>
                        </div>
                        """;

                string payload = JsonSerializer.Serialize(new { html });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Frieren: Beyond Journey's End",
                EnglishTitle = "Frieren: Beyond Journey's End",
                RomajiTitle = "Sousou no Frieren",
                AvailableSubEpisodes = 28,
                TotalEpisodes = 28
            });

            Assert.True(result.Checked);
            Assert.Equal(28, result.DubEpisodes);
            Assert.True(handler.RequestCount >= 2);
        }

        [Fact]
        public async Task DubCountAboveKnownEpisodeCount_IsRejectedAsUnsafe()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("UnsafeDub", "https://mock.example/filter?keyword={query}")
            });

            var handler = new StubHttpHandler((_, _) =>
            {
                string payload = JsonSerializer.Serialize(new
                {
                    html = """
                        <div class="item">
                          <a class="name d-title">Mock Anime</a>
                          <span class="ep-status sub"><span> 103</span></span>
                          <span class="ep-status dub"><span> 103</span></span>
                        </div>
                        """
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                AvailableSubEpisodes = 13,
                TotalEpisodes = 13
            });

            Assert.False(result.Checked);
            Assert.Equal(0, result.DubEpisodes);
        }

        [Fact]
        public async Task HalfFilledSearchUrlWithoutPlaceholder_AppendsQueryBeforeFallbacks()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("HalfUrlDub", "https://mock.example/search?keyword=")
            });

            var handler = new StubHttpHandler((request, _) =>
            {
                string pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
                if (pathAndQuery.Equals("/search?keyword=Mock%20Anime", StringComparison.OrdinalIgnoreCase))
                {
                    string payload = JsonSerializer.Serialize(new
                    {
                        html = """
                            <div>
                              <a title="Mock Anime">Mock Anime</a>
                              <span class="tick-item tick-sub">12</span>
                              <span class="tick-item tick-dub">3</span>
                            </div>
                            """
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                AvailableSubEpisodes = 1
            });

            Assert.True(result.Checked);
            Assert.Equal("HalfUrlDub", result.Source);
            Assert.Equal(3, result.DubEpisodes);
        }

        [Fact]
        public async Task AniKotoVerified_UsesExactCardAndDistinctEpisodeMetadata()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            int searchRequests = 0;
            int episodeRequests = 0;
            var handler = new StubHttpHandler((request, _) =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Equals("/filter", StringComparison.OrdinalIgnoreCase))
                {
                    searchRequests++;
                    Assert.Equal("?keyword=Frieren%3A%20Beyond%20Journey%27s%20End", request.RequestUri?.Query);
                    return Task.FromResult(HtmlResponse("""
                        <div class="item"><div class="inner">
                          <div class="ani poster tip" data-tip="6351"></div>
                          <a class="name d-title" data-jp="Sousou no Frieren">Frieren: Beyond Journey's End</a>
                          <span class="ep-status sub"><span>4</span></span>
                          <span class="ep-status dub"><span>4</span></span>
                        </div></div>
                        <div class="item"><div class="ani poster tip" data-tip="9999"></div>
                          <a class="name d-title">Frieren: Beyond Journey's End Season 2</a>
                          <span class="ep-status dub"><span>99</span></span>
                        </div>
                        """));
                }

                Assert.Equal("/ajax/episode/list/6351", path);
                Assert.True(request.Headers.TryGetValues("X-Requested-With", out var values));
                Assert.Contains("XMLHttpRequest", values);
                Assert.Equal("/filter", request.Headers.Referrer?.AbsolutePath);
                episodeRequests++;
                string result = """
                    <a data-num="1" data-mal="52991" data-sub="1" data-dub="1"></a>
                    <a data-num="2" data-mal="52991" data-sub="1" data-dub="1"></a>
                    <a data-num="2" data-mal="52991" data-sub="1" data-dub="1"></a>
                    <a data-num="3.5" data-mal="52991" data-sub="1" data-dub="1"></a>
                    <a data-num="4" data-mal="52991" data-sub="1" data-dub="1"></a>
                    """;
                return Task.FromResult(JsonResponse(new { status = 200, result }));
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            DubAvailabilityResult result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Frieren: Beyond Journey's End",
                RomajiTitle = "Sousou no Frieren",
                IdMal = 52991,
                TotalEpisodes = 4,
                AvailableSubEpisodes = 4
            });

            Assert.True(result.Checked);
            Assert.True(result.Verified);
            Assert.Equal(DubAvailabilityConfidence.Verified, result.Confidence);
            Assert.Equal(4, result.DubEpisodes);
            Assert.Equal(2, result.HighestContiguousDubEpisode);
            Assert.Equal(new decimal[] { 1, 2, 3.5m, 4 }, result.DubbedEpisodeNumbers);
            Assert.Equal(1, searchRequests);
            Assert.Equal(1, episodeRequests);
        }

        [Fact]
        public async Task AniKotoVerified_MalMismatchIsRejected()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            var handler = new StubHttpHandler((request, _) =>
            {
                if (request.RequestUri?.AbsolutePath == "/filter")
                {
                    return Task.FromResult(HtmlResponse(AniKotoCard("Mock Anime", "77", 12, 8)));
                }

                return Task.FromResult(JsonResponse(new
                {
                    status = 200,
                    result = "<a data-num=\"1\" data-mal=\"999\" data-sub=\"1\" data-dub=\"1\"></a>"
                }));
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            DubAvailabilityResult result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                IdMal = 123,
                TotalEpisodes = 12
            });

            Assert.False(result.Checked);
            Assert.Equal(DubAvailabilityConfidence.Unknown, result.Confidence);
            Assert.Contains("MAL", result.Detail, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AniKotoVerified_MissingMalIdentityFallsBackToSummary()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            var handler = new StubHttpHandler((request, _) =>
                Task.FromResult(request.RequestUri?.AbsolutePath == "/filter"
                    ? HtmlResponse(AniKotoCard("Mock Anime", "77", 12, 8))
                    : JsonResponse(new
                    {
                        status = 200,
                        result = "<a data-num=\"1\" data-sub=\"1\" data-dub=\"1\"></a>"
                    })));
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            DubAvailabilityResult result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                IdMal = 123,
                TotalEpisodes = 12
            });

            Assert.True(result.Checked);
            Assert.False(result.Verified);
            Assert.Equal(DubAvailabilityConfidence.Summary, result.Confidence);
            Assert.Equal(8, result.DubEpisodes);
        }

        [Fact]
        public async Task AniKotoVerified_PartialMalIdentityFallsBackToSummary()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            var handler = new StubHttpHandler((request, _) =>
                Task.FromResult(request.RequestUri?.AbsolutePath == "/filter"
                    ? HtmlResponse(AniKotoCard("Mock Anime", "77", 12, 8))
                    : JsonResponse(new
                    {
                        status = 200,
                        result = "<a data-num=\"1\" data-mal=\"123\" data-dub=\"1\"></a>" +
                                 "<a data-num=\"2\" data-dub=\"1\"></a>"
                    })));
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            DubAvailabilityResult result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                IdMal = 123,
                TotalEpisodes = 12
            });

            Assert.True(result.Checked);
            Assert.False(result.Verified);
            Assert.Equal(DubAvailabilityConfidence.Summary, result.Confidence);
            Assert.Equal(8, result.DubEpisodes);
        }

        [Fact]
        public async Task AniKotoVerified_MissingOrUnrecognizedDubFlagsFallsBackToSummary()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            var handler = new StubHttpHandler((request, _) =>
                Task.FromResult(request.RequestUri?.AbsolutePath == "/filter"
                    ? HtmlResponse(AniKotoCard("Mock Anime", "77", 12, 8))
                    : JsonResponse(new
                    {
                        status = 200,
                        result = "<a data-num=\"1\" data-mal=\"123\"></a>" +
                                 "<a data-num=\"2\" data-mal=\"123\" data-dub=\"maybe\"></a>"
                    })));
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            DubAvailabilityResult result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                IdMal = 123,
                TotalEpisodes = 12
            });

            Assert.True(result.Checked);
            Assert.False(result.Verified);
            Assert.Equal(DubAvailabilityConfidence.Summary, result.Confidence);
            Assert.Equal(8, result.DubEpisodes);
        }

        [Fact]
        public async Task AniKotoVerified_MediaWithoutMalIdFallsBackToSummary()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            var handler = new StubHttpHandler((request, _) =>
                Task.FromResult(request.RequestUri?.AbsolutePath == "/filter"
                    ? HtmlResponse(AniKotoCard("Mock Anime", "77", 12, 8))
                    : JsonResponse(new
                    {
                        status = 200,
                        result = "<a data-num=\"1\" data-mal=\"123\" data-dub=\"1\"></a>"
                    })));
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            DubAvailabilityResult result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                TotalEpisodes = 12
            });

            Assert.True(result.Checked);
            Assert.False(result.Verified);
            Assert.Equal(DubAvailabilityConfidence.Summary, result.Confidence);
            Assert.Equal(8, result.DubEpisodes);
        }

        [Fact]
        public async Task AniKotoSummary_UsesOnlyAggregateCardRequest()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            var handler = new StubHttpHandler((request, _) =>
            {
                Assert.Equal("/filter", request.RequestUri?.AbsolutePath);
                return Task.FromResult(HtmlResponse(AniKotoCard("Mock Anime", "77", 12, 8)));
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            DubAvailabilityResult result = await service.CheckAsync(
                new MediaResult { OfficialTitle = "Mock Anime", TotalEpisodes = 12 },
                mode: DubAvailabilityCheckMode.Summary);

            Assert.True(result.Checked);
            Assert.False(result.Verified);
            Assert.Equal(DubAvailabilityConfidence.Summary, result.Confidence);
            Assert.Equal(8, result.DubEpisodes);
            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public async Task AniKotoSummaryDiscovery_IsReusedByVerifiedLookup()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            var handler = new StubHttpHandler((request, _) =>
                Task.FromResult(request.RequestUri?.AbsolutePath == "/filter"
                    ? HtmlResponse(AniKotoCard("Mock Anime", "77", 2, 1))
                    : JsonResponse(new
                    {
                        status = 200,
                        result = "<a data-num=\"1\" data-mal=\"123\" data-sub=\"1\" data-dub=\"1\"></a>"
                    })));
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);
            var media = new MediaResult { OfficialTitle = "Mock Anime", IdMal = 123, TotalEpisodes = 2 };

            DubAvailabilityResult summary = await service.CheckAsync(media, mode: DubAvailabilityCheckMode.Summary);
            DubAvailabilityResult verified = await service.CheckAsync(media, mode: DubAvailabilityCheckMode.Verified);

            Assert.Equal(DubAvailabilityConfidence.Summary, summary.Confidence);
            Assert.True(verified.Verified);
            Assert.Equal(2, handler.RequestCount);
        }

        [Fact]
        public async Task AniKotoVerified_TriesBoundedAliasAfterMalMismatch()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            var handler = new StubHttpHandler((request, _) =>
            {
                string path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path == "/filter")
                {
                    bool romaji = request.RequestUri?.Query.Contains("Sousou%20no%20Frieren", StringComparison.OrdinalIgnoreCase) == true;
                    return Task.FromResult(HtmlResponse(AniKotoCard(
                        romaji ? "Sousou no Frieren" : "Frieren: Beyond Journey's End",
                        romaji ? "2" : "1",
                        2,
                        2)));
                }

                bool correct = path.EndsWith("/2", StringComparison.Ordinal);
                return Task.FromResult(JsonResponse(new
                {
                    status = 200,
                    result = $"<a data-num=\"1\" data-mal=\"{(correct ? 52991 : 999)}\" data-sub=\"1\" data-dub=\"1\"></a>"
                }));
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            DubAvailabilityResult result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Frieren: Beyond Journey's End",
                RomajiTitle = "Sousou no Frieren",
                IdMal = 52991,
                TotalEpisodes = 2
            });

            Assert.True(result.Verified);
            Assert.Equal(4, handler.RequestCount);
        }

        [Fact]
        public async Task SameMediaConcurrentChecks_ShareOneProviderLookup()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new StubHttpHandler(async (request, token) =>
            {
                if (request.RequestUri?.AbsolutePath == "/filter")
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return HtmlResponse(AniKotoCard("Mock Anime", "77", 2, 2));
                }

                return JsonResponse(new
                {
                    status = 200,
                    result = "<a data-num=\"1\" data-mal=\"123\" data-sub=\"1\" data-dub=\"1\"></a>"
                });
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);
            var media = new MediaResult { Id = 42, OfficialTitle = "Mock Anime", IdMal = 123, TotalEpisodes = 2 };

            Task<DubAvailabilityResult>[] checks = Enumerable.Range(0, 8)
                .Select(_ => service.CheckAsync(media))
                .ToArray();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            release.TrySetResult();
            DubAvailabilityResult[] results = await Task.WhenAll(checks);

            Assert.All(results, result => Assert.True(result.Verified));
            Assert.Equal(2, handler.RequestCount);
        }

        [Fact]
        public async Task UnknownCache_ExpiresAndRetriesProvider()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("RetryDub", "https://mock.example/search?q={query}")
            });
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            int responseNumber = 0;
            var handler = new StubHttpHandler((_, _) =>
            {
                int current = Interlocked.Increment(ref responseNumber);
                string html = current == 1
                    ? "<div>No matching title</div>"
                    : "<div><a title=\"Mock Anime\">Mock Anime</a><span class=\"tick-dub\">3</span></div>";
                return Task.FromResult(HtmlResponse(html));
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient, clock, TimeSpan.Zero);
            var media = new MediaResult { OfficialTitle = "Mock Anime", TotalEpisodes = 12 };

            DubAvailabilityResult first = await service.CheckAsync(media);
            DubAvailabilityResult cached = await service.CheckAsync(media);
            clock.Advance(TimeSpan.FromMinutes(3));
            DubAvailabilityResult refreshed = await service.CheckAsync(media);

            Assert.False(first.Checked);
            Assert.False(cached.Checked);
            Assert.True(refreshed.Checked);
            Assert.Equal(2, handler.RequestCount);
        }

        [Fact]
        public async Task ReleasingVerifiedZero_IsCachedBrieflyThenReverified()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = AniKotoProviderJson()
            });
            var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            var handler = new StubHttpHandler((request, _) =>
                Task.FromResult(request.RequestUri?.AbsolutePath == "/filter"
                    ? HtmlResponse(AniKotoCard("Mock Anime", "77", 2, 0))
                    : JsonResponse(new
                    {
                        status = 200,
                        result = "<a data-num=\"1\" data-mal=\"123\" data-sub=\"1\" data-dub=\"0\"></a>"
                    })));
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient, clock, TimeSpan.Zero);
            var media = new MediaResult
            {
                OfficialTitle = "Mock Anime",
                IdMal = 123,
                TotalEpisodes = 2,
                DisplayStatus = "Releasing"
            };

            DubAvailabilityResult first = await service.CheckAsync(media);
            DubAvailabilityResult cached = await service.CheckAsync(media);
            clock.Advance(TimeSpan.FromMinutes(6));
            DubAvailabilityResult refreshed = await service.CheckAsync(media);

            Assert.True(first.Verified);
            Assert.True(cached.Verified);
            Assert.True(refreshed.Verified);
            Assert.Equal(0, refreshed.DubEpisodes);
            Assert.Equal(3, handler.RequestCount);
        }

        [Fact]
        public async Task MultipleProviders_FirstVerifiedZeroDoesNotHideLaterDub()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = JsonSerializer.Serialize(new[]
                {
                    new DubAvailabilityProviderConfig
                    {
                        Name = "Zero", SuggestUrlTemplate = "https://zero.example/search?q={query}", ParserType = "Badge"
                    },
                    new DubAvailabilityProviderConfig
                    {
                        Name = "Positive", SuggestUrlTemplate = "https://positive.example/search?q={query}", ParserType = "Badge"
                    }
                })
            });
            var handler = new StubHttpHandler((request, _) =>
            {
                int dub = request.RequestUri?.Host == "zero.example" ? 0 : 5;
                return Task.FromResult(HtmlResponse(
                    $"<div><a title=\"Mock Anime\">Mock Anime</a><span class=\"tick-sub\">12</span><span class=\"tick-dub\">{dub}</span></div>"));
            });
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            DubAvailabilityResult result = await service.CheckAsync(new MediaResult
            {
                OfficialTitle = "Mock Anime",
                TotalEpisodes = 12
            });

            Assert.True(result.Checked);
            Assert.Equal(5, result.DubEpisodes);
            Assert.Equal("Positive", result.Source);
            Assert.Equal(2, handler.RequestCount);
        }

        [Fact]
        public async Task OversizedProviderResponse_IsRejectedBeforeParsing()
        {
            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("Bounded", "https://mock.example/search?q={query}")
            });
            var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[2048])
            }));
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(
                config.Value,
                httpClient,
                TimeProvider.System,
                TimeSpan.Zero,
                maxResponseBytes: 1024);

            DubAvailabilityResult result = await service.CheckAsync(new MediaResult { OfficialTitle = "Mock Anime" });

            Assert.False(result.Checked);
            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public async Task InvalidProviderTemplate_IsRejectedWithoutNetworkCalls()
        {
            Assert.True(DubAvailabilityService.IsValidProviderTemplate("https://mock.example/search"));
            Assert.True(DubAvailabilityService.IsValidProviderTemplate("https://mock.example"));
            Assert.False(DubAvailabilityService.IsValidProviderTemplate("file:///tmp/{query}"));

            using var config = new TempConfig(new Dictionary<string, string>
            {
                [DubAvailabilityService.ProviderConfigKey] = ProviderJson("BadProvider", "not-a-url/{query}")
            });
            var handler = new StubHttpHandler((_, _) =>
                throw new InvalidOperationException("Invalid templates should not be requested."));
            using var httpClient = new HttpClient(handler);
            var service = new DubAvailabilityService(config.Value, httpClient);

            var result = await service.CheckAsync(new MediaResult { OfficialTitle = "Mock Anime" });

            Assert.False(result.Checked);
            Assert.Equal(DubAvailabilityService.UnknownDetail, result.Detail);
            Assert.Equal(0, handler.RequestCount);
        }

        [Fact]
        public void SettingsProviderItem_ToConfig_ClampsTimeoutAndPreservesProviderData()
        {
            var item = new DubAvailabilityProviderSettingsItem
            {
                Name = "  MockDub  ",
                SuggestUrlTemplate = "  https://mock.example/suggest?keyword={query}  ",
                Enabled = false,
                TimeoutSeconds = 99,
                ParserType = "",
                AdapterType = "AniKoto"
            };

            var config = item.ToConfig();

            Assert.Equal("MockDub", config.Name);
            Assert.Equal("https://mock.example/suggest?keyword={query}", config.SuggestUrlTemplate);
            Assert.False(config.Enabled);
            Assert.Equal(30, config.TimeoutSeconds);
            Assert.Equal("Badge", config.ParserType);
            Assert.Equal("AniKoto", config.AdapterType);
        }

        private static string ProviderJson(string name, string template, int timeoutSeconds = 8)
        {
            return JsonSerializer.Serialize(new[]
            {
                new DubAvailabilityProviderConfig
                {
                    Name = name,
                    SuggestUrlTemplate = template,
                    Enabled = true,
                    TimeoutSeconds = timeoutSeconds,
                    ParserType = "Badge"
                }
            });
        }

        private static string AniKotoProviderJson()
        {
            return JsonSerializer.Serialize(new[]
            {
                new DubAvailabilityProviderConfig
                {
                    Name = "AniKoto",
                    SuggestUrlTemplate = "https://anikoto.example",
                    Enabled = true,
                    TimeoutSeconds = 8,
                    ParserType = "AniKoto",
                    AdapterType = "AniKoto"
                }
            });
        }

        private static string AniKotoCard(string title, string dataTip, int sub, int dub)
        {
            return $"""
                <div class="item"><div class="inner">
                  <div class="ani poster tip" data-tip="{dataTip}"></div>
                  <a class="name d-title">{title}</a>
                  <span class="ep-status sub"><span>{sub}</span></span>
                  <span class="ep-status dub"><span>{dub}</span></span>
                </div></div>
                """;
        }

        private static HttpResponseMessage HtmlResponse(string html)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
        }

        private static HttpResponseMessage JsonResponse<T>(T value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
            private int _requestCount;

            public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            public int RequestCount => Volatile.Read(ref _requestCount);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _requestCount);
                return _handler(request, cancellationToken);
            }
        }

        private sealed class ManualTimeProvider : TimeProvider
        {
            private DateTimeOffset _utcNow;

            public ManualTimeProvider(DateTimeOffset utcNow)
            {
                _utcNow = utcNow;
            }

            public override DateTimeOffset GetUtcNow() => _utcNow;

            public void Advance(TimeSpan amount)
            {
                _utcNow = _utcNow.Add(amount);
            }
        }

        private sealed class TempConfig : IDisposable
        {
            public TempConfig(Dictionary<string, string> settings)
            {
                Root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
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
}
