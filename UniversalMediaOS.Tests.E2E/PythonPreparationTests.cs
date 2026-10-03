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
        Assert.Equal(new[] { "-m", "pip", "install", "--quiet", "httpx" }, runner.InstallArguments);
        Assert.Equal(exitCode == 0 ? 2 : 1, runner.Probes);
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
             "[IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "', [string]$PID); Start-Sleep -Seconds 30"], cancellation.Token);
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
        public IReadOnlyList<string>? InstallArguments { get; private set; }
        public async Task<PreparationProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls++;
            if (arguments[0] == "--version") return new(0, "Python 3.12.0");
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
