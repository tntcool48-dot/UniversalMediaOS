using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    /// <summary>
    /// Manages the Python environment and the stateless scraper.py CLI tool.
    /// No server is started — the scraper is invoked as a fresh subprocess per request.
    ///
    /// scraper.py is shipped as a build Content item (CopyToOutputDirectory=Always)
    /// and copied to %LocalAppData%\UniversalMediaOS\Services\scraper.py on each boot.
    /// Always overwriting ensures scraper logic updates ship automatically with the app.
    /// </summary>
    public sealed class PythonBootstrapper : IDisposable
    {
        private readonly string _scraperDir;
        private static readonly SemaphoreSlim _pipLock = new SemaphoreSlim(1, 1);

        private static readonly (string Package, string ImportName)[] RequiredPackages =
        {
            ("drissionpage", "DrissionPage"),
            ("curl_cffi", "curl_cffi"),
            ("httpx", "httpx"),
            ("beautifulsoup4", "bs4"),
            ("lxml", "lxml")
        };

        public PythonBootstrapper()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _scraperDir = Path.Combine(appData, "UniversalMediaOS", "Services");
            Directory.CreateDirectory(_scraperDir);
        }

        // ── Public API ───────────────────────────────────────────────────────

        public bool IsAvailable =>
            ResolvePythonExecutable() != null && File.Exists(GetScraperPath());

        public string GetScraperPath() =>
            Path.Combine(_scraperDir, "scraper.py");

        /// <summary>
        /// Copies scraper.py from the application's output directory to AppData
        /// (always overwrites to pick up updates), then installs pip packages.
        /// </summary>
        public async Task EnsureScraperReadyAsync(CancellationToken token = default)
        {
            string destPath = GetScraperPath();

            // Source: shipped alongside the app binary as a Content item
            string appDir = AppContext.BaseDirectory;
            string srcPath = Path.Combine(appDir, "scraper.py");

            if (!File.Exists(srcPath))
            {
                AppLogger.Log($"[PythonBootstrapper] WARNING: scraper.py not found at {srcPath}. " +
                              "Scraper (Tier 1) will be unavailable.", "WARNING");
            }
            else
            {
                File.Copy(srcPath, destPath, overwrite: true);
                AppLogger.Log($"[PythonBootstrapper] scraper.py deployed to: {destPath}");
            }

            await RunPipInstallAsync(token);
        }

        /// <summary>
        /// Resolves the Python executable path. Returns null if Python is not installed.
        /// </summary>
        public string? ResolvePythonExecutable()
        {
            // On Windows, try python.exe, then py.exe (Python Launcher), then python3.exe
            string[] candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "python.exe", "python3.exe", "py.exe" }
                : new[] { "python3", "python" };

            foreach (var candidate in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(2000);
                    if (proc?.ExitCode == 0)
                    {
                        AppLogger.Log($"[PythonBootstrapper] Python resolved: {candidate}");
                        return candidate;
                    }
                }
                catch { }
            }

            AppLogger.Log("[PythonBootstrapper] Python not found on PATH.", "WARNING");
            return null;
        }

        // ── Pip Install ──────────────────────────────────────────────────────

        private async Task RunPipInstallAsync(CancellationToken token)
        {
            string? python = ResolvePythonExecutable();
            if (python == null)
            {
                AppLogger.Log("[PythonBootstrapper] Skipping pip install — Python not found.", "WARNING");
                return;
            }

            await _pipLock.WaitAsync(token);
            try
            {
                foreach (var requirement in RequiredPackages)
                {
                    token.ThrowIfCancellationRequested();
                    AppLogger.Log($"[PythonBootstrapper] Checking Python package: {requirement.Package}");

                    if (await IsPackageImportableAsync(python, requirement.ImportName, token))
                    {
                        AppLogger.Log($"[PythonBootstrapper] Package already available: {requirement.Package}");
                        continue;
                    }

                    AppLogger.Log($"[PythonBootstrapper] Installing missing pip package: {requirement.Package}");

                    var psi = new ProcessStartInfo
                    {
                        FileName = python,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    psi.ArgumentList.Add("-m");
                    psi.ArgumentList.Add("pip");
                    psi.ArgumentList.Add("install");
                    psi.ArgumentList.Add("--quiet");
                    psi.ArgumentList.Add(requirement.Package);

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        using var installCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        installCts.CancelAfter(TimeSpan.FromSeconds(60));

                        try
                        {
                            await proc.WaitForExitAsync(installCts.Token);
                            if (proc.ExitCode != 0)
                            {
                                string err = await proc.StandardError.ReadToEndAsync(token);
                                AppLogger.Log($"[PythonBootstrapper] pip install {requirement.Package} failed: {err}", "WARNING");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            try { proc.Kill(entireProcessTree: true); } catch { }
                            AppLogger.Log($"[PythonBootstrapper] pip install {requirement.Package} timed out or was cancelled.", "WARNING");
                        }
                    }
                }
                AppLogger.Log("[PythonBootstrapper] All pip packages verified.");
            }
            finally
            {
                _pipLock.Release();
            }
        }

        private static async Task<bool> IsPackageImportableAsync(
            string python,
            string importName,
            CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"import {importName}");

            using var proc = Process.Start(psi);
            if (proc == null)
                return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                await proc.WaitForExitAsync(cts.Token);
                return proc.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
        }

        // ── IDisposable ──────────────────────────────────────────────────────

        public void Dispose()
        {
        }
    }
}
