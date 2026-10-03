using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using UniversalMediaOS.Core.Services;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    [Collection("SequentialBootstrapperTests")]
    public class BootstrapperTests
    {
        [Fact]
        public async Task DependencyBootstrapper_ShouldNotThrow_WhenFfmpegIsMissing()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", null);

                var dep = new DependencyBootstrapper(tempDir);
                var exception = await Record.ExceptionAsync(async () => await dep.EnsureDependenciesAsync());

                Assert.Null(exception);
                Assert.NotEqual("Not checked.", dep.FfmpegStatus);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", oldPath);
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task DependencyBootstrapper_ShouldHandleInvalidCharactersInPath()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            
            var oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                // Inject invalid characters in PATH
                Environment.SetEnvironmentVariable("PATH", "C:\\invalid<path>dir;\"C:\\another?dir\"");
                
                var dep = new DependencyBootstrapper(tempDir);
                var exception = await Record.ExceptionAsync(async () => await dep.EnsureDependenciesAsync());

                Assert.Null(exception);
                Assert.NotEqual("Not checked.", dep.FfmpegStatus);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", oldPath);
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ConsumetBootstrapper was removed in v3.0 — Python scraper + HLS proxy replaced it.
        // These tests are retained as skipped stubs to preserve test history.

        [Fact]
        public async Task DependencyBootstrapper_DetectsNestedUBlockManifestWithoutRedownloading()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            string localAppData = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            string nestedExtensionDir = Path.Combine(
                localAppData,
                "UniversalMediaOS",
                "Extensions",
                "ublock-origin",
                "uBlock0.chromium");
            Directory.CreateDirectory(nestedExtensionDir);
            await File.WriteAllTextAsync(
                Path.Combine(nestedExtensionDir, "manifest.json"),
                "{\"version\":\"1.71.0\"}");

            try
            {
                using var httpClient = new HttpClient(new ThrowingHandler());
                var dep = new DependencyBootstrapper(
                    baseDir,
                    logger: null,
                    httpClient,
                    TimeSpan.FromMilliseconds(1),
                    localAppData);

                await dep.EnsureUBlockOriginAsync();

                Assert.True(dep.IsUBlockOriginAvailable);
                Assert.Equal(nestedExtensionDir, dep.GetUBlockOriginPath());
                Assert.Contains(nestedExtensionDir, dep.UBlockOriginStatus);
            }
            finally
            {
                try { Directory.Delete(baseDir, true); } catch { }
                try { Directory.Delete(localAppData, true); } catch { }
            }
        }

        [Fact]
        public void DependencyBootstrapper_UBlockDigest_IsPinnedAndComparedExactly()
        {
            Assert.True(DependencyBootstrapper.HasExpectedUBlockDigest(
                DependencyBootstrapper.PinnedUBlockSha256.ToUpperInvariant()));
            Assert.False(DependencyBootstrapper.HasExpectedUBlockDigest(
                new string('0', DependencyBootstrapper.PinnedUBlockSha256.Length)));
            Assert.False(DependencyBootstrapper.HasExpectedUBlockDigest(string.Empty));
        }

        [Fact]
        public async Task DependencyBootstrapper_RejectsUBlockArchiveWithWrongDigest()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            string localAppData = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(baseDir);
            Directory.CreateDirectory(localAppData);

            try
            {
                using var httpClient = new HttpClient(
                    new StaticPayloadHandler("not-a-reviewed-extension"u8.ToArray()));
                var dep = new DependencyBootstrapper(
                    baseDir,
                    logger: null,
                    httpClient,
                    TimeSpan.FromSeconds(2),
                    localAppData);

                await dep.EnsureUBlockOriginAsync();

                Assert.False(dep.IsUBlockOriginAvailable);
                Assert.Contains("integrity check failed", dep.UBlockOriginStatus, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(Directory.EnumerateFiles(localAppData, "*.zip", SearchOption.AllDirectories));
            }
            finally
            {
                try { Directory.Delete(baseDir, true); } catch { }
                try { Directory.Delete(localAppData, true); } catch { }
            }
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Network should not be used for an already installed nested uBlock extension.");
            }
        }

        private sealed class StaticPayloadHandler(byte[] payload) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Assert.Equal(
                    "https://github.com/gorhill/uBlock/releases/download/1.71.0/uBlock0_1.71.0.chromium.zip",
                    request.RequestUri?.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                });
            }
        }

    }
}
