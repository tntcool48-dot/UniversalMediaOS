using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public class PythonBootstrapper : IDisposable
    {
        private readonly string _pythonServiceDir;
        private Process? _pythonProcess;
        private bool _disposed;

        private static readonly SemaphoreSlim _bootLock = new SemaphoreSlim(1, 1);
        private static readonly HttpClient _healthCheckClient;

        static PythonBootstrapper()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15)
            };
            _healthCheckClient = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(400) };
        }

        public PythonBootstrapper(string baseDirectory)
        {
            _pythonServiceDir = Path.Combine(baseDirectory, "Scrapers", "PythonService");
        }

        public async Task<bool> BootPythonServiceAsync()
        {
            await _bootLock.WaitAsync();
            try
            {
                AppLogger.Log("Booting Python scrapers service...");
                
                // Idempotency check: check if already running and healthy
                if (await IsServerHealthyAsync())
                {
                    AppLogger.Log("[Python Bootstrapper] Python scraper service is already running and healthy.");
                    return true;
                }

                if (!Directory.Exists(_pythonServiceDir) || !File.Exists(Path.Combine(_pythonServiceDir, "main.py")))
                {
                    AppLogger.Log($"[Python Bootstrapper] main.py not found at {_pythonServiceDir}", "ERROR");
                    return false;
                }

                string pythonExe = ResolvePythonExecutable();
                AppLogger.Log($"[Python Bootstrapper] Using Python executable: {pythonExe}");

                AppLogger.Log("[Python Bootstrapper] Installing pip dependencies (fastapi uvicorn curl_cffi beautifulsoup4)...");
                bool pipOk = await RunPipInstallAsync(pythonExe);
                if (!pipOk)
                {
                    AppLogger.Log("[Python Bootstrapper] pip dependencies installation failed. Aborting startup.", "ERROR");
                    return false;
                }

                AppLogger.Log("[Python Bootstrapper] Starting FastAPI server on localhost:8000...");
                StopServer(); // Ensure any previously started process by this instance is stopped
                StartPythonServer(pythonExe);

                // Readiness probe/health check
                bool isUp = false;
                for (int attempt = 1; attempt <= 15; attempt++)
                {
                    if (_pythonProcess == null || _pythonProcess.HasExited)
                    {
                        AppLogger.Log("[Python Bootstrapper] Python process has exited or failed to start.", "ERROR");
                        break;
                    }

                    if (await IsServerHealthyAsync())
                    {
                        isUp = true;
                        AppLogger.Log($"[Python Bootstrapper] Python server is up and responding (attempt {attempt}).");
                        break;
                    }

                    await Task.Delay(500);
                }

                if (!isUp)
                {
                    AppLogger.Log("[Python Bootstrapper] Python server failed to respond within timeout.", "ERROR");
                    StopServer();
                    return false;
                }

                AppLogger.Log("[Python Bootstrapper] Python scraper service booted successfully.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Python Bootstrapper] Error booting service: {ex.Message}", "ERROR");
                return false;
            }
            finally
            {
                _bootLock.Release();
            }
        }

        private async Task<bool> IsServerHealthyAsync()
        {
            try
            {
                using var response = await _healthCheckClient.GetAsync("http://localhost:8000/");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private string ResolvePythonExecutable()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "python.exe";
            }
            else
            {
                return "python3";
            }
        }

        private async Task<bool> RunPipInstallAsync(string pythonExe)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = "-m pip install fastapi uvicorn curl_cffi beautifulsoup4",
                    WorkingDirectory = _pythonServiceDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.OutputDataReceived += (s, e) => { if (e.Data != null) AppLogger.Log($"[Python pip] {e.Data}"); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) AppLogger.Log($"[Python pip ERROR] {e.Data}", "WARNING"); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();
                
                AppLogger.Log($"[Python Bootstrapper] pip dependencies installation exited with code {process.ExitCode}");
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Python Bootstrapper] failed to run pip install: {ex.Message}", "ERROR");
                return false;
            }
        }

        private void StartPythonServer(string pythonExe)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = "main.py",
                    WorkingDirectory = _pythonServiceDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _pythonProcess = new Process { StartInfo = startInfo };
                _pythonProcess.OutputDataReceived += (s, e) => { if (e.Data != null) AppLogger.Log($"[Python Server] {e.Data}"); };
                _pythonProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) AppLogger.Log($"[Python Server ERROR] {e.Data}", "ERROR"); };

                _pythonProcess.Start();
                _pythonProcess.BeginOutputReadLine();
                _pythonProcess.BeginErrorReadLine();

                AppLogger.Log($"[Python Bootstrapper] python main.py process started. PID={_pythonProcess.Id}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Python Bootstrapper] failed to start python server: {ex.Message}", "ERROR");
            }
        }

        public void StopServer()
        {
            try
            {
                if (_pythonProcess != null)
                {
                    try
                    {
                        if (!_pythonProcess.HasExited)
                        {
                            AppLogger.Log($"Stopping Python scrapers service server (PID={_pythonProcess.Id})...");
                            _pythonProcess.Kill(entireProcessTree: true);
                            _pythonProcess.WaitForExit(3000);
                            AppLogger.Log("Python scrapers service server terminated.");
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                    {
                        AppLogger.Log($"Python scrapers process already exited or cannot be killed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error stopping Python scrapers service server: {ex.Message}", "WARNING");
            }
            finally
            {
                if (_pythonProcess != null)
                {
                    try { _pythonProcess.Dispose(); } catch { }
                    _pythonProcess = null;
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    StopServer();
                }
                _disposed = true;
            }
        }
    }
}
