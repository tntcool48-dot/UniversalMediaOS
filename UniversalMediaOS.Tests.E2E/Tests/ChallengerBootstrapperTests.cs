using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Helpers;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    [Collection("SequentialBootstrapperTests")]
    public class ChallengerBootstrapperTests
    {
        private class TestLogger<T> : ILogger<T>, IDisposable
        {
            public List<string> LoggedMessages { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => this;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var msg = formatter(state, exception);
                lock (LoggedMessages) { LoggedMessages.Add($"[{logLevel}] {msg}"); }
            }
            public void Dispose() { }
        }

        [Fact]
        public async Task DependencyBootstrapper_ShouldNotDownloadNodeDuringStartup()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            AppLogger.Initialize(tempDir);
            AppLogger.IsEnabled = true;

            var logger = new TestLogger<DependencyBootstrapper>();

            try
            {
                var dep = new DependencyBootstrapper(tempDir, logger);
                var exception = await Record.ExceptionAsync(async () => await dep.EnsureDependenciesAsync());

                Assert.Null(exception);

                lock (logger.LoggedMessages)
                {
                    Assert.DoesNotContain(logger.LoggedMessages, msg => msg.Contains("Node.js", StringComparison.OrdinalIgnoreCase));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

    }
}
