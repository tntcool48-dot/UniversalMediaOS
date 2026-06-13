using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;

namespace UniversalMediaOS.WPF
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public MainWindow(ViewModels.MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            SourceInitialized += MainWindow_SourceInitialized;
            WindowHelper.EnableMica(this);
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
    }
}
