using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Services
{
    public class SystemResourceCheck
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("libc", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int sysctlbyname(string name, out ulong oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);

        private readonly object _lock = new object();
        private bool _isReady;
        private string _statusMessage = "Not checked yet.";
        private readonly double _minAvailableRamMB;

        public bool IsReady
        {
            get { lock (_lock) return _isReady; }
            private set { lock (_lock) _isReady = value; }
        }

        public string StatusMessage
        {
            get { lock (_lock) return _statusMessage; }
            private set { lock (_lock) _statusMessage = value; }
        }

        public SystemResourceCheck() : this(512.0)
        {
        }

        public SystemResourceCheck(double minAvailableRamMB)
        {
            _minAvailableRamMB = minAvailableRamMB;
        }

        public void RunCheck()
        {
            var result = PerformStartupCheck(_minAvailableRamMB);
            IsReady = result.IsReady;
            StatusMessage = result.Message;
        }

        public async Task RunCheckAsync()
        {
            var result = await PerformStartupCheckAsync(_minAvailableRamMB);
            IsReady = result.IsReady;
            StatusMessage = result.Message;
        }

        public static (bool IsReady, string Message) PerformStartupCheck(double minAvailableRamMB = 512.0)
        {
            try
            {
                double availableMB;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var memStatus = new MEMORYSTATUSEX();
                    memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));

                    if (!GlobalMemoryStatusEx(ref memStatus))
                    {
                        int err = Marshal.GetLastWin32Error();
                        return (false, $"Failed to query system memory (GlobalMemoryStatusEx returned false, Error Code: {err}).");
                    }
                    availableMB = memStatus.ullAvailPhys / (1024.0 * 1024.0);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var linuxMem = GetLinuxAvailableMemoryMB();
                    if (!linuxMem.HasValue)
                    {
                        availableMB = GetGcFallbackAvailableMemoryMB();
                    }
                    else
                    {
                        availableMB = linuxMem.Value;
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var macMem = GetMacAvailableMemoryMB();
                    if (!macMem.HasValue)
                    {
                        availableMB = GetGcFallbackAvailableMemoryMB();
                    }
                    else
                    {
                        availableMB = macMem.Value;
                    }
                }
                else
                {
                    availableMB = GetGcFallbackAvailableMemoryMB();
                }

                double availableGB = availableMB / 1024.0;

                if (availableMB < minAvailableRamMB)
                {
                    return (false, $"Low memory — only {availableGB:F2} GB ({availableMB:F0} MB) available. At least {minAvailableRamMB:F0} MB required.");
                }

                return (true, $"RAM: {availableGB:F1} GB available");
            }
            catch (Exception ex)
            {
                return (false, $"System Check Failed: {ex.Message}");
            }
        }

        public static async Task<(bool IsReady, string Message)> PerformStartupCheckAsync(double minAvailableRamMB = 512.0)
        {
            return await Task.Run(() => PerformStartupCheck(minAvailableRamMB));
        }

        private static double? GetLinuxAvailableMemoryMB()
        {
            try
            {
                if (File.Exists("/proc/meminfo"))
                {
                    string[] lines = File.ReadAllLines("/proc/meminfo");
                    long availableKb = 0;
                    long freeKb = 0;
                    long buffersKb = 0;
                    long cachedKb = 0;
                    bool foundAvailable = false;

                    foreach (var line in lines)
                    {
                        var parts = line.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;

                        string key = parts[0];
                        string valStr = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                        if (long.TryParse(valStr, out long kb))
                        {
                            if (key.Equals("MemAvailable", StringComparison.OrdinalIgnoreCase))
                            {
                                availableKb = kb;
                                foundAvailable = true;
                                break;
                            }
                            else if (key.Equals("MemFree", StringComparison.OrdinalIgnoreCase))
                            {
                                freeKb = kb;
                            }
                            else if (key.Equals("Buffers", StringComparison.OrdinalIgnoreCase))
                            {
                                buffersKb = kb;
                            }
                            else if (key.Equals("Cached", StringComparison.OrdinalIgnoreCase))
                            {
                                cachedKb = kb;
                            }
                        }
                    }

                    if (foundAvailable)
                    {
                        return availableKb / 1024.0;
                    }
                    else
                    {
                        return (freeKb + buffersKb + cachedKb) / 1024.0;
                    }
                }
            }
            catch {}
            return null;
        }

        private static double? GetMacAvailableMemoryMB()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "vm_stat",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    long pageSize = 4096;
                    long freePages = 0;
                    long inactivePages = 0;

                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (line.Contains("page size of", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            int idx = Array.IndexOf(parts, "size");
                            if (idx >= 0 && idx + 2 < parts.Length)
                            {
                                long.TryParse(parts[idx + 2], out pageSize);
                            }
                        }
                        else if (line.StartsWith("Pages free:", StringComparison.OrdinalIgnoreCase))
                        {
                            var valStr = line.Split(':', StringSplitOptions.TrimEntries)[1].TrimEnd('.');
                            long.TryParse(valStr, out freePages);
                        }
                        else if (line.StartsWith("Pages inactive:", StringComparison.OrdinalIgnoreCase))
                        {
                            var valStr = line.Split(':', StringSplitOptions.TrimEntries)[1].TrimEnd('.');
                            long.TryParse(valStr, out inactivePages);
                        }
                    }

                    double availableBytes = (freePages + inactivePages) * (double)pageSize;
                    return availableBytes / (1024.0 * 1024.0);
                }
            }
            catch {}
            return null;
        }

        private static double GetGcFallbackAvailableMemoryMB()
        {
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                long availBytes = gcInfo.TotalAvailableMemoryBytes - gcInfo.MemoryLoadBytes;
                if (availBytes > 0)
                {
                    return availBytes / (1024.0 * 1024.0);
                }
            }
            catch {}
            return 1024.0;
        }
    }
}
