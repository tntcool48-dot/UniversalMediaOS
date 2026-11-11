using System.ComponentModel;
using System.IO;
using System.Net.Http;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Streaming;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

/// <summary>Services that stop while the app is running: Settings must say so, and Repair must bring them back.</summary>
public sealed class ServiceRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.ServiceRecoveryTests", Guid.NewGuid().ToString("N"));
    private readonly HlsLoopbackProxy _proxy = new();
    private readonly Runner _runner;
    private readonly PythonBootstrapper _python;
    private readonly SettingsViewModel _settings;

    public ServiceRecoveryTests()
    {
        string source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        foreach (string script in new[] { "scraper.py", "book_scraper.py", "audiovisual_scraper.py" })
            File.WriteAllText(Path.Combine(source, script), "print('fixture')");
        _runner = new Runner(Path.Combine(_root, "python", "python.exe"));
        _python = new PythonBootstrapper(_runner, source, Path.Combine(_root, "services"), ["python.exe"], TimeSpan.FromSeconds(10));
        var dependencies = new DependencyBootstrapper(_root, null, new HttpClient(new Unreachable()), TimeSpan.FromSeconds(2),
            Path.Combine(_root, "local"), _runner, () => string.Empty);
        var config = new DomainHotSwapper(Path.Combine(_root, "config.json"));
        _settings = new SettingsViewModel(config, new Dialogs(), new Launcher(), new Picker(), dependencies, _proxy,
            new MalOAuthService(config), new EpisodeAlertHistoryService(Path.Combine(_root, "alerts.json")), _python);
    }

    [Fact]
    public async Task RepairRestartsAnHlsProxyThatStoppedDuringUse()
    {
        _proxy.Start();
        Assert.True(_proxy.IsRunning);
        _proxy.Stop(); // Its listener is gone, as when the proxy dies under the running app.

        _settings.RefreshServiceHealthCommand.Execute(null);
        Assert.Contains("HLS proxy: Offline", _settings.ServiceHealthText, StringComparison.Ordinal);
        Assert.Equal("Offline", _settings.HlsProxyStatus);

        await _settings.RepairServicesCommand.ExecuteAsync(null);

        Assert.True(_proxy.IsRunning, _settings.ServiceHealthText);
        Assert.Contains("HLS proxy: Listening on " + _proxy.ListeningEndpoint, _settings.ServiceHealthText, StringComparison.Ordinal);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(_proxy.ListeningUri + "health-probe");
        Assert.True((int)response.StatusCode is >= 200 and < 600);
    }

    [Fact]
    public async Task APythonInterpreterRemovedAfterReadinessIsReportedAndRepairRecoversIt()
    {
        await _python.EnsureScraperReadyAsync();
        _settings.RefreshServiceHealthCommand.Execute(null);
        Assert.Contains("Python scraper: Ready (ready)", _settings.ServiceHealthText, StringComparison.Ordinal);
        Assert.Equal(_runner.Interpreter, _python.ResolvePythonExecutable());

        string away = _runner.Interpreter + ".away";
        File.Move(_runner.Interpreter, away); // Uninstalled or moved while the app runs.
        _settings.RefreshServiceHealthCommand.Execute(null);
        Assert.Contains("Python scraper: Failed (python_missing)", _settings.ServiceHealthText, StringComparison.Ordinal);
        Assert.False(_python.IsAvailable);
        Assert.Null(_python.ResolvePythonExecutable());

        // The next scraper use prepares again instead of launching the missing interpreter.
        await _python.EnsureScraperReadyAsync();
        Assert.Equal("python_missing", _python.Snapshot.DiagnosticCode);

        File.Move(away, _runner.Interpreter);
        await _settings.RepairServicesCommand.ExecuteAsync(null);
        Assert.Contains("Python scraper: Ready (ready)", _settings.ServiceHealthText, StringComparison.Ordinal);
        Assert.True(_python.IsAvailable);
    }

    public void Dispose()
    {
        _proxy.Dispose();
        _python.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A Python on disk that answers like a real one, and fails to start as Windows does once it is gone.</summary>
    private sealed class Runner : IPreparationProcessRunner
    {
        public Runner(string interpreter)
        {
            Interpreter = interpreter;
            Directory.CreateDirectory(Path.GetDirectoryName(interpreter)!);
            File.WriteAllText(interpreter, string.Empty);
        }

        public string Interpreter { get; }

        public Task<PreparationProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            if (!File.Exists(Interpreter)) throw new Win32Exception(2, "The system cannot find the file specified.");
            if (arguments[0] == "--version") return Task.FromResult(new PreparationProcessResult(0, "Python 3.12.10"));
            if (arguments[0] == "-m") return Task.FromResult(new PreparationProcessResult(0, string.Empty));
            if (arguments[1].StartsWith("import sys; print(sys.executable)", StringComparison.Ordinal))
                return Task.FromResult(new PreparationProcessResult(0, Interpreter + Environment.NewLine));
            if (arguments[1].StartsWith("import ast", StringComparison.Ordinal)) return Task.FromResult(new PreparationProcessResult(0, string.Empty));
            return Task.FromResult(new PreparationProcessResult(0, "[]"));
        }
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("No network in this test.");
    }

    private sealed class Dialogs : IDialogService
    {
        public (bool DialogResult, UniversalMediaOS.WPF.SelectedSourceTier SelectedTier) ShowSourceSelection() => (false, default);
        public bool ShowConfirmDialog(string message, string title) => false;
        public void ShowErrorDialog(string message, string title) { }
        public void ShowInfoDialog(string message, string title) { }
    }

    private sealed class Launcher : IExternalLauncher
    {
        public bool OpenUrl(string url) => false;
        public bool OpenFolder(string folderPath) => false;
        public bool OpenFile(string filePath) => false;
    }

    private sealed class Picker : IFolderPicker
    {
        public string? PickFolder(string title, string? initialDirectory = null) => null;
    }
}
