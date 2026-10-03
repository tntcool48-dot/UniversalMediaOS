using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class DubAvailabilityUiRegressionTests
    {
        [Fact]
        public void DubBadge_DistinguishesUnknownSummaryVerifiedAndConfirmedNone()
        {
            var media = new MediaResult();

            Assert.Equal("Dub —", media.DubBadgeText);

            media.AvailableDubEpisodes = 12;
            media.DubAvailabilityState = MediaDubAvailabilityState.Summary;
            Assert.Equal("Dub ~12", media.DubBadgeText);

            media.DubAvailabilityState = MediaDubAvailabilityState.Verified;
            Assert.Equal("Dub 12", media.DubBadgeText);

            media.AvailableDubEpisodes = 0;
            Assert.Equal("Dub none", media.DubBadgeText);
        }

        [Fact]
        public void VerifiedDubNavigation_UsesContiguousEpisodeCeilingInsteadOfRawCount()
        {
            var media = new MediaResult
            {
                TotalEpisodes = 14,
                AvailableSubEpisodes = 14,
                AvailableDubEpisodes = 13,
                HighestContiguousDubEpisode = 4,
                DubbedEpisodeNumbers = [1m, 2m, 3m, 4m, 6m, 7m, 8m, 9m, 10m, 11m, 12m, 13m, 14m],
                DubAvailabilityState = MediaDubAvailabilityState.Verified,
                DubAvailabilityChecked = true
            };

            Assert.Equal(4, AnimeDetailsViewModel.ResolveAvailableEpisodeCount(media, "Dub"));
            Assert.Equal(14, AnimeDetailsViewModel.ResolveAvailableEpisodeCount(media, "Sub"));
        }

        [Fact]
        public void UnknownOrSummaryDubCount_DoesNotPretendToBeAnEpisodeCeiling()
        {
            var media = new MediaResult
            {
                TotalEpisodes = 24,
                AvailableSubEpisodes = 15,
                AvailableDubEpisodes = 12,
                DubAvailabilityState = MediaDubAvailabilityState.Summary,
                DubAvailabilityChecked = true
            };

            Assert.Equal(15, AnimeDetailsViewModel.ResolveAvailableEpisodeCount(media, "Dub"));

            media.DubAvailabilityState = MediaDubAvailabilityState.Unknown;
            media.DubAvailabilityChecked = false;
            Assert.Equal(15, AnimeDetailsViewModel.ResolveAvailableEpisodeCount(media, "Dub"));
        }

        [Fact]
        public void PlannedEpisodeTotal_DoesNotBecomeReleasedAvailability()
        {
            var media = new MediaResult
            {
                TotalEpisodes = 24,
                AvailableSubEpisodes = 9
            };

            Assert.Equal(9, AnimeDetailsViewModel.ResolveAvailableEpisodeCount(media, "Sub"));
            Assert.Equal(9, AnimeDetailsViewModel.ResolveAvailableEpisodeCount(media, "Dub"));

            media.AvailableSubEpisodes = 0;
            Assert.Equal(0, AnimeDetailsViewModel.ResolveAvailableEpisodeCount(media, "Sub"));
            Assert.Equal(0, AnimeDetailsViewModel.ResolveAvailableEpisodeCount(media, "Dub"));
        }

        [Fact]
        public void Details_UpgradesSearchSummaryButDoesNotRepeatVerifiedLookup()
        {
            var media = new MediaResult
            {
                OfficialTitle = "Frieren: Beyond Journey's End",
                AvailableDubEpisodes = 28,
                DubAvailabilityChecked = true,
                DubAvailabilityState = MediaDubAvailabilityState.Summary
            };

            Assert.True(AnimeDetailsViewModel.ShouldResolveDubAvailability(media));

            media.DubAvailabilityState = MediaDubAvailabilityState.Verified;
            Assert.False(AnimeDetailsViewModel.ShouldResolveDubAvailability(media));
        }

        [Fact]
        public async Task SearchResults_DoNotEagerlyProbeEveryOffscreenCard()
        {
            string root = Path.Combine(Path.GetTempPath(), "umos-dub-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string configPath = Path.Combine(root, "config.json");
                File.WriteAllText(
                    configPath,
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        [DubAvailabilityService.ProviderConfigKey] =
                            "[{\"Name\":\"Mock\",\"SuggestUrlTemplate\":\"https://mock.example/search?keyword={query}\",\"Enabled\":true,\"ParserType\":\"Badge\"}]"
                    }));
                var config = new DomainHotSwapper(configPath);
                var handler = new CountingHandler();
                using var http = new HttpClient(handler);
                using var viewModel = new SearchViewModel(
                    new ManyResultSearch(),
                    new DubAvailabilityService(config, http),
                    new FavoriteMediaService(Path.Combine(root, "favorites.json")));

                viewModel.SearchQuery = "mock";
                await viewModel.SearchCommand.ExecuteAsync(null);
                await Task.Delay(100);

                Assert.Equal(20, viewModel.SearchResults.Count);
                Assert.Equal(0, handler.RequestCount);

                viewModel.RequestDubAvailabilityForVisibleResults(viewModel.SearchResults.ToList());
                for (int attempt = 0; attempt < 50 && handler.RequestCount < 12; attempt++)
                {
                    await Task.Delay(20);
                }

                Assert.Equal(12, handler.RequestCount);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void NewInstall_ConfiguresTheVerifiedDubAdapterButExplicitEmptyRemainsPossible()
        {
            string root = Path.Combine(Path.GetTempPath(), "umos-dub-default-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var config = new DomainHotSwapper(Path.Combine(root, "config.json"));
                string providers = config.GetSetting(DubAvailabilityService.ProviderConfigKey);

                Assert.Contains("AniKoto", providers, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("AdapterType", providers, StringComparison.Ordinal);

                config.SetSetting(DubAvailabilityService.ProviderConfigKey, "[]");
                Assert.Equal("[]", config.GetSetting(DubAvailabilityService.ProviderConfigKey));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        private sealed class ManyResultSearch : FuzzyShieldSearch
        {
            public override Task<MediaSearchPage> SearchAnimePageAsync(
                string query,
                int page,
                int perPage,
                AnimeSearchFilters? filters = null,
                CancellationToken token = default)
            {
                return Task.FromResult(new MediaSearchPage(
                    Enumerable.Range(1, 20)
                        .Select(id => new MediaResult
                        {
                            Id = id,
                            IdMal = 10_000 + id,
                            OfficialTitle = "Mock Anime " + id,
                            AvailableSubEpisodes = 12,
                            TotalEpisodes = 12
                        })
                        .ToList(),
                    HasNextPage: false));
            }
        }

        private sealed class CountingHandler : HttpMessageHandler
        {
            public int RequestCount;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref RequestCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html></html>")
                });
            }
        }
    }
}
