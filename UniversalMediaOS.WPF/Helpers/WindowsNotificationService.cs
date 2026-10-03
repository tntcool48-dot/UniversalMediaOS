using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.Helpers
{
    /// <summary>
    /// Best-effort Windows notification delivery using the notification-area API
    /// built into Windows. The in-app alert and persisted history remain available
    /// when notifications are disabled, quiet hours are active, or Explorer is not
    /// available.
    /// </summary>
    public sealed class WindowsNotificationService : IDisposable
    {
        private const uint NotifyIconAdd = 0x00000000;
        private const uint NotifyIconModify = 0x00000001;
        private const uint NotifyIconDelete = 0x00000002;
        private const uint NotifyIconSetVersion = 0x00000004;
        private const uint NotifyIconVersion4 = 4;
        private const uint NotifyIconIcon = 0x00000002;
        private const uint NotifyIconTip = 0x00000004;
        private const uint NotifyIconInfo = 0x00000010;
        private const uint NotifyInfoFlag = 0x00000001;
        private const uint NotifyRespectQuietTime = 0x00000080;
        private const uint IconId = 0x554D4F53; // "UMOS"
        private static readonly IntPtr DefaultApplicationIcon = new(32512);

        private readonly object _sync = new();
        private CancellationTokenSource? _cleanupCancellation;
        private IntPtr _windowHandle;
        private bool _iconAdded;
        private bool _disposed;

        public bool TryShow(string title, string message)
        {
            if (_disposed || !OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted)
                {
                    return false;
                }

                return dispatcher.CheckAccess()
                    ? ShowCore(title, message)
                    : dispatcher.Invoke(() => ShowCore(title, message));
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Windows episode notification was unavailable: {ex.Message}", "WARNING");
                return false;
            }
        }

        private bool ShowCore(string title, string message)
        {
            Window? window = Application.Current?.MainWindow;
            if (window == null || !window.IsLoaded)
            {
                return false;
            }

            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            lock (_sync)
            {
                DeleteIconLocked();
                _windowHandle = handle;

                var data = CreateData(handle);
                data.uFlags = NotifyIconIcon | NotifyIconTip;
                data.hIcon = LoadIconW(IntPtr.Zero, DefaultApplicationIcon);
                data.szTip = "Universal Media OS";
                if (!Shell_NotifyIconW(NotifyIconAdd, ref data))
                {
                    _windowHandle = IntPtr.Zero;
                    return false;
                }

                _iconAdded = true;
                data.uTimeoutOrVersion = NotifyIconVersion4;
                Shell_NotifyIconW(NotifyIconSetVersion, ref data);

                data.uFlags = NotifyIconInfo;
                data.szInfoTitle = Truncate(string.IsNullOrWhiteSpace(title) ? "New episode" : title.Trim(), 63);
                data.szInfo = Truncate(message.Trim(), 255);
                data.dwInfoFlags = NotifyInfoFlag | NotifyRespectQuietTime;
                data.uTimeoutOrVersion = 10000;
                bool shown = Shell_NotifyIconW(NotifyIconModify, ref data);
                if (!shown)
                {
                    DeleteIconLocked();
                    return false;
                }

                ScheduleCleanupLocked();
                return true;
            }
        }

        private void ScheduleCleanupLocked()
        {
            _cleanupCancellation?.Cancel();
            _cleanupCancellation?.Dispose();
            _cleanupCancellation = new CancellationTokenSource();
            CancellationToken token = _cleanupCancellation.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(12), token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            DeleteIconLocked();
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
            });
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _cleanupCancellation?.Cancel();
                _cleanupCancellation?.Dispose();
                _cleanupCancellation = null;
                DeleteIconLocked();
            }
        }

        private void DeleteIconLocked()
        {
            if (!_iconAdded || _windowHandle == IntPtr.Zero)
            {
                return;
            }

            var data = CreateData(_windowHandle);
            Shell_NotifyIconW(NotifyIconDelete, ref data);
            _iconAdded = false;
            _windowHandle = IntPtr.Zero;
        }

        private static NotifyIconData CreateData(IntPtr handle) => new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = handle,
            uID = IconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;

            public uint dwState;
            public uint dwStateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;

            public uint uTimeoutOrVersion;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;

            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);
    }
}
