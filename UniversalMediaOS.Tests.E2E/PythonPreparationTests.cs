using System.IO;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class PythonPreparationTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(" FALSE ", false)]
    public void StartupAndSettingsShareTheSameDefault(string? setting, bool expected) =>
        Assert.Equal(expected, ServiceStartupPolicy.AutoManageEnabled(setting));

    [Fact]
    public async Task SharedPreparationSurvivesCanceledWaiterAndCachesSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return "[]"; });
        using var fixture = new Fixture(runner);
        using var caller = new CancellationTokenSource();
        Task first = fixture.Python.EnsureScraperReadyAsync(caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = fixture.Python.EnsureScraperReadyAsync();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        await second;
        Assert.Equal(PythonPreparationState.Ready, fixture.Python.Snapshot.State);
        Assert.True(fixture.Python.IsAvailable);
        Assert.Equal(1, runner.Probes);
        await fixture.Python.EnsureScraperReadyAsync();
        Assert.Equal(1, runner.Probes);
        File.AppendAllText(Path.Combine(fixture.Source, "scraper.py"), "\n# revision");
        await fixture.Python.EnsureScraperReadyAsync();
        Assert.Equal(2, runner.Probes);
    }

    [Theory]
    [InlineData(1, "pip_failed")]
    [InlineData(0, "imports_missing")]
    public async Task FailedInstallationOrFailedRecheckNeverReportsReady(int exitCode, string code)
    {
        var runner = new FakeRunner((_, _) => Task.FromResult("[\"httpx\"]")) { PipExit = exitCode };
        using var fixture = new Fixture(runner);
        await fixture.Python.EnsureScraperReadyAsync();
        Assert.Equal(PythonPreparationState.Failed, fixture.Python.Snapshot.State);
        Assert.Equal(code, fixture.Python.Snapshot.DiagnosticCode);
        Assert.False(fixture.Python.IsAvailable);
        Assert.Null(fixture.Python.ResolvePythonExecutable());
        Assert.Equal(new[] { "-m", "pip", "install", "--quiet", "httpx==0.28.1" }, runner.InstallArguments);
        Assert.Equal(exitCode == 0 ? 2 : 1, runner.Probes);
    }

    [Fact]
    public async Task InstallPinsMatchTheVersionsReleaseValidationTests()
    {
        var runner = new FakeRunner((_, _) => Task.FromResult("[\"DrissionPage\",\"curl_cffi\",\"httpx\",\"bs4\",\"lxml\"]")) { PipExit = 1 };
        using var fixture = new Fixture(runner);
        await fixture.Python.EnsureScraperReadyAsync();
        string[] pins = runner.InstallArguments!.Skip(4).ToArray();
        Assert.Equal(5, pins.Length);
        Assert.All(pins, pin => Assert.Matches(@"^[\w.-]+==\d+(\.\d+)+$", pin));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "UniversalMediaOS.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        string[] workflow = File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "release-validation.yml"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.All(pins, pin => Assert.Contains(pin, workflow));
    }

    [Theory]
    [InlineData("scraper.py")]
    [InlineData("book_scraper.py")]
    [InlineData("audiovisual_scraper.py")]
    public async Task ChangedDeployedScriptCannotReuseCachedReadiness(string script)
    {
        var runner = new FakeRunner((_, _) => Task.FromResult("[]"));
        using var fixture = new Fixture(runner);
        await fixture.Python.EnsureScraperReadyAsync();
        string deployed = Path.Combine(Path.GetDirectoryName(fixture.Python.GetScraperPath())!, script);
        File.WriteAllText(deployed, "def broken(:\n");

        await fixture.Python.EnsureScraperReadyAsync();

        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Source, script)), File.ReadAllBytes(deployed));
        Assert.Equal(2, runner.Probes);
        Assert.Equal(PythonPreparationState.Ready, fixture.Python.Snapshot.State);
        Assert.True(fixture.Python.IsAvailable);
    }

    [Fact]
    public async Task LockedDeployedScriptReportsPreparationFailureAndRecoversAfterRelease()
    {
        var runner = new FakeRunner((_, _) => Task.FromResult("[]"));
        using var fixture = new Fixture(runner);
        await fixture.Python.EnsureScraperReadyAsync();
        using (var ownedLock = new FileStream(fixture.Python.GetScraperPath(), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            await fixture.Python.EnsureScraperReadyAsync();
            Assert.Equal(PythonPreparationState.Failed, fixture.Python.Snapshot.State);
            Assert.Equal("preparation_failed", fixture.Python.Snapshot.DiagnosticCode);
            Assert.False(fixture.Python.IsAvailable);
        }
        await fixture.Python.EnsureScraperReadyAsync();
        Assert.True(fixture.Python.IsAvailable);
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Source, "scraper.py")),
            File.ReadAllBytes(fixture.Python.GetScraperPath()));
    }

    [Fact]
    public async Task RetryRecoversInSameSessionAndRechecksInstalledImports()
    {
        var runner = new FakeRunner((probe, _) => Task.FromResult(probe < 3 ? "[\"httpx\"]" : "[]")) { PipExit = 1 };
        using var fixture = new Fixture(runner);
        await fixture.Python.EnsureScraperReadyAsync();
        runner.PipExit = 0;
        await fixture.Python.EnsureScraperReadyAsync();
        Assert.True(fixture.Python.IsAvailable);
        Assert.Equal(3, runner.Probes);
    }

    [Theory]
    [InlineData(true, "python_missing")]
    [InlineData(false, "pip_failed")]
    public async Task ExplicitRepairRechecksRuntimeAndImportsAfterWarmReadiness(bool runtimeMissing, string code)
    {
        bool importsReady = true;
        var runner = new FakeRunner((_, _) => Task.FromResult(importsReady ? "[]" : "[\"httpx\"]"));
        using var fixture = new Fixture(runner);
        await fixture.Python.EnsureScraperReadyAsync();
        if (runtimeMissing) runner.VersionOutput = "Python 2.7.0";
        else { importsReady = false; runner.PipExit = 1; }

        await fixture.Python.EnsureScraperReadyAsync(forceRefresh: true);

        Assert.Equal(PythonPreparationState.Failed, fixture.Python.Snapshot.State);
        Assert.Equal(code, fixture.Python.Snapshot.DiagnosticCode);
        Assert.False(fixture.Python.IsAvailable);
        Assert.Null(fixture.Python.ResolvePythonExecutable());

        runner.VersionOutput = "Python 3.12.0";
        importsReady = true;
        await fixture.Python.EnsureScraperReadyAsync(forceRefresh: true);
        Assert.Equal(PythonPreparationState.Ready, fixture.Python.Snapshot.State);
        Assert.True(fixture.Python.IsAvailable);
    }

    [Fact]
    public async Task ForcedRepairSharesFreshWorkAndDoesNotCancelAnotherWaiter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (probe, token) =>
        {
            if (probe > 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return "[]";
        });
        using var fixture = new Fixture(runner);
        await fixture.Python.EnsureScraperReadyAsync();

        Task repair = fixture.Python.EnsureScraperReadyAsync(forceRefresh: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PythonPreparationState.Preparing, fixture.Python.Snapshot.State);
        Assert.False(fixture.Python.IsAvailable);
        Task ordinaryWaiter = fixture.Python.EnsureScraperReadyAsync();
        using var canceled = new CancellationTokenSource();
        Task canceledRepair = fixture.Python.EnsureScraperReadyAsync(canceled.Token, forceRefresh: true);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRepair);
        release.TrySetResult();
        await Task.WhenAll(repair, ordinaryWaiter).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fixture.Python.IsAvailable);
        Assert.Equal(2, runner.Probes);
        await fixture.Python.EnsureScraperReadyAsync();
        Assert.Equal(2, runner.Probes);
    }

    [Fact]
    public async Task MissingScriptsFailWithoutLaunchingOrInstalling()
    {
        var runner = new FakeRunner((_, _) => Task.FromResult("[]"));
        using var fixture = new Fixture(runner);
        File.Delete(Path.Combine(fixture.Source, "scraper.py"));
        await fixture.Python.EnsureScraperReadyAsync();
        Assert.Equal("scripts_missing", fixture.Python.Snapshot.DiagnosticCode);
        Assert.False(fixture.Python.IsAvailable);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task OwnedBudgetStopsPreparationAndDoesNotBecomeReady()
    {
        var runner = new FakeRunner(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return "[]"; });
        using var fixture = new Fixture(runner, TimeSpan.FromMilliseconds(100));
        await fixture.Python.EnsureScraperReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("preparation_timed_out", fixture.Python.Snapshot.DiagnosticCode);
        Assert.False(fixture.Python.IsAvailable);
    }

    [Fact]
    public async Task ExplicitCancelStopsSharedWorkAndHealthGettersDoNotProbe()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return "[]"; });
        using var fixture = new Fixture(runner);
        Assert.False(fixture.Python.IsAvailable);
        Assert.Null(fixture.Python.ResolvePythonExecutable());
        Assert.Equal(0, runner.Calls);
        Task task = fixture.Python.EnsureScraperReadyAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Python.CancelPreparation();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PythonPreparationState.Canceled, fixture.Python.Snapshot.State);
    }

    [Fact]
    public async Task ProcessRunnerDrainsBothSaturatedPipesWithBoundedCapture()
    {
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await new PreparationProcessRunner().RunAsync(shell,
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('x' * 100000); [Console]::Error.Write('y' * 100000)"], token.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(65536, result.Output.Length);
    }

    [Fact]
    public async Task ProcessCancellationTerminatesTheOwnedProcess()
    {
        using var fixture = new Fixture(new FakeRunner((_, _) => Task.FromResult("[]")));
        string pidFile = Path.Combine(fixture.Source, "owned-process.pid");
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task task = new PreparationProcessRunner().RunAsync(shell,
            ["-NoProfile", "-NonInteractive", "-Command",
             "[IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + ".part', [string]$PID); " +
             "[IO.File]::Move('" + pidFile.Replace("'", "''") + ".part', '" + pidFile.Replace("'", "''") + "'); Start-Sleep -Seconds 30"], cancellation.Token);
        try
        {
            while (!File.Exists(pidFile)) await Task.Delay(20, cancellation.Token);
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile, cancellation.Token));
            using var owned = System.Diagnostics.Process.GetProcessById(pid);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(owned.WaitForExit(2000), "Owned process remained alive after cancellation.");
        }
        finally
        {
            cancellation.Cancel();
            try { await task; } catch (OperationCanceledException) { }
        }
    }

    private sealed class FakeRunner(Func<int, CancellationToken, Task<string>> probe) : IPreparationProcessRunner
    {
        public int Probes { get; private set; }
        public int Calls { get; private set; }
        public int PipExit { get; set; }
        public string VersionOutput { get; set; } = "Python 3.12.0";
        public IReadOnlyList<string>? InstallArguments { get; private set; }
        public async Task<PreparationProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls++;
            if (arguments[0] == "--version") return new(0, VersionOutput);
            if (arguments[0] == "-m") { InstallArguments = arguments.ToArray(); return new(PipExit, ""); }
            if (arguments[1].StartsWith("import ast", StringComparison.Ordinal)) return new(0, "");
            return new(0, await probe(++Probes, token));
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.PreparationTests", Guid.NewGuid().ToString("N"));
        public string Source { get; }
        public PythonBootstrapper Python { get; }
        public Fixture(IPreparationProcessRunner runner, TimeSpan? budget = null)
        {
            Source = Path.Combine(_root, "source"); Directory.CreateDirectory(Source);
            foreach (string script in new[] { "scraper.py", "book_scraper.py", "audiovisual_scraper.py" }) File.WriteAllText(Path.Combine(Source, script), "print('fixture')");
            Python = new(runner, Source, Path.Combine(_root, "services"), ["fixture-python"], budget ?? TimeSpan.FromSeconds(10));
        }
        public void Dispose()
        {
            Python.Dispose();
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.PreparationTests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
        }
    }
}
