using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;

namespace UniversalMediaOS.WPF
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr lprcClip,
            MonitorEnumProc lpfnEnum,
            IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);

        private delegate bool MonitorEnumProc(
            IntPtr hMonitor,
            IntPtr hdcMonitor,
            IntPtr lprcMonitor,
            IntPtr dwData);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int MONITORINFOF_PRIMARY = 1;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const string TabDragFormat = "UniversalMediaOS.Tab";

        private Point _tabDragStart;
        private MediaTabViewModel? _draggedTab;
        private bool _isAppFullscreen;
        private WindowStyle _restoreWindowStyle;
        private WindowState _restoreWindowState;
        private ResizeMode _restoreResizeMode;
        private bool _restoreTopmost;
        private double _restoreLeft;
        private double _restoreTop;
        private double _restoreWidth;
        private double _restoreHeight;
        private WindowChrome? _restoreWindowChrome;
        private readonly bool _startOnSecondaryMonitor;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        public MainWindow(ViewModels.MainViewModel viewModel, DomainHotSwapper config)
        {
            InitializeComponent();
            _startOnSecondaryMonitor = config.GetSetting("StartupMonitor")
                .Equals("Secondary", StringComparison.OrdinalIgnoreCase);
            if (_startOnSecondaryMonitor)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
            }

            if (config.GetSetting("StartMaximized") == "false")
            {
                WindowState = WindowState.Normal;
            }
            DataContext = viewModel;
            SourceInitialized += MainWindow_SourceInitialized;
            WindowHelper.EnableMica(this);
            LocalizationRuntime.EnableAutoApply(this, () =>
                viewModel.SettingsViewModel.SelectedLanguage.Equals("Arabic", StringComparison.OrdinalIgnoreCase));
            UpdateWindowStateVisuals();
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            if (_startOnSecondaryMonitor)
            {
                PlaceOnSecondaryMonitor(hwnd);
            }
        }

        private void PlaceOnSecondaryMonitor(IntPtr hwnd)
        {
            IntPtr secondaryMonitor = IntPtr.Zero;
            RECT secondaryWorkArea = default;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref info) || (info.dwFlags & MONITORINFOF_PRIMARY) != 0)
                {
                    return true;
                }

                secondaryMonitor = monitor;
                secondaryWorkArea = info.rcWork;
                return false;
            }, IntPtr.Zero);

            if (secondaryMonitor == IntPtr.Zero)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log(
                    "Secondary-monitor startup was requested, but no secondary display was available.",
                    "WARNING");
                return;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            int workWidth = Math.Max(1, secondaryWorkArea.Right - secondaryWorkArea.Left);
            int workHeight = Math.Max(1, secondaryWorkArea.Bottom - secondaryWorkArea.Top);
            int targetWidth = Math.Min(workWidth, Math.Max(1, (int)Math.Round(Width * dpi.DpiScaleX)));
            int targetHeight = Math.Min(workHeight, Math.Max(1, (int)Math.Round(Height * dpi.DpiScaleY)));
            int targetLeft = secondaryWorkArea.Left + Math.Max(0, (workWidth - targetWidth) / 2);
            int targetTop = secondaryWorkArea.Top + Math.Max(0, (workHeight - targetHeight) / 2);
            WindowState requestedState = WindowState;
            if (requestedState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }

            bool moved = SetWindowPos(
                hwnd,
                IntPtr.Zero,
                targetLeft,
                targetTop,
                targetWidth,
                targetHeight,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            if (requestedState == WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }

            IntPtr actualMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            string level = moved && actualMonitor == secondaryMonitor ? "INFO" : "WARNING";
            UniversalMediaOS.Core.Helpers.AppLogger.Log(
                moved && actualMonitor == secondaryMonitor
                    ? "Main window placed on the requested secondary monitor before display."
                    : "Main window could not be verified on the requested secondary monitor.",
                level);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppFullscreen)
            {
                ExitAppFullscreen();
            }

            WindowState = WindowState.Minimized;
        }

        private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppFullscreen)
            {
                ExitAppFullscreen();
                WindowState = WindowState.Normal;
                return;
            }

            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            UpdateWindowStateVisuals();
        }

        private void UpdateWindowStateVisuals()
        {
            if (MaximizeRestoreButton == null)
            {
                return;
            }

            bool canRestore = _isAppFullscreen || WindowState == WindowState.Maximized;
            MaximizeRestoreButton.Content = canRestore ? "\uE923" : "\uE922";
            MaximizeRestoreButton.ToolTip = canRestore
                ? "Restore the app window."
                : "Maximize the app window.";
        }

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleAppFullscreen();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleAppFullscreen();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _isAppFullscreen)
            {
                ExitAppFullscreen();
                e.Handled = true;
            }
            else if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) &&
                     DataContext is MainViewModel vm &&
                     vm.ActiveTab is not null &&
                     (e.Key == Key.Left || e.Key == Key.Right))
            {
                if (e.Key == Key.Left && vm.MoveTabLeftCommand.CanExecute(vm.ActiveTab))
                {
                    vm.MoveTabLeftCommand.Execute(vm.ActiveTab);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right && vm.MoveTabRightCommand.CanExecute(vm.ActiveTab))
                {
                    vm.MoveTabRightCommand.Execute(vm.ActiveTab);
                    e.Handled = true;
                }
            }
        }

        private void ScrollTabsLeftButton_Click(object sender, RoutedEventArgs e)
        {
            TabHeadersScrollViewer.ScrollToHorizontalOffset(
                Math.Max(0, TabHeadersScrollViewer.HorizontalOffset - 220));
        }

        private void ScrollTabsRightButton_Click(object sender, RoutedEventArgs e)
        {
            TabHeadersScrollViewer.ScrollToHorizontalOffset(
                Math.Min(TabHeadersScrollViewer.ScrollableWidth, TabHeadersScrollViewer.HorizontalOffset + 220));
        }

        private void ToggleAppFullscreen()
        {
            if (_isAppFullscreen)
            {
                ExitAppFullscreen();
            }
            else
            {
                EnterAppFullscreen();
            }
        }

        private void EnterAppFullscreen()
        {
            if (_isAppFullscreen)
            {
                return;
            }

            _restoreWindowStyle = WindowStyle;
            _restoreWindowState = WindowState;
            _restoreResizeMode = ResizeMode;
            _restoreTopmost = Topmost;
            _restoreLeft = Left;
            _restoreTop = Top;
            _restoreWidth = Width;
            _restoreHeight = Height;
            _restoreWindowChrome = WindowChrome.GetWindowChrome(this);

            WindowChrome.SetWindowChrome(this, null);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowState = WindowState.Normal;

            ApplyCurrentMonitorBounds();
            _isAppFullscreen = true;
            UpdateWindowStateVisuals();
            UniversalMediaOS.Core.Helpers.AppLogger.Log("[MainWindow] Whole-app fullscreen enabled.");
        }

        private void ExitAppFullscreen()
        {
            if (!_isAppFullscreen)
            {
                return;
            }

            WindowChrome.SetWindowChrome(this, _restoreWindowChrome);
            Topmost = _restoreTopmost;
            ResizeMode = _restoreResizeMode;
            WindowStyle = _restoreWindowStyle;
            WindowState = WindowState.Normal;
            Left = _restoreLeft;
            Top = _restoreTop;
            Width = _restoreWidth;
            Height = _restoreHeight;
            ClampWindowToNearestWorkArea();
            WindowState = _restoreWindowState;
            _isAppFullscreen = false;
            UpdateWindowStateVisuals();
            UniversalMediaOS.Core.Helpers.AppLogger.Log("[MainWindow] Whole-app fullscreen disabled.");
        }

        private void ApplyCurrentMonitorBounds()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                WindowState = WindowState.Maximized;
                return;
            }

            Matrix fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                                ?? Matrix.Identity;
            Point topLeft = fromDevice.Transform(new Point(info.rcMonitor.Left, info.rcMonitor.Top));
            Point bottomRight = fromDevice.Transform(new Point(info.rcMonitor.Right, info.rcMonitor.Bottom));

            Left = topLeft.X;
            Top = topLeft.Y;
            Width = Math.Max(1, bottomRight.X - topLeft.X);
            Height = Math.Max(1, bottomRight.Y - topLeft.Y);
        }

        private void ClampWindowToNearestWorkArea()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            Matrix fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                                ?? Matrix.Identity;
            Point workTopLeft = fromDevice.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
            Point workBottomRight = fromDevice.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
            double workWidth = Math.Max(1, workBottomRight.X - workTopLeft.X);
            double workHeight = Math.Max(1, workBottomRight.Y - workTopLeft.Y);

            Width = Math.Min(Math.Max(1, Width), workWidth);
            Height = Math.Min(Math.Max(1, Height), workHeight);
            Left = Math.Min(Math.Max(Left, workTopLeft.X), workBottomRight.X - Width);
            Top = Math.Min(Math.Max(Top, workTopLeft.Y), workBottomRight.Y - Height);
        }

        private void TabHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _tabDragStart = e.GetPosition(this);
            _draggedTab = (sender as FrameworkElement)?.DataContext as MediaTabViewModel;
        }

        private void TabHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedTab == null)
            {
                return;
            }

            var position = e.GetPosition(this);
            if (Math.Abs(position.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(TabDragFormat, _draggedTab), DragDropEffects.Move);
            _draggedTab = null;
        }

        private void TabHeader_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(TabDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void TabHeader_Drop(object sender, DragEventArgs e)
        {
            if (DataContext is not MainViewModel vm ||
                !e.Data.GetDataPresent(TabDragFormat) ||
                e.Data.GetData(TabDragFormat) is not MediaTabViewModel source ||
                (sender as FrameworkElement)?.DataContext is not MediaTabViewModel target ||
                ReferenceEquals(source, target))
            {
                return;
            }

            int sourceIndex = vm.Tabs.IndexOf(source);
            int targetIndex = vm.Tabs.IndexOf(target);
            if (sourceIndex < 0 || targetIndex < 0)
            {
                return;
            }

            vm.Tabs.Move(sourceIndex, targetIndex);
            vm.ActiveTab = source;
            e.Handled = true;
        }
    }
}
