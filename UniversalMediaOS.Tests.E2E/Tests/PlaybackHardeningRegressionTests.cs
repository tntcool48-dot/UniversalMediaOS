using System.Text.Json;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Views;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class PlaybackHardeningRegressionTests
    {
        [Fact]
        public void NormalizeMediaSource_PreservesSignedAndNestedNetworkUrl()
        {
            const string source =
                "http://127.0.0.1:19475/seg?id=abc&url=https%3A%2F%2Fcdn.example%2Fpart.ts%3Ftoken%3Da%252Bb%26expires%3D1";

            string normalized = PlaybackViewModel.NormalizeMediaSource(source);

            Assert.Equal(source, normalized);
        }

        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(30, 30, false)]
        [InlineData(85, 100, false)]
        [InlineData(900, 1200, false)]
        [InlineData(1020, 1200, true)]
        [InlineData(1141, 1200, true)]
        [InlineData(600, 0, true)]
        public void CompletionCredibility_RejectsAdsAndPrematureEnds(
            double positionSeconds,
            double durationSeconds,
            bool expected)
        {
            Assert.Equal(expected, PlaybackViewModel.IsCredibleCompletion(positionSeconds, durationSeconds));
        }

        [Fact]
        public void AdBlockList_DoesNotBlockCommonVideoCdns()
        {
            var domains = PlaybackView.GetBlockedDomains();

            Assert.DoesNotContain("akamaihd.net", domains);
            Assert.DoesNotContain("googlevideo.com", domains);
        }

        [Fact]
        public void HlsQualityParser_ReturnsOnlyAdvertisedMasterVariantsInDescendingOrder()
        {
            const string manifest = """
                #EXTM3U
                #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360
                http://127.0.0.1:19475/stream?id=a&url=360
                #EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080
                http://127.0.0.1:19475/stream?id=a&url=1080
                #EXT-X-STREAM-INF:BANDWIDTH=2200000,RESOLUTION=1280x720
                http://127.0.0.1:19475/stream?id=a&url=720
                """;

            var qualities = PlaybackViewModel.ParseHlsQualityOptions(manifest);

            Assert.Collection(
                qualities,
                option => Assert.Equal(("1080p", 1080), (option.Label, option.Height)),
                option => Assert.Equal(("720p", 720), (option.Label, option.Height)),
                option => Assert.Equal(("360p", 360), (option.Label, option.Height)));
        }

        [Fact]
        public void WebTelemetry_RequiresNavigationTokenAndExpectedOrigin()
        {
            const string token = "8B304FE8836F6AB133477C0F53DAA9C64C8B9DBCF67E1F8A4EC4EE0B22EAAB74";
            string message = JsonSerializer.Serialize(new
            {
                type = "ums-video-progress",
                sessionToken = token,
                currentTime = 1_150d,
                duration = 1_200d,
                ended = true
            });

            Assert.True(PlaybackView.TryParseAuthenticatedTelemetry(
                message,
                "https://player.example/frame/episode-1",
                "https://player.example/embed?id=1",
                token,
                out PlaybackView.AuthenticatedWebTelemetry telemetry));
            Assert.True(telemetry.Ended);
            Assert.Equal(1_150d, telemetry.CurrentTime);

            Assert.False(PlaybackView.TryParseAuthenticatedTelemetry(
                message,
                "https://attacker.example/frame",
                "https://player.example/embed?id=1",
                token,
                out _));
            Assert.False(PlaybackView.TryParseAuthenticatedTelemetry(
                message.Replace(token, new string('0', token.Length), StringComparison.Ordinal),
                "https://player.example/frame/episode-1",
                "https://player.example/embed?id=1",
                token,
                out _));
        }

        [Fact]
        public void WebTelemetry_RejectsImpossibleTimesAndUnknownActions()
        {
            const string token = "session-token";
            const string source = "https://player.example/embed";
            string impossibleTime = JsonSerializer.Serialize(new
            {
                type = "ums-video-progress",
                sessionToken = token,
                currentTime = 900d,
                duration = 300d,
                ended = true
            });
            string unknownAction = JsonSerializer.Serialize(new
            {
                type = "ums-video-action",
                sessionToken = token,
                action = "complete-mal",
                currentTime = 280d,
                duration = 300d
            });

            Assert.False(PlaybackView.TryParseAuthenticatedTelemetry(
                impossibleTime, source, source, token, out _));
            Assert.False(PlaybackView.TryParseAuthenticatedTelemetry(
                unknownAction, source, source, token, out _));
        }

        [Fact]
        public void CapturedRequestHeaders_ArePreservedAndSensitiveHeadersRequireProxy()
        {
            var mutableHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer test-token",
                ["X-Playback-Key"] = "key"
            };
            var message = new PlayMediaMessage(
                "https://cdn.example/video.m3u8",
                "Example",
                referer: string.Empty,
                contentType: "application/vnd.apple.mpegurl",
                userAgent: "ScraperAgent/1.0",
                cookie: "session=test",
                requestHeaders: mutableHeaders);
            mutableHeaders["Authorization"] = "changed";

            Assert.Equal(string.Empty, message.Referer);
            Assert.Equal("Bearer test-token", message.RequestHeaders["Authorization"]);
            Assert.True(PlaybackViewModel.RequiresHeaderPreservingProxy(
                message.Cookie,
                message.RequestHeaders));
            Assert.True(PlaybackViewModel.LooksLikeHlsSource(message.Value, message.ContentType));
            Assert.False(PlaybackViewModel.RequiresHeaderPreservingProxy(
                string.Empty,
                new Dictionary<string, string>
                {
                    ["User-Agent"] = "Agent/1.0",
                    ["Referer"] = "https://player.example/"
                }));
        }

        [Fact]
        public void WebRequestContext_ScopesHeadersAndParsesOnlyCookiePairs()
        {
            IReadOnlyList<KeyValuePair<string, string>> cookies = PlaybackView.ParseCookieHeader(
                "session=abc; token=value=with-equals; Path=/; SameSite=None; Secure");

            Assert.Collection(
                cookies.OrderBy(cookie => cookie.Key),
                cookie => Assert.Equal(("session", "abc"), (cookie.Key, cookie.Value)),
                cookie => Assert.Equal(("token", "value=with-equals"), (cookie.Key, cookie.Value)));
            Assert.True(PlaybackView.ShouldApplyWebRequestHeaders(
                "https://player.example/assets/video.js",
                "https://player.example/embed"));
            Assert.False(PlaybackView.ShouldApplyWebRequestHeaders(
                "https://cdn.player.example/video.ts",
                "https://player.example/embed"));
            Assert.False(PlaybackView.ShouldApplyWebRequestHeaders(
                "https://attacker.example/redirect",
                "https://player.example/embed"));
            Assert.True(PlaybackView.IsAllowedWebRequestHeader(
                "Authorization",
                "Bearer test-token"));
            Assert.False(PlaybackView.IsAllowedWebRequestHeader(
                "cOoKiE",
                "stolen=value"));
            Assert.False(PlaybackView.IsAllowedWebRequestHeader(
                "X-Test",
                "valid\r\ninjected: value"));
        }

        [Fact]
        public async Task EpisodePlaybackContext_RejectsOutOfRangeWithoutCallingResolver()
        {
            int calls = 0;
            var context = new EpisodePlaybackContext(
                1,
                12,
                (episode, _) =>
                {
                    calls++;
                    return Task.FromResult<ResolvedEpisodePlayback?>(
                        new ResolvedEpisodePlayback($"https://example.test/{episode}", $"Episode {episode}", false));
                });

            Assert.Null(await context.ResolveAsync(13, CancellationToken.None));
            Assert.Equal(0, calls);
            Assert.NotNull(await context.ResolveAsync(12, CancellationToken.None));
            Assert.Equal(1, calls);
        }
    }
}
