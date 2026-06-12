using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public class PythonBootstrapper
    {
        private readonly string _pythonServiceDir;
        private Process? _pythonProcess;

        public PythonBootstrapper(string baseDirectory)
        {
            _pythonServiceDir = Path.Combine(baseDirectory, "Scrapers", "PythonService");
        }

        public async Task<bool> BootPythonServiceAsync()
        {
            AppLogger.Log("Booting Python scrapers service...");
            try
            {
                if (!Directory.Exists(_pythonServiceDir) || !File.Exists(Path.Combine(_pythonServiceDir, "main.py")))
                {
                    AppLogger.Log($"[Python Bootstrapper] main.py not found at {_pythonServiceDir}", "ERROR");
                    return false;
                }

                AppLogger.Log("[Python Bootstrapper] Installing pip dependencies (fastapi uvicorn curl_cffi beautifulsoup4)...");
                await Task.Run(() => RunPipInstall());

                AppLogger.Log("[Python Bootstrapper] Starting FastAPI server on localhost:8000...");
                await Task.Run(() => StartPythonServer());

                AppLogger.Log("[Python Bootstrapper] Python scraper service booted successfully.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Python Bootstrapper] Error booting service: {ex.Message}", "ERROR");
                return false;
            }
        }

        private void RunPipInstall()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c pip install fastapi uvicorn curl_cffi beautifulsoup4",
                    WorkingDirectory = _pythonServiceDir,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(startInfo);
                process?.WaitForExit();
                AppLogger.Log($"[Python Bootstrapper] pip dependencies installation exited with code {process?.ExitCode}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Python Bootstrapper] failed to run pip install: {ex.Message}", "ERROR");
            }
        }

        private void StartPythonServer()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c python main.py",
                    WorkingDirectory = _pythonServiceDir,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                _pythonProcess = Process.Start(startInfo);
                AppLogger.Log($"[Python Bootstrapper] python main.py process started. PID={_pythonProcess?.Id}");
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
                if (_pythonProcess != null && !_pythonProcess.HasExited)
                {
                    AppLogger.Log($"Stopping Python scrapers service server (PID={_pythonProcess.Id})...");
                    _pythonProcess.Kill(entireProcessTree: true);
                    AppLogger.Log("Python scrapers service server terminated.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error stopping Python scrapers service server: {ex.Message}", "WARNING");
            }
        }
    }
}
