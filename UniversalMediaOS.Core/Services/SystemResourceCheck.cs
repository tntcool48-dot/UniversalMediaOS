using System;
using System.Runtime.InteropServices;

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

        private const double MinAvailableRamMB = 512.0;

        /// <summary>Whether the last RunCheck determined the system is ready.</summary>
        public bool IsReady { get; private set; }

        /// <summary>Human-readable status from the last RunCheck.</summary>
        public string StatusMessage { get; private set; } = "Not checked yet.";

        /// <summary>
        /// Performs the system resource check and updates IsReady / StatusMessage.
        /// </summary>
        public void RunCheck()
        {
            var result = PerformStartupCheck();
            IsReady = result.IsReady;
            StatusMessage = result.Message;
        }

        /// <summary>
        /// Static helper that returns a tuple — keeps the original public API so
        /// MainWindow and other callers continue to compile without changes.
        /// </summary>
        public static (bool IsReady, string Message) PerformStartupCheck()
        {
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));

                if (!GlobalMemoryStatusEx(ref memStatus))
                {
                    return (false, "Failed to query system memory (GlobalMemoryStatusEx returned false).");
                }

                double availableMB = memStatus.ullAvailPhys / (1024.0 * 1024.0);
                double availableGB = availableMB / 1024.0;

                if (availableMB < MinAvailableRamMB)
                {
                    return (false,
                        $"Low memory — only {availableGB:F2} GB ({availableMB:F0} MB) available. " +
                        $"At least {MinAvailableRamMB:F0} MB required.");
                }

                string statusMsg = $"RAM: {availableGB:F1} GB available";
                return (true, statusMsg);
            }
            catch (Exception ex)
            {
                return (false, $"System Check Failed: {ex.Message}");
            }
        }
    }
}
