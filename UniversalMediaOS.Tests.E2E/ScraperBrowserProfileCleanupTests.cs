using System.IO;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

/// <summary>A live Vidsrc probe on October 24, 2026 left one 70-97 MB Chromium profile for every film whose
/// verified native source cancelled the parallel lookup: cancellation kills the scraper's process tree before
/// its own cleanup runs. The fixture script places its profile exactly as audiovisual_scraper.launch_browser
/// does and then blocks, standing in for a browser extraction that is still running.</summary>
public sealed class ScraperBrowserProfileCleanupTests
{
    [Fact]
    public async Task CancelledExtractionLeavesNoBrowserProfileBehind()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        Task resolve = fixture.Engine.ResolveAsync("https://player.invalid/embed/movie/tt0013442", cancel.Token);

        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 1, TimeSpan.FromSeconds(20)),
            "The extraction should create its browser profile.");
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolve);

        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 0, TimeSpan.FromSeconds(10)),
            "Left behind: " + string.Join(", ", fixture.Profiles()));
    }

    [Fact]
    public async Task CancellingOneLookupKeepsAnotherTabsRunningProfile()
    {
        using var fixture = new Fixture();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        Task cancelled = fixture.Engine.ResolveAsync("https://player.invalid/embed/movie/tt0063350", first.Token);
        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 1, TimeSpan.FromSeconds(20)));
        string[] cancelledProfile = fixture.Profiles();
        Task running = fixture.Engine.ResolveAsync("https://player.invalid/embed/movie/tt0029606", second.Token);
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

    [Fact]
    public async Task ClosingTheAppStopsARunningLookupAndRemovesItsProfile()
    {
        // October 24 real app: closing it during Movies "Find sources" left the scraper processes running on their
        // own, launching Chromium after the app had exited, and left their run folders behind. App.OnExit disposes
        // the service container that owns the engine.
        using var fixture = new Fixture();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, _ => fixture.Engine);
        var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var engine = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AudiovisualScraperEngine>(provider);
        Task lookup = engine.ResolveAsync("https://player.invalid/embed/movie/tt0013442");
        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 1 &&
            File.Exists(Path.Combine(fixture.Profiles()[0], "pid")), TimeSpan.FromSeconds(20)));
        string profile = fixture.Profiles()[0];
        int pid = int.Parse(File.ReadAllText(Path.Combine(profile, "pid")));
        try
        {
            provider.Dispose();

            Assert.True(SpinWait.SpinUntil(() => !IsRunning(pid), TimeSpan.FromSeconds(10)), "The lookup outlived the app.");
            Assert.True(SpinWait.SpinUntil(() => fixture.Runs().Length == 0, TimeSpan.FromSeconds(10)),
                "Left behind: " + string.Join(", ", fixture.Runs()));
            await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(lookup.IsCompleted, "The lookup task must end when the app closes.");
        }
        finally
        {
            if (IsRunning(pid)) System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task ProfileStillHeldWhenItsLookupEndsIsRemovedWhenTheAppCloses()
    {
        // October 24 real app: after a normal Movies "Find sources", one run folder survived the app's close.
        // A browser that is still exiting can hold its profile files after the lookup's own cleanup gives up.
        using var fixture = new Fixture();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, _ => fixture.Engine);
        var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var engine = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AudiovisualScraperEngine>(provider);
        Task lookup = engine.ResolveAsync("https://player.invalid/embed/movie/finish");
        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 1 &&
            File.Exists(Path.Combine(fixture.Profiles()[0], "pid")), TimeSpan.FromSeconds(20)));
        using (new FileStream(Path.Combine(fixture.Profiles()[0], "Cookies"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await lookup;
        }
        Assert.NotEmpty(fixture.Runs());

        provider.Dispose();

        Assert.True(SpinWait.SpinUntil(() => fixture.Runs().Length == 0, TimeSpan.FromSeconds(10)),
            "Left behind: " + string.Join(", ", fixture.Runs()));
    }

    [Fact]
    public async Task ProfileHeldPastItsLookupIsRemovedOnceReleasedWithoutClosingTheApp()
    {
        using var fixture = new Fixture();
        Task lookup = fixture.Engine.ResolveAsync("https://player.invalid/embed/movie/finish");
        Assert.True(SpinWait.SpinUntil(() => fixture.Profiles().Length == 1 &&
            File.Exists(Path.Combine(fixture.Profiles()[0], "pid")), TimeSpan.FromSeconds(20)));
        using (new FileStream(Path.Combine(fixture.Profiles()[0], "Cookies"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await lookup;
        }

        Assert.True(SpinWait.SpinUntil(() => fixture.Runs().Length == 0, TimeSpan.FromSeconds(10)),
            "Left behind: " + string.Join(", ", fixture.Runs()));
    }

    private static bool IsRunning(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.ScraperProfileTests", Guid.NewGuid().ToString("N"));
        private readonly string? _previousDataRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        private readonly PythonBootstrapper _python;
        public AudiovisualScraperEngine Engine { get; }

        public Fixture()
        {
            string dataRoot = Path.Combine(_root, "profile");
            Directory.CreateDirectory(dataRoot);
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, dataRoot);
            string source = Path.Combine(_root, "source");
            Directory.CreateDirectory(source);
            // Same placement as audiovisual_scraper.launch_browser.
            const string script = """
                import json, os, sys, tempfile, time
                data_root = os.environ.get('UNIVERSAL_MEDIA_OS_DATA_ROOT', '').strip()
                owned_root = os.environ.get('UMOS_AV_PROFILE_ROOT', '').strip()
                root = owned_root or (os.path.join(data_root, 'Local', 'UniversalMediaOS', 'BrowserProfiles')
                                      if data_root else tempfile.gettempdir())
                os.makedirs(root, exist_ok=True)
                profile = tempfile.mkdtemp(prefix='umos_av_', dir=root)
                with open(os.path.join(profile, 'Cookies'), 'w') as handle:
                    handle.write('browser state')
                with open(os.path.join(profile, 'pid'), 'w') as handle:
                    handle.write(str(os.getpid()))
                # 'finish' lookups end on their own after a short extraction; others run until stopped.
                time.sleep(2 if 'finish' in sys.argv[2] else 60)
                print(json.dumps({'error': 'all_sources_failed'}))
                """;
            File.WriteAllText(Path.Combine(source, "audiovisual_scraper.py"), script);
            foreach (string other in new[] { "scraper.py", "book_scraper.py" })
                File.WriteAllText(Path.Combine(source, other), "# unused preparation fixture\n");
            _python = new(new PreparedRuntime(), source, Path.Combine(_root, "services"), ["python.exe"], TimeSpan.FromSeconds(10));
            Engine = new(_python);
        }

        public string[] Profiles()
        {
            string browserProfiles = Path.Combine(_root, "profile", "Local", "UniversalMediaOS", "BrowserProfiles");
            return Directory.Exists(browserProfiles)
                ? Directory.GetDirectories(browserProfiles, "umos_av_*", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path).StartsWith("umos_av_", StringComparison.Ordinal) &&
                        !Path.GetFileName(path).StartsWith("umos_av_run_", StringComparison.Ordinal)).ToArray()
                : [];
        }

        public string[] Runs()
        {
            string browserProfiles = Path.Combine(_root, "profile", "Local", "UniversalMediaOS", "BrowserProfiles");
            return Directory.Exists(browserProfiles) ? Directory.GetDirectories(browserProfiles) : [];
        }

        public void Dispose()
        {
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
