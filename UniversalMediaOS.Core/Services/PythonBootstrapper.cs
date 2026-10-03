using System.Security.Cryptography;
using System.Text.Json;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services;

public enum PythonPreparationState { NotStarted, Preparing, Ready, Failed, Canceled }
public sealed record PythonPreparationSnapshot(PythonPreparationState State, string DiagnosticCode,
    IReadOnlyList<string> MissingImports);

public sealed class PythonPreparationException(PythonPreparationSnapshot snapshot)
    : InvalidOperationException("Python preparation is not ready: " + snapshot.DiagnosticCode)
{
    public PythonPreparationSnapshot Snapshot { get; } = snapshot;
}

public sealed class PythonBootstrapper : IDisposable
{
    private readonly object _gate = new();
    private readonly string _scraperDir;
    private readonly string _sourceDir;
    private readonly IPreparationProcessRunner _runner;
    private readonly IReadOnlyList<string> _candidates;
    private readonly TimeSpan _budget;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _preparationCts;
    private Task? _preparationTask;
    private string? _resolvedPython;
    private string? _readyRevision;
    private bool _disposed;
    private bool _cancelRequested;
    private PythonPreparationSnapshot _snapshot = new(PythonPreparationState.NotStarted, "not_started", []);
    private static readonly string[] Scripts = ["scraper.py", "book_scraper.py", "audiovisual_scraper.py"];
    private static readonly (string Package, string Import)[] Packages =
    [ ("drissionpage", "DrissionPage"), ("curl_cffi", "curl_cffi"), ("httpx", "httpx"), ("beautifulsoup4", "bs4"), ("lxml", "lxml") ];

