using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;

namespace UniversalMediaOS.Core.Services
{
    public class ServiceManager : IDisposable
    {
        private readonly List<Process> _managedProcesses = new List<Process>();

        public void StartService(string executablePath, string arguments, string workingDirectory)
        {
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

                var process = new Process { StartInfo = startInfo };
                
                process.OutputDataReceived += (sender, args) => 
                {
                    if (args.Data != null) Console.WriteLine($"[{Path.GetFileName(executablePath)}] {args.Data}");
                };
                process.ErrorDataReceived += (sender, args) => 
                {
                    if (args.Data != null) Console.WriteLine($"[{Path.GetFileName(executablePath)} ERROR] {args.Data}");
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _managedProcesses.Add(process);
                Console.WriteLine($"Started service: {executablePath} {arguments}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start service {executablePath}: {ex.Message}");
            }
        }

        public void StopAll()
        {
            foreach (var process in _managedProcesses)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to kill process: {ex.Message}");
                }
            }
            _managedProcesses.Clear();
        }

        public void Dispose()
        {
            StopAll();
        }
    }
}
