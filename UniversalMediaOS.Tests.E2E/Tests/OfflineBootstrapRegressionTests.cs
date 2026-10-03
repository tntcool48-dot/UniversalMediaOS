using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class OfflineBootstrapRegressionTests
    {
        [Fact]
        public async Task UBlockBootstrap_TimesOutQuicklyAndLogsWarningWhenOffline()
        {
            string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var logger = new RecordingLogger<DependencyBootstrapper>();
            using var client = new HttpClient(new BlockingHandler())
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            var bootstrapper = new DependencyBootstrapper(
                root,
                logger,
                client,
                TimeSpan.FromMilliseconds(100),
                root);

            var stopwatch = Stopwatch.StartNew();
            await bootstrapper.EnsureUBlockOriginAsync();
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Offline bootstrap took {stopwatch.Elapsed}.");
            Assert.False(bootstrapper.IsUBlockOriginAvailable);
            Assert.Contains("Timed out", bootstrapper.UBlockOriginStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(logger.Entries, entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase));

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }

        private sealed class BlockingHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
        }

        private sealed class RecordingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
