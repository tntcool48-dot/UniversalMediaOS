using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

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
            try
            {
                if (!Directory.Exists(_pythonServiceDir) || !File.Exists(Path.Combine(_pythonServiceDir, "main.py")))
                {
                    System.Diagnostics.Debug.WriteLine($"[Python Bootstrapper] main.py not found at {_pythonServiceDir}");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine("[Python Bootstrapper] Installing pip dependencies...");
                await Task.Run(() => RunPipInstall());

                System.Diagnostics.Debug.WriteLine("[Python Bootstrapper] Starting FastAPI server on localhost:8000...");
                await Task.Run(() => StartPythonServer());

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Python Bootstrapper] Error: {ex.Message}");
                return false;
            }
        }

        private void RunPipInstall()
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
        }

        private void StartPythonServer()
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
        }

        public void StopServer()
        {
            try
            {
                if (_pythonProcess != null && !_pythonProcess.HasExited)
                {
                    _pythonProcess.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }
    }
}