    public PythonBootstrapper() : this(new PreparationProcessRunner(), AppContext.BaseDirectory,
        Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "Services"),
        OperatingSystem.IsWindows() ? ["python.exe", "python3.exe", "py.exe"] : ["python3", "python"], TimeSpan.FromMinutes(2)) { }

    internal PythonBootstrapper(IPreparationProcessRunner runner, string sourceDir, string scraperDir,
        IReadOnlyList<string> candidates, TimeSpan budget)
    {
        _runner = runner; _sourceDir = sourceDir; _scraperDir = scraperDir; _candidates = candidates; _budget = budget;
        Directory.CreateDirectory(scraperDir);
    }

    public event EventHandler? StateChanged;
    public PythonPreparationSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool IsAvailable => Snapshot.State == PythonPreparationState.Ready && File.Exists(GetScraperPath());
    public bool IsBookScraperAvailable => Snapshot.State == PythonPreparationState.Ready && File.Exists(GetBookScraperPath());
    public bool IsAudiovisualScraperAvailable => Snapshot.State == PythonPreparationState.Ready && File.Exists(GetAudiovisualScraperPath());
    public string GetScraperPath() => Path.Combine(_scraperDir, Scripts[0]);
    public string GetBookScraperPath() => Path.Combine(_scraperDir, Scripts[1]);
    public string GetAudiovisualScraperPath() => Path.Combine(_scraperDir, Scripts[2]);
    // Compatibility accessor: health checks no longer launch synchronous Python probes.
    public string? ResolvePythonExecutable() => Snapshot.State == PythonPreparationState.Ready ? _resolvedPython : null;

    public Task EnsureScraperReadyAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preparationTask is { IsCompleted: false }) return _preparationTask.WaitAsync(token);
            string revision = Revision();
            if (_snapshot.State == PythonPreparationState.Ready && _readyRevision == revision &&
                Scripts.All(s => File.Exists(Path.Combine(_scraperDir, s)))) return Task.CompletedTask;
            _preparationCts?.Dispose();
            _preparationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _preparationCts.CancelAfter(_budget);
            _cancelRequested = false;
            var owner = _preparationCts;
            _preparationTask = Task.Run(() => PrepareAsync(revision, owner.Token));
            // A canceled waiter does not cancel work needed by another caller.
            return _preparationTask.WaitAsync(token);
        }
    }

    public void CancelPreparation() { lock (_gate) { _cancelRequested = true; _preparationCts?.Cancel(); } }

    private async Task PrepareAsync(string revision, CancellationToken token)
    {
        SetState(PythonPreparationState.Preparing, "preparing", []);
        try
        {
            _resolvedPython = null;
            if (Scripts.Any(s => !File.Exists(Path.Combine(_sourceDir, s))))
            { SetState(PythonPreparationState.Failed, "scripts_missing", []); return; }
            foreach (string candidate in _candidates)
            {
                token.ThrowIfCancellationRequested();
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                probe.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var result = await _runner.RunAsync(candidate, ["--version"], probe.Token).ConfigureAwait(false);
                    if (result.ExitCode == 0 && result.Output.TrimStart().StartsWith("Python 3.", StringComparison.Ordinal))
                    { _resolvedPython = candidate; break; }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (IOException) { }
            }
            if (_resolvedPython == null) { SetState(PythonPreparationState.Failed, "python_missing", []); return; }
            foreach (string script in Scripts)
            {
                token.ThrowIfCancellationRequested();
                File.Copy(Path.Combine(_sourceDir, script), Path.Combine(_scraperDir, script), overwrite: true);
            }
            string[] missing = await ProbeImportsAsync(_resolvedPython, token).ConfigureAwait(false);
            SetState(PythonPreparationState.Preparing, "checking_imports", missing);
            if (missing.Length > 0)
            {
                string[] install = ["-m", "pip", "install", "--quiet", .. Packages.Where(p => missing.Contains(p.Import)).Select(p => p.Package)];
                var result = await _runner.RunAsync(_resolvedPython, install, token).ConfigureAwait(false);
                if (result.ExitCode != 0) { SetState(PythonPreparationState.Failed, "pip_failed", missing); return; }
                missing = await ProbeImportsAsync(_resolvedPython, token).ConfigureAwait(false);
            }
            if (missing.Length > 0) { SetState(PythonPreparationState.Failed, "imports_missing", missing); return; }
            // Parse all deployed scripts without executing network/browser work or writing pyc files.
            var syntax = await _runner.RunAsync(_resolvedPython,
                ["-c", "import ast,sys; [ast.parse(open(p,encoding='utf-8-sig').read(),filename=p) for p in sys.argv[1:]]",
                 .. Scripts.Select(s => Path.Combine(_scraperDir, s))], token).ConfigureAwait(false);
            if (syntax.ExitCode != 0) { SetState(PythonPreparationState.Failed, "script_validation_failed", []); return; }
            token.ThrowIfCancellationRequested();
            _readyRevision = revision;
            SetState(PythonPreparationState.Ready, "ready", []);
        }
        catch (OperationCanceledException)
        {
            bool canceled = _lifetime.IsCancellationRequested || _cancelRequested;
            SetState(canceled ? PythonPreparationState.Canceled : PythonPreparationState.Failed,
                canceled ? "preparation_canceled" : "preparation_timed_out", Snapshot.MissingImports);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Python preparation failed: {ex.GetType().Name}", "WARNING");
            SetState(PythonPreparationState.Failed, "preparation_failed", Snapshot.MissingImports);
        }
    }

    private async Task<string[]> ProbeImportsAsync(string python, CancellationToken token)
    {
        const string probe = "import importlib,json,sys\nmissing=[]\nfor name in sys.argv[1:]:\n try: importlib.import_module(name)\n except Exception: missing.append(name)\nprint(json.dumps(missing))";
        var result = await _runner.RunAsync(python, ["-c", probe, .. Packages.Select(p => p.Import)], token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidDataException("Import probe failed.");
        string[] missing = JsonSerializer.Deserialize<string[]>(result.Output.Trim()) ?? throw new InvalidDataException("Import probe returned no result.");
        if (missing.Any(name => !Packages.Any(p => p.Import == name))) throw new InvalidDataException("Unknown import result.");
        return missing.Distinct().ToArray();
    }

    private string Revision() => string.Join("|", Scripts.Select(s =>
    {
        string path = Path.Combine(_sourceDir, s);
        return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    }));

    private void SetState(PythonPreparationState state, string code, IReadOnlyList<string> missing)
    {
        lock (_gate) _snapshot = new(state, code, Array.AsReadOnly(missing.ToArray()));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            var running = _preparationTask ?? Task.CompletedTask;
            _ = running.ContinueWith(_ => { _preparationCts?.Dispose(); _lifetime.Dispose(); }, TaskScheduler.Default);
        }
    }
}
