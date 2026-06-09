using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Diagnostics
{
    public class SystemDiagnostics
    {
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
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

        public class ResourceStatus
        {
            public bool IsReady { get; set; }
            public double AvailableRamGB { get; set; }
            public double CpuUsagePercent { get; set; }
            public string WarningMessage { get; set; } = string.Empty;
        }

        public static async Task<ResourceStatus> CheckSystemResourcesAsync(double minRamGB = 1.0, double maxCpuPercent = 85.0)
        {
            var status = new ResourceStatus { IsReady = true };

            // Check RAM
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                    memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                    if (GlobalMemoryStatusEx(ref memStatus))
                    {
                        status.AvailableRamGB = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                        if (status.AvailableRamGB < minRamGB)
                        {
                            status.IsReady = false;
                            status.WarningMessage += $"Low Memory: Only {status.AvailableRamGB:F2}GB available. ";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                status.WarningMessage += $"RAM Check failed: {ex.Message}. ";
            }

            // Check CPU
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    cpuCounter.NextValue(); // First call always returns 0
                    await Task.Delay(500); // Wait a moment to get a real reading
                    status.CpuUsagePercent = cpuCounter.NextValue();

                    if (status.CpuUsagePercent > maxCpuPercent)
                    {
                        status.IsReady = false;
                        status.WarningMessage += $"High CPU: Currently at {status.CpuUsagePercent:F1}%. ";
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read CPU usage: {ex.Message}");
            }

            return status;
        }
    }
}
