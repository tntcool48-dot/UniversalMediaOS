using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Services
{
    /// <summary>
    /// Windows-only RAM availability check using GlobalMemoryStatusEx.
    /// All Linux/macOS branches have been removed — this is a Windows-only WPF application.
    /// </summary>
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

        public SystemResourceCheck() : this(512.0) { }

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
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));

                if (!GlobalMemoryStatusEx(ref memStatus))
                {
                    int err = Marshal.GetLastWin32Error();
                    return (false, $"Failed to query system memory (GlobalMemoryStatusEx Error: {err}).");
                }

                double availableMB = memStatus.ullAvailPhys / (1024.0 * 1024.0);
                double availableGB = availableMB / 1024.0;

                if (availableMB < minAvailableRamMB)
                {
                    return (false,
                        $"Low memory — only {availableGB:F2} GB ({availableMB:F0} MB) available. " +
                        $"At least {minAvailableRamMB:F0} MB required.");
                }

                return (true, $"RAM: {availableGB:F1} GB available");
            }
            catch (Exception ex)
            {
                return (false, $"System Check Failed: {ex.Message}");
            }
        }

        public static async Task<(bool IsReady, string Message)> PerformStartupCheckAsync(
            double minAvailableRamMB = 512.0)
        {
            return await Task.Run(() => PerformStartupCheck(minAvailableRamMB));
        }
    }
}
