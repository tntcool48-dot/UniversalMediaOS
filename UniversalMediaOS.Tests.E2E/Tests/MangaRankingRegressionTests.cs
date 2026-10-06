using System.Collections.Generic;
using System.Linq;
using UniversalMediaOS.WPF.Controls;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class MangaRankingRegressionTests
    {
        [Fact]
        public void SearchRanking_PrefersExactTitleOverSubstringBerserkerMatches()
        {
            var results = new List<MangaSearchResult>
            {
                new()
                {
                    Id = "substring",
                    Title = "VRMMO Chronicles of a Solo Cleric ~Surprise! I'm Actually a Berserker!~"
                },
                new()
                {
                    Id = "exact",
                    Title = "Berserk",
                    AlternateTitles = ["ベルセルク"]
                }
            };

            var ranked = MangaService.RankSearchResultsForTesting(results, "Berserk");

            Assert.Equal("exact", ranked.First().Id);
        }

        [Fact]
        public void SearchRanking_UsesAlternateTitlesForExactMatches()
        {
            var results = new List<MangaSearchResult>
            {
                new() { Id = "loose", Title = "Some Berserker Story" },
                new()
                {
                    Id = "alt",
                    Title = "Kenpuu Denki",
                    AlternateTitles = ["Berserk"]
                }
            };

            var ranked = MangaService.RankSearchResultsForTesting(results, "Berserk");

            Assert.Equal("alt", ranked.First().Id);
        }

        [Fact]
        public void MangaDexApiClient_IdentifiesTheApplicationWithoutBrowserOrCorsHeaders()
        {
            using var client = MangaService.CreateHttpClientForTesting();

            Assert.Contains(client.DefaultRequestHeaders.UserAgent, agent => agent.Product?.Name == "UniversalMediaOS");
            Assert.DoesNotContain(client.DefaultRequestHeaders.UserAgent, agent => agent.Product?.Name == "Mozilla");
            Assert.Null(client.DefaultRequestHeaders.Referrer);
            Assert.False(client.DefaultRequestHeaders.Contains("Origin"));
            Assert.False(client.DefaultRequestHeaders.Contains("Sec-Fetch-Site"));
            Assert.Equal("application/json", client.DefaultRequestHeaders.Accept.Single().MediaType);
        }

        [Fact]
        public void MangaDexCoverRequest_DoesNotUseRejectedBrowserIdentity()
        {
            using var client = AsyncImageLoader.CreateHttpClientForTesting();
            using var request = AsyncImageLoader.CreateImageRequestForTesting(
                "https://uploads.mangadex.org/covers/title/cover.jpg.512.jpg");

            Assert.Empty(client.DefaultRequestHeaders.UserAgent);
            Assert.Contains(request.Headers.UserAgent, agent => agent.Product?.Name == "UniversalMediaOS");
            Assert.Null(request.Headers.Referrer);
            Assert.False(request.Headers.Contains("Origin"));
            Assert.False(request.Headers.Contains("Sec-Fetch-Site"));
            Assert.Contains(client.DefaultRequestHeaders.Accept, header => header.MediaType == "image/jpeg");
        }

        [Fact]
        public void OtherPosterProvidersRetainTheirExistingRequestHeaders()
        {
            using var request = AsyncImageLoader.CreateImageRequestForTesting("https://images.example/cover.jpg");
            Assert.Empty(request.Headers.UserAgent);
            Assert.Null(request.Headers.Referrer);
        }

        [Theory]
        [InlineData("javascript:alert(1)")]
        [InlineData("file:///C:/Windows/win.ini")]
        [InlineData("data:text/html,unsafe")]
        [InlineData("not-a-url")]
        public void ExternalReaderUrl_RejectsNonWebSchemes(string value)
        {
            Assert.Empty(MangaService.NormalizeExternalReaderUrl(value));
        }

        [Fact]
        public void ExternalReaderUrl_NormalizesHttpsUrl()
        {
            Assert.Equal(
                "https://reader.example/chapter/1",
                MangaService.NormalizeExternalReaderUrl(" https://reader.example/chapter/1 "));
        }
    }
}
