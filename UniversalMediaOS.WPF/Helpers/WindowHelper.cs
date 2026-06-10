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
            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero)
            {
                window.SourceInitialized += (s, e) => ApplyMica(new WindowInteropHelper(window).Handle);
            }
            else
            {
                ApplyMica(helper.Handle);
            }
        }

        private static void ApplyMica(IntPtr handle)
        {
            int trueValue = 1;
            // Windows 11 Build 22523+ Backdrop type
            int micaBackdrop = 2; 

            // Try modern Windows 11 backdrop attribute first
            int hr = DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, ref micaBackdrop, Marshal.SizeOf(typeof(int)));
            
            if (hr != 0)
            {
                // Fallback for earlier Windows 11 builds
                DwmSetWindowAttribute(handle, DWMWA_MICA_EFFECT, ref trueValue, Marshal.SizeOf(typeof(int)));
            }
        }
    }
}
