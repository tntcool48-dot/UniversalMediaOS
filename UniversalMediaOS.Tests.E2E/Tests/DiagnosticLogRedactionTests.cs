using UniversalMediaOS.Core.Helpers;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class DiagnosticLogRedactionTests
    {
        [Fact]
        public void LogSanitizer_RemovesUrlCredentialsQueriesAndFragments()
        {
            const string message =
                "GET https://alice:password@example.com:8443/video/master.m3u8?token=super-secret&expires=9#private, " +
                "fallback https://cdn.example.org/public/path.ts.";

            string safe = LogSanitizer.RedactSensitiveUrls(message);

            Assert.Contains("https://example.com:8443/video/master.m3u8?<redacted>#<redacted>,", safe);
            Assert.Contains("https://cdn.example.org/public/path.ts.", safe);
            Assert.DoesNotContain("alice", safe);
            Assert.DoesNotContain("password", safe);
            Assert.DoesNotContain("super-secret", safe);
            Assert.DoesNotContain("expires=9", safe);
        }

        [Fact]
        public void LogSanitizer_RedactsNestedSignedProxyQueryAsOneUnit()
        {
            const string message =
                "Proxy http://127.0.0.1:32145/seg?id=session-secret&url=https%3A%2F%2Fcdn.example%2Fpart.ts%3Fsig%3Dsecret";

            string safe = LogSanitizer.RedactSensitiveUrls(message);

            Assert.Equal("Proxy http://127.0.0.1:32145/seg?<redacted>", safe);
            Assert.DoesNotContain("session-secret", safe);
            Assert.DoesNotContain("sig", safe);
        }

        [Fact]
        public void LogSanitizer_FailsClosedForMalformedCredentialUrl()
        {
            const string message = "Bad https://user:pass@example.com:invalid/video?token=secret";

            string safe = LogSanitizer.RedactSensitiveUrls(message);

            Assert.Equal("Bad https://example.com:invalid/video?<redacted>", safe);
            Assert.DoesNotContain("user", safe);
            Assert.DoesNotContain("pass", safe);
            Assert.DoesNotContain("secret", safe);
        }

        [Fact]
        public void LogSanitizer_RedactsOpaqueSignedPathSegmentsButKeepsReadableRoutes()
        {
            const string pathToken =
                "OD9Ahdg3U16VFqZchzRihy0m6xWuMBhXnasOzMv4JXgJ9i2me9_iYpaaVGyyNmzg";
            const string message =
                "Media https://cdn.example/segment/" + pathToken +
                " from https://anime.example/watch/frieren-beyond-journeys-end/ep-1";

            string safe = LogSanitizer.RedactSensitiveUrls(message);

            Assert.Equal(
                "Media https://cdn.example/segment/<redacted> " +
                "from https://anime.example/watch/frieren-beyond-journeys-end/ep-1",
                safe);
            Assert.DoesNotContain(pathToken, safe);
        }
    }
}
