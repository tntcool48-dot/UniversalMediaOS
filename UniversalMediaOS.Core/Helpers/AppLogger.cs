using System;
using System.IO;
using System.Text;

namespace UniversalMediaOS.Core.Helpers
{
    public static class AppLogger
    {
        private static string _logFilePath = string.Empty;
        private static readonly object _lock = new object();
        
        public static bool IsEnabled { get; set; } = true;

        public static void Initialize(string appDataDir)
        {
            try
            {
                Directory.CreateDirectory(appDataDir);
                _logFilePath = Path.Combine(appDataDir, "app.log");

                // Auto-clean on startup if log exceeds 5MB
                lock (_lock)
                {
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize AppLogger: {ex.Message}");
            }
        }

        public static void Log(string message, string level = "INFO")
        {
            if (!IsEnabled || string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                lock (_lock)
                {
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level.ToUpper()}] {message}\n";
                    File.AppendAllText(_logFilePath, logLine, Encoding.UTF8);
                    System.Diagnostics.Debug.Write(logLine);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write to log file: {ex.Message}");
            }
        }

        public static string GetLogFileSize()
        {
            if (string.IsNullOrEmpty(_logFilePath) || !File.Exists(_logFilePath))
            {
                return "0 KB";
            }

            try
            {
                lock (_lock)
                {
                    var fi = new FileInfo(_logFilePath);
                    double len = fi.Length;
                    if (len > 1024 * 1024)
                    {
                        return $"{len / (1024 * 1024.0):F2} MB";
                    }
                    return $"{len / 1024.0:F1} KB";
                }
            }
            catch
            {
                return "Unknown";
            }
        }

        public static void ClearLog()
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                lock (_lock)
                {
                    File.WriteAllText(_logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] Log manually cleared.\n");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear log file: {ex.Message}");
            }
        }
    }
}
