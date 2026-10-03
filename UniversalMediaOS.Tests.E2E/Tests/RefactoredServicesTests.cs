using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Helpers;
using System.Diagnostics;
using System.Threading;
using System.Reflection;
using UniversalMediaOS.Core.Streaming;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    [Collection("SequentialBootstrapperTests")]
    public class RefactoredServicesTests
    {
        [Fact]
        public async Task PythonBootstrapper_ShouldNotThrow_WhenScraperPyMissing()
        {
            // The new bootstrapper uses no-arg ctor and copies scraper.py from AppContext.BaseDirectory.
            // If scraper.py is absent from build output the method logs a warning but does not throw.
            var pythonBoot = new PythonBootstrapper();
            var ex = await Record.ExceptionAsync(() => pythonBoot.EnsureScraperReadyAsync());
            // Should complete without exception (warnings are logged internally)
            Assert.Null(ex);
        }

        [Fact]
        public void SystemResourceCheck_ShouldAllowConfigurableThresholds()
        {
            var check = new SystemResourceCheck(1024.0);
            check.RunCheck();

            if (!check.IsReady)
            {
                Assert.Contains("At least 1024 MB required", check.StatusMessage);
            }
            else
            {
                Assert.Contains("GB available", check.StatusMessage);
            }
        }

        [Fact]
        public async Task SystemResourceCheck_ShouldRunCheckAsync()
        {
            var check = new SystemResourceCheck(256.0);
            await check.RunCheckAsync();

            Assert.NotNull(check.StatusMessage);
        }

        [Fact]
        public async Task SystemResourceCheck_ThreadSafetyAccess()
        {
            var check = new SystemResourceCheck(512.0);
            
            var tasks = new Task[10];
            for (int i = 0; i < 10; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 5; j++)
                    {
                        check.RunCheck();
                        bool ready = check.IsReady;
                        string msg = check.StatusMessage;
                        Assert.NotNull(msg);
                    }
                });
            }
            await Task.WhenAll(tasks);
        }

        [Fact]
        public async Task ServiceManager_ShouldPruneExitedProcesses()
        {
            using var mgr = new ServiceManager();
            string cmd = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "cmd.exe" : "sh";
            string args = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "/c exit 0" : "-c exit 0";

            mgr.StartService(cmd, args, Directory.GetCurrentDirectory());

            await Task.Delay(1000);

            var field = typeof(ServiceManager).GetField("_managedProcesses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var list = (System.Collections.IList?)field?.GetValue(mgr);

            Assert.NotNull(list);
            Assert.Empty(list);
        }

        [Fact]
        public async Task ServiceManager_ShouldStopAllAsynchronously()
        {
            using var mgr = new ServiceManager();
            string cmd = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "ping.exe" : "sleep";
            string args = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "127.0.0.1 -n 10" : "10";

            await mgr.StartServiceAsync(cmd, args, Directory.GetCurrentDirectory());

            var field = typeof(ServiceManager).GetField("_managedProcesses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var list = (System.Collections.IList?)field?.GetValue(mgr);

            Assert.NotNull(list);
            Assert.NotEmpty(list);

            await mgr.StopAllAsync();

            Assert.Empty(list);
        }

        [Fact]
        public void HlsLoopbackProxy_ShouldRewriteAllPlaylistUrisThroughLocalhost()
        {
            using var proxy = new HlsLoopbackProxy();
            proxy.Start();
            Assert.True(proxy.IsRunning, proxy.LastStartupError);
            string localRoot = proxy.ListeningUri!.AbsoluteUri.TrimEnd('/');
            var session = new ProxySession(
                "https://cdn.example.com/hls/master.m3u8",
                "UA",
                "sid=1",
                "https://cdn.example.com/hls/fallback.key?token=a%2Bb",
                "https://site.example/watch",
                DateTime.UtcNow);

            string manifest = string.Join("\n", new[]
            {
                "#EXTM3U",
                "#EXT-X-KEY:METHOD=AES-128,URI=\"keys/main.key?token=a%2Bb%2Fc==&expires=1\"",
                "#EXT-X-MAP:URI=\"init.mp4?range=0-999\"",
                "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",URI=\"audio/eng/prog.m3u8\"",
                "#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=86000,URI=\"iframes/iframe.m3u8\"",
                "#EXT-X-STREAM-INF:BANDWIDTH=4000000",
                "1080p/index.m3u8",
                "segments/seg-0001.ts?token=a%2Bb%2Fc==&expires=1"
            });

            string rewritten = RewriteManifest(proxy, manifest, "https://cdn.example.com/hls/master.m3u8", "abc", session);

            Assert.Contains($"{localRoot}/key?id=abc&url=", rewritten);
            Assert.Contains($"{localRoot}/seg?id=abc&url=", rewritten);
            Assert.Contains($"{localRoot}/stream?id=abc&url=", rewritten);
            Assert.Contains(Uri.EscapeDataString("https://cdn.example.com/hls/1080p/index.m3u8"), rewritten);
            Assert.Contains(Uri.EscapeDataString("https://cdn.example.com/hls/audio/eng/prog.m3u8"), rewritten);
            Assert.Contains(Uri.EscapeDataString("https://cdn.example.com/hls/iframes/iframe.m3u8"), rewritten);
            Assert.Contains(Uri.EscapeDataString("https://cdn.example.com/hls/segments/seg-0001.ts?token=a%2Bb%2Fc==&expires=1"), rewritten);
            Assert.Contains("token%3Da%252Bb%252Fc%3D%3D%26expires%3D1", rewritten);
        }

        [Fact]
        public void HlsLoopbackProxy_ShouldUseSessionKeyUrl_WhenKeyTagHasNoUri()
        {
            using var proxy = new HlsLoopbackProxy();
            proxy.Start();
            Assert.True(proxy.IsRunning, proxy.LastStartupError);
            string localRoot = proxy.ListeningUri!.AbsoluteUri.TrimEnd('/');
            var session = new ProxySession(
                "https://cdn.example.com/hls/master.m3u8",
                null,
                null,
                "https://keys.example.com/k.bin?sig=a%2Bb",
                null,
                DateTime.UtcNow);

            string rewritten = RewriteManifest(
                proxy,
                "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128\nsegment.ts",
                "https://cdn.example.com/hls/master.m3u8",
                "sid",
                session);

            Assert.Contains($"#EXT-X-KEY:METHOD=AES-128,URI=\"{localRoot}/key?id=sid&url=", rewritten);
            Assert.Contains(Uri.EscapeDataString("https://keys.example.com/k.bin?sig=a%2Bb"), rewritten);
        }

        [Fact]
        public void HlsLoopbackProxy_ShouldTreatExtensionlessVariantAndAudioUrisAsPlaylists()
        {
            using var proxy = new HlsLoopbackProxy();
            proxy.Start();
            Assert.True(proxy.IsRunning, proxy.LastStartupError);
            string localRoot = proxy.ListeningUri!.AbsoluteUri.TrimEnd('/');
            var session = new ProxySession(
                "https://cdn.example.com/master",
                "UA",
                null,
                null,
                "https://site.example/watch",
                DateTime.UtcNow);

            string manifest = string.Join("\n", new[]
            {
                "#EXTM3U",
                "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",URI=\"audio?id=eng\"",
                "#EXT-X-STREAM-INF:BANDWIDTH=4000000,AUDIO=\"aud\"",
                "variant?id=1080"
            });

            string rewritten = RewriteManifest(proxy, manifest, "https://cdn.example.com/master", "sid", session);

            Assert.Contains(
                $"{localRoot}/stream?id=sid&url=" +
                Uri.EscapeDataString("https://cdn.example.com/variant?id=1080"),
                rewritten);
            Assert.Contains(
                $"{localRoot}/stream?id=sid&url=" +
                Uri.EscapeDataString("https://cdn.example.com/audio?id=eng"),
                rewritten);
        }

        [Fact]
        public void HlsLoopbackProxy_ShouldFallbackWhenPreferredPortIsAlreadyBound()
        {
            using var first = new HlsLoopbackProxy();
            first.Start();
            Assert.True(first.IsRunning, first.LastStartupError);

            int occupiedPort = first.ListeningUri!.Port;
            using var second = new HlsLoopbackProxy(occupiedPort);
            second.Start();

            Assert.True(second.IsRunning, second.LastStartupError);
            Assert.NotEqual(occupiedPort, second.ListeningUri!.Port);
            Assert.Equal("127.0.0.1", second.ListeningUri.Host);
            Assert.StartsWith(
                second.ListeningUri.AbsoluteUri + "stream?id=session",
                second.CreateStreamUrl("session"),
                StringComparison.Ordinal);

            second.Stop();
            Assert.False(second.IsRunning);
            Assert.Null(second.ListeningUri);
            Assert.Equal("Not listening", second.ListeningEndpoint);
        }

        [Fact]
        public void HlsLoopbackProxy_ShouldOnlyOwnCurrentEndpointAndRegisteredSession()
        {
            using var proxy = new HlsLoopbackProxy();
            proxy.Start();
            Assert.True(proxy.IsRunning, proxy.LastStartupError);

            string sessionId = proxy.RegisterSession(new ProxySession(
                "https://cdn.example.com/master.m3u8?token=secret",
                null,
                null,
                null,
                null,
                DateTime.UtcNow));
            string ownedUrl = proxy.CreateStreamUrl(sessionId);
            Uri ownedUri = new(ownedUrl);

            Assert.True(proxy.OwnsStreamUrl(ownedUrl));
            Assert.True(proxy.OwnsStreamUrl(proxy.CreateStreamUrl(sessionId, "https://cdn.example.com/variant.m3u8")));
            Assert.False(proxy.OwnsStreamUrl($"http://127.0.0.1:{DifferentPort(ownedUri.Port)}/stream?id={sessionId}"));
            Assert.False(proxy.OwnsStreamUrl($"{proxy.ListeningUri!.AbsoluteUri}stream?id=unknown-session"));
            Assert.False(proxy.OwnsStreamUrl($"{proxy.ListeningUri.AbsoluteUri}seg?id={sessionId}"));
            Assert.False(proxy.OwnsStreamUrl($"http://localhost:{ownedUri.Port}/stream?id={sessionId}"));

            string expiredId = proxy.RegisterSession(new ProxySession(
                "https://cdn.example.com/expired.m3u8",
                null,
                null,
                null,
                null,
                DateTime.UtcNow.AddHours(-5)));
            Assert.False(proxy.OwnsStreamUrl(proxy.CreateStreamUrl(expiredId)));
        }

        private static int DifferentPort(int port) => port == 65535 ? 65534 : port + 1;

        [Theory]
        [InlineData("http://127.0.0.1/private.m3u8", false)]
        [InlineData("http://10.0.0.5/private.m3u8", false)]
        [InlineData("http://localhost/private.m3u8", false)]
        [InlineData("https://user:pass@example.com/master.m3u8", false)]
        [InlineData("file:///c:/secret.txt", false)]
        [InlineData("https://cdn.example.com/master.m3u8", true)]
        public void HlsLoopbackProxy_ShouldRejectUnsafeRemoteAddresses(string url, bool expected)
        {
            Assert.Equal(expected, HlsLoopbackProxy.IsAllowedRemoteUri(url));
        }

        [Theory]
        [InlineData("0", 0)]
        [InlineData("31", 31)]
        [InlineData("-1", 6)]
        [InlineData("invalid", 6)]
        public void ScraperSiteAttemptLimit_PreservesAllAndUnboundedValues(
            string configuredValue,
            int expected)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "umos-site-limit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var config = new UniversalMediaOS.Core.Configuration.DomainHotSwapper(
                    Path.Combine(root, "config.json"));
                config.SetSetting("ScraperSiteAttemptLimit", configuredValue);
                using var python = new PythonBootstrapper();
                var scraper = new ScraperEngine(python);
                using var proxy = new HlsLoopbackProxy();
                var router = new UniversalMediaOS.Core.Routing.TripleNetHandoff(
                    config,
                    scraper,
                    proxy);
                MethodInfo? method = typeof(UniversalMediaOS.Core.Routing.TripleNetHandoff)
                    .GetMethod(
                        "GetScraperSiteAttemptLimit",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.NotNull(method);
                Assert.Equal(expected, (int)method!.Invoke(router, null)!);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ScraperResolve_AllSitesRetainsExistingMaximumDeadline()
        {
            MethodInfo? method = typeof(ScraperEngine).GetMethod(
                "ComputeResolveTimeoutMs",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            int allSitesTimeout = (int)method!.Invoke(null, new object[] { 0 })!;
            int legacyMaximumTimeout = (int)method.Invoke(null, new object[] { 30 })!;
            int overThirtyTimeout = (int)method.Invoke(null, new object[] { 31 })!;
            Assert.Equal(legacyMaximumTimeout, allSitesTimeout);
            Assert.Equal(legacyMaximumTimeout, overThirtyTimeout);
        }

        [Fact]
        public async Task WebViewResolver_RejectsBlankProviderInsteadOfBuildingMalformedHttpsUrl()
        {
            string root = Path.Combine(Path.GetTempPath(), "umos-webview-route-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var config = new UniversalMediaOS.Core.Configuration.DomainHotSwapper(Path.Combine(root, "config.json"));
                using var python = new PythonBootstrapper();
                var scraper = new ScraperEngine(python);
                using var proxy = new HlsLoopbackProxy();
                var router = new UniversalMediaOS.Core.Routing.TripleNetHandoff(config, scraper, proxy);
                var method = typeof(UniversalMediaOS.Core.Routing.TripleNetHandoff).GetMethod(
                    "BuildWebViewUrlAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                Assert.NotNull(method);
                var task = (Task<string>)method!.Invoke(
                    router,
                    new object[]
                    {
                        string.Empty,
                        "Frieren",
                        "1",
                        new Action<string>(_ => { }),
                        CancellationToken.None
                    })!;

                var error = await Assert.ThrowsAsync<ArgumentException>(() => task);
                Assert.Contains("provider URL", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task DependencyBootstrapper_FfmpegVerification_ShouldBeNonFatal()
        {
            var dep = new DependencyBootstrapper(Path.GetTempPath());
            var ex = await Record.ExceptionAsync(() => dep.VerifyFfmpegAsync());
            Assert.Null(ex);
            Assert.NotEqual("Not checked.", dep.FfmpegStatus);
        }

        private static string RewriteManifest(
            HlsLoopbackProxy proxy,
            string manifest,
            string remoteManifestUrl,
            string sessionId,
            ProxySession session)
        {
            var getBaseUrl = typeof(HlsLoopbackProxy).GetMethod(
                "GetBaseUrl",
                BindingFlags.NonPublic | BindingFlags.Static);
            var rewrite = typeof(HlsLoopbackProxy).GetMethod(
                "RewriteManifest",
                BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(getBaseUrl);
            Assert.NotNull(rewrite);

            string baseUrl = (string)getBaseUrl!.Invoke(null, new object[] { remoteManifestUrl })!;
            return (string)rewrite!.Invoke(proxy, new object[] { manifest, baseUrl, sessionId, session })!;
        }
    }
}
