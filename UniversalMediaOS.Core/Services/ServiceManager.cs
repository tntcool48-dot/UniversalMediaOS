using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public class ServiceManager : IDisposable
    {
        private readonly List<Process> _managedProcesses = new List<Process>();
        private readonly object _lock = new object();
        private bool _disposed;

        public void StartService(string executablePath, string arguments, string workingDirectory)
        {
            Process? process = null;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                process = new Process { StartInfo = startInfo };
                process.EnableRaisingEvents = true;

                string procName = Path.GetFileName(executablePath);

                process.OutputDataReceived += (sender, args) => 
                {
                    if (args.Data != null) AppLogger.Log($"[{procName}] {args.Data}");
                };
                process.ErrorDataReceived += (sender, args) => 
                {
                    if (args.Data != null) AppLogger.Log($"[{procName} ERROR] {args.Data}", "ERROR");
                };

                process.Exited += (sender, args) =>
                {
                    lock (_lock)
                    {
                        _managedProcesses.Remove(process);
                    }
                    try { process.Dispose(); } catch {}
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                lock (_lock)
                {
                    _managedProcesses.Add(process);
                }
                AppLogger.Log($"Started service: {executablePath} {arguments}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to start service {executablePath}: {ex.Message}", "ERROR");
                process?.Dispose();
            }
        }

        public async Task StartServiceAsync(string executablePath, string arguments, string workingDirectory)
        {
            await Task.Run(() => StartService(executablePath, arguments, workingDirectory));
        }

        public void StopAll()
        {
            List<Process> processesToStop;
            lock (_lock)
            {
                processesToStop = new List<Process>(_managedProcesses);
                _managedProcesses.Clear();
            }

            foreach (var process in processesToStop)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Failed to kill process: {ex.Message}", "WARNING");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        public async Task StopAllAsync()
        {
            List<Process> processesToStop;
            lock (_lock)
            {
                processesToStop = new List<Process>(_managedProcesses);
                _managedProcesses.Clear();
            }

            var tasks = new List<Task>();
            foreach (var process in processesToStop)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            await process.WaitForExitAsync(cts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"Failed to kill process: {ex.Message}", "WARNING");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }));
            }
            await Task.WhenAll(tasks);
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
                    StopAll();
                }
                _disposed = true;
            }
        }
    }
}
