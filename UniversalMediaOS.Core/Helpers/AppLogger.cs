using System;
using System.IO;
using System.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Helpers
{
    public static class AppLogger
    {
        private static string _logFilePath = string.Empty;
        private static readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private static readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private static readonly SemaphoreSlim _fileSemaphore = new SemaphoreSlim(1, 1);
        private static Task? _loggingTask;
        private static CancellationTokenSource? _cts;
        private static bool _disposed;

        public static event EventHandler? LogChanged;
        
        public static bool IsEnabled { get; set; } = true;

        private static void OnLogChanged()
        {
            LogChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void Initialize(string appDataDir)
        {
            if (string.IsNullOrWhiteSpace(appDataDir))
            {
                throw new ArgumentException("Log directory path cannot be null or empty.", nameof(appDataDir));
            }

            try
            {
                Directory.CreateDirectory(appDataDir);
                _logFilePath = Path.Combine(appDataDir, "app.log");

                _fileSemaphore.Wait();
                try
                {
                    // Auto-clean on startup if log exceeds 5MB
                    if (File.Exists(_logFilePath))
                    {
                        var fi = new FileInfo(_logFilePath);
                        if (fi.Length > 5 * 1024 * 1024)
                        {
                            File.WriteAllText(_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] Log cleared due to size constraints (exceeded 5MB).\n");
                        }
                    }
                    else
                    {
                        File.WriteAllText(_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] Logger Initialized.\n");
                    }
                }
                finally
                {
                    _fileSemaphore.Release();
                }

                // Start background logging task
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _loggingTask = Task.Run(() => ProcessLogQueueAsync(_cts.Token));
                
                OnLogChanged();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize AppLogger: {ex.Message}");
                throw;
            }
        }

        private static async Task ProcessLogQueueAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(token);
                    
                    var linesToWrite = new List<string>();
                    while (_logQueue.TryDequeue(out var line))
                    {
                        linesToWrite.Add(line);
                    }

                    if (linesToWrite.Count > 0 && !string.IsNullOrEmpty(_logFilePath))
                    {
                        var sb = new StringBuilder();
                        foreach (var l in linesToWrite)
                        {
                            sb.Append(l);
                        }

                        await _fileSemaphore.WaitAsync(token);
                        try
                        {
                            await File.AppendAllTextAsync(_logFilePath, sb.ToString(), Encoding.UTF8, token);
                        }
                        finally
                        {
                            _fileSemaphore.Release();
                        }

                        OnLogChanged();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error writing logs in background: {ex.Message}");
                }
            }
        }

        public static void Log(string message, string level = "INFO")
        {
            if (!IsEnabled || message == null) return;
            if (string.IsNullOrWhiteSpace(level)) level = "INFO";

            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level.ToUpper()}] {message}\n";
            System.Diagnostics.Debug.Write(logLine);

            _logQueue.Enqueue(logLine);
            _signal.Release();
        }

        public static string GetLogFileSize()
        {
            if (string.IsNullOrEmpty(_logFilePath) || !File.Exists(_logFilePath))
            {
                return "0 KB";
            }

            try
            {
                _fileSemaphore.Wait();
                try
                {
                    var fi = new FileInfo(_logFilePath);
                    long len = fi.Length;
                    if (len >= 1024 * 1024)
                    {
                        return $"{len / (1024 * 1024.0):F2} MB";
                    }
                    return $"{len / 1024.0:F1} KB";
                }
                finally
                {
                    _fileSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading log size: {ex.Message}");
                return "Unknown";
            }
        }

        public static void ClearLog()
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                _fileSemaphore.Wait();
                try
                {
                    File.WriteAllText(_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] Log manually cleared.\n");
                }
                finally
                {
                    _fileSemaphore.Release();
                }
                OnLogChanged();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear log file: {ex.Message}");
            }
        }

        public static void Shutdown()
        {
            if (_disposed) return;
            _cts?.Cancel();
            try
            {
                _loggingTask?.GetAwaiter().GetResult();
            }
            catch { }
            
            // Flush any remaining items in queue synchronously
            if (!_logQueue.IsEmpty && !string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    _fileSemaphore.Wait();
                    try
                    {
                        var sb = new StringBuilder();
                        while (_logQueue.TryDequeue(out var line))
                        {
                            sb.Append(line);
                        }
                        File.AppendAllText(_logFilePath, sb.ToString(), Encoding.UTF8);
                    }
                    finally
                    {
                        _fileSemaphore.Release();
                    }
                }
                catch { }
            }

            _disposed = true;
        }
    }
}
