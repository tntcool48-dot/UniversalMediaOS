using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UniversalMediaOS.WPF.Helpers
{
    public static class WindowHelper
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_MICA_EFFECT = 1029;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public static void EnableMica(Window window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));

            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero)
            {
                EventHandler? handler = null;
                handler = (s, e) =>
                {
                    window.SourceInitialized -= handler;
                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        ApplyMica(hwnd);
                    }
                };
                window.SourceInitialized += handler;
            }
            else
            {
                ApplyMica(helper.Handle);
            }
        }

        private static void ApplyMica(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;

            int trueValue = 1;
            // Windows 11 Build 22523+ Backdrop type
            int micaBackdrop = 2; 

            // Try modern Windows 11 backdrop attribute first
            int hr = DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, ref micaBackdrop, Marshal.SizeOf(typeof(int)));
            
            if (hr != 0)
            {
                // Fallback for earlier Windows 11 builds
                int hr2 = DwmSetWindowAttribute(handle, DWMWA_MICA_EFFECT, ref trueValue, Marshal.SizeOf(typeof(int)));
                if (hr2 != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to set Mica effect (HRESULT {hr} / {hr2})");
                }
            }
        }
    }
}
