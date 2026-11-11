using System.IO;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

/// <summary>The anime resolver had no owner for its Chromium profile: a cancelled or killed lookup (the
/// October 23 live cancellation, or closing the app) left its folder until the 12-hour sweep, and a browser
/// still exiting after a normal run could keep its folder past scraper.close_browser. The fixture places its
/// profile exactly as scraper.launch_browser does and then blocks or exits without cleaning up.</summary>
public sealed class AnimeScraperBrowserProfileCleanupTests
{
    [Fact]
    public async Task CancelledAnimeLookupLeavesNoBrowserProfileBehind()
    {
        using var fixture = new Fixture(block: true);
        using var cancel = new CancellationTokenSource();
        Task resolve = fixture.Engine.ResolveAsync("Fixture Special", "1", 1, cancel.Token);

        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 1, TimeSpan.FromSeconds(20)),
            "The lookup should create its browser profile.");
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolve);

        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 0, TimeSpan.FromSeconds(10)),
            "Left behind: " + string.Join(", ", fixture.Profiles()));
    }

    [Fact]
    public async Task FinishedAnimeLookupLeavesNoBrowserProfileBehind()
    {
        using var fixture = new Fixture(block: false);
        Assert.Null(await fixture.Engine.ResolveAsync("Fixture Special", "1", 1));
        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 0, TimeSpan.FromSeconds(10)),
            "Left behind: " + string.Join(", ", fixture.Profiles()));
    }

    [Fact]
    public async Task ClosingTheAppStopsARunningAnimeLookupAndRemovesItsProfile()
    {
        using var fixture = new Fixture(block: true);
        Task resolve = fixture.Engine.ResolveAsync("Fixture Special", "1", 1);
        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 1, TimeSpan.FromSeconds(20)));

        ((object)fixture.Engine as IDisposable)?.Dispose();

        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 0, TimeSpan.FromSeconds(10)),
            "Left behind: " + string.Join(", ", fixture.Profiles()));
        Assert.Same(resolve, await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task CancellingOneAnimeLookupKeepsAnotherRunningProfile()
    {
        using var fixture = new Fixture(block: true);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        Task cancelled = fixture.Engine.ResolveAsync("Fixture Special", "1", 1, first.Token);
        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 1, TimeSpan.FromSeconds(20)));
        string[] cancelledProfile = fixture.Profiles();
        Task running = fixture.Engine.ResolveAsync("Fixture Special", "2", 1, second.Token);
        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 2, TimeSpan.FromSeconds(20)));
        string runningProfile = Assert.Single(fixture.Profiles().Except(cancelledProfile));

        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.True(SpinWait.SpinUntil(() => !Directory.Exists(cancelledProfile[0]), TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(Path.Combine(runningProfile, "Cookies")), "Another lookup's live profile must remain.");

        second.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 0, TimeSpan.FromSeconds(10)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.ScraperProfileTests", Guid.NewGuid().ToString("N"));
        private readonly string? _previousDataRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        private readonly PythonBootstrapper _python;
        public ScraperEngine Engine { get; }

        public Fixture(bool block)
        {
            string dataRoot = Path.Combine(_root, "profile");
            Directory.CreateDirectory(dataRoot);
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, dataRoot);
            string source = Path.Combine(_root, "source");
            Directory.CreateDirectory(source);
            // Same placement as scraper.launch_browser; a normal run here exits without close_browser's cleanup,
            // as when the browser still holds its files.
            string script = """
                import json, os, sys, tempfile, time
                data_root = os.environ.get('UNIVERSAL_MEDIA_OS_DATA_ROOT', '').strip()
                owned_root = os.environ.get('UMOS_SCRAPER_PROFILE_ROOT', '').strip()
                root = owned_root or (os.path.join(data_root, 'Local', 'UniversalMediaOS', 'BrowserProfiles')
                                      if data_root else tempfile.gettempdir())
                os.makedirs(root, exist_ok=True)
                profile = tempfile.mkdtemp(prefix='umos_scraper_', dir=root)
                with open(os.path.join(profile, 'Cookies'), 'w') as handle:
                    handle.write('browser state')
                """ + (block ? "\ntime.sleep(60)\n" : "\n") + """
                print(json.dumps({'error': 'all_indexed_sites_failed', 'attempted_sites': 1}))
                """;
            File.WriteAllText(Path.Combine(source, "scraper.py"), script);
            foreach (string other in new[] { "audiovisual_scraper.py", "book_scraper.py" })
                File.WriteAllText(Path.Combine(source, other), "# unused preparation fixture\n");
            _python = new(new PreparedRuntime(), source, Path.Combine(_root, "services"), ["python.exe"], TimeSpan.FromSeconds(10));
            Engine = new(_python);
        }

        public string[] Profiles()
        {
            string browserProfiles = Path.Combine(_root, "profile", "Local", "UniversalMediaOS", "BrowserProfiles");
            return Directory.Exists(browserProfiles)
                ? Directory.GetDirectories(browserProfiles, "umos_scraper_*", SearchOption.AllDirectories)
                    .Where(path => !Path.GetFileName(path).StartsWith("umos_scraper_run_", StringComparison.Ordinal)).ToArray()
                : [];
        }

        public void Dispose()
        {
            ((object)Engine as IDisposable)?.Dispose();
            _python.Dispose();
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _previousDataRoot);
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.ScraperProfileTests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class PreparedRuntime : IPreparationProcessRunner
    {
        public Task<PreparationProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
            => Task.FromResult(new PreparationProcessResult(0, arguments[0] == "--version" ? "Python 3.12.0" :
                arguments[1].StartsWith("import ast", StringComparison.Ordinal) ? "" : "[]"));
    }
}
