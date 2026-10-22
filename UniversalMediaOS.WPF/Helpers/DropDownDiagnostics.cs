using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.Helpers
{
    /// <summary>Logs each time a drop-down menu opens and closes, and why it closed. A committed choice or other
    /// input of the person closes it. So do its window losing activation, and keyboard focus or mouse capture moving
    /// elsewhere, which a person sees as the menu closing by itself. Rebuilt choices and selection changes do not
    /// close a WPF menu, but are listed with the other events seen while it was open whenever the close was not the
    /// person's own input.</summary>
    internal sealed class DropDownDiagnostics
    {
        private const int WM_ACTIVATE = 0x0006;
        private const int WM_KILLFOCUS = 0x0008;
        private const int WM_ACTIVATEAPP = 0x001C;
        private const int WM_CANCELMODE = 0x001F;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
        private const int MaxEvents = 24;
        // Events that close a menu arrive together, within one input or dispatcher operation.
        private static readonly TimeSpan Burst = TimeSpan.FromMilliseconds(250);
        // Events that close an open ComboBox; the rest only describe what else happened.
        private static readonly HashSet<string> Closers = ["activation", "focus", "capture", "cancel mode", "disabled", "hidden"];

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenThread(int access, bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetProcessIdOfThread(IntPtr thread);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private sealed class Session
        {
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal readonly Queue<(TimeSpan At, string Kind, string Detail, bool WhileOpen)> Events = new();
            internal int Dropped;
            internal TimeSpan? CloseStart;
            internal string? CloseInput;
            internal TimeSpan CloseInputAt;
            internal TimeSpan? ClosedAt;
            internal HwndSource? Source;
        }

        private readonly ComboBox _menu;
        private readonly string _name;
        private readonly Action<string> _log;
        // The open menu, then its closing burst until the close is logged.
        private Session? _session;
        // The person's input that WPF is handling, from its preview until it bubbles back to the menu.
        private string? _input;
        private TimeSpan _inputAt;

        private DropDownDiagnostics(ComboBox menu, string name, Action<string> log)
        {
            _menu = menu;
            _name = name;
            _log = log;
            menu.DropDownOpened += Menu_DropDownOpened;
            menu.DropDownClosed += Menu_DropDownClosed;
            // Descriptions are built only while a menu is open or closing; a closed menu costs nothing.
            menu.SelectionChanged += (_, e) =>
            {
                if (_session != null) Record("selection", $"{Labels(e.RemovedItems)} to {Labels(e.AddedItems)}");
            };
            ((INotifyCollectionChanged)menu.Items).CollectionChanged += (_, e) =>
            {
                if (_session != null) Record("choices", $"{e.Action}, now {menu.Items.Count}");
            };
            menu.IsEnabledChanged += (_, e) => { if (e.NewValue is false) Record("disabled", "the menu"); };
            menu.IsVisibleChanged += (_, e) => { if (e.NewValue is false) Record("hidden", "the menu"); };
            menu.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Menu_LostKeyboardFocus), true);
            menu.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(Menu_LostMouseCapture), true);
            menu.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Menu_PreviewMouseButton), true);
            menu.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Menu_PreviewMouseButton), true);
            menu.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Menu_PreviewKeyDown), true);
            menu.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler(EndInput), true);
            menu.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(EndInput), true);
            menu.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(EndInput), true);
        }

        /// <summary>The menu keeps its diagnostics alive through its own events.</summary>
        internal static void Attach(ComboBox menu, string name, Action<string>? log = null) =>
            _ = new DropDownDiagnostics(menu, name, log ?? (message => AppLogger.Log(message)));

        private void Menu_DropDownOpened(object? sender, EventArgs e)
        {
            if (_session != null) Summarize(_session);
            _input = null;
            var session = new Session { Source = PresentationSource.FromVisual(_menu) as HwndSource };
            _session = session;
            session.Source?.AddHook(WindowMessage);
            _log($"[Menu] {_name} opened: {_menu.Items.Count} choices, selected {Label(_menu.SelectedItem)}; " +
                $"its window {(Window.GetWindow(_menu)?.IsActive == true ? "active" : "inactive")}, " +
                $"foreground {DescribeWindow(GetForegroundWindow())}.");
        }

        private void Menu_DropDownClosed(object? sender, EventArgs e)
        {
            if (_session is not { ClosedAt: null } session) return;
            MarkCloseStart(session);
            session.ClosedAt = session.Clock.Elapsed;
            // Log once the closing operation and its follow-ups have run; some causes are seen after the close.
            _menu.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Summarize(session)));
        }

        private void Summarize(Session session)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
            if (session.Source is { IsDisposed: false } source) source.RemoveHook(WindowMessage);
            TimeSpan closedAt = session.ClosedAt ?? session.Clock.Elapsed;
            // A close that began while WPF handled the person's input is that input's, however long a committed
            // choice's reload took. Otherwise the cause is the earliest closing event seen while the menu was still
            // open: WPF's own handlers close it before later handlers see focus or capture move.
            var burst = session.Events.Where(item => item.At >= (session.CloseStart ?? closedAt) - Burst).ToArray();
            var cause = burst.FirstOrDefault(item => item.WhileOpen && Closers.Contains(item.Kind)) is { Kind: not null } early ? early
                : burst.FirstOrDefault(item => Closers.Contains(item.Kind));
            string reason = session.CloseInput is { } input
                ? session.Events.Any(item => item.Kind == "selection" && item.At >= session.CloseInputAt)
                    ? $"a choice was committed ({input})" : $"input: {input}"
                : cause.Kind == null ? "no recorded cause" : $"{cause.Kind}: {cause.Detail}";
            var line = new StringBuilder($"[Menu] {_name} closed after {closedAt.TotalMilliseconds:0} ms; cause {reason}; " +
                $"selected {Label(_menu.SelectedItem)}; keyboard focus {Describe(Keyboard.FocusedElement)}; " +
                $"foreground {DescribeWindow(GetForegroundWindow())}.");
            if (session.CloseInput == null && session.Events.Count > 0)
            {
                line.Append(" While open");
                if (session.Dropped > 0) line.Append($" (first {session.Dropped} events omitted)");
                line.Append(": ").AppendJoin("; ", session.Events.Select(item =>
                    $"{item.At.TotalMilliseconds:0} ms {item.Kind} {item.Detail}{(item.WhileOpen ? "" : " (closing)")}"));
                line.Append('.');
            }
            _log(line.ToString());
        }

        private void Record(string kind, string detail)
        {
            if (_session is not { } session) return;
            bool whileOpen = _menu.IsDropDownOpen;
            if (!whileOpen) MarkCloseStart(session);
            if (session.Events.Count == MaxEvents)
            {
                session.Events.Dequeue();
                session.Dropped++;
            }
            session.Events.Enqueue((session.Clock.Elapsed, kind, detail, whileOpen));
        }

        /// <summary>The first event after the menu stopped being open, usually its own capture release.</summary>
        private void MarkCloseStart(Session session)
        {
            if (session.CloseStart != null) return;
            session.CloseStart = session.Clock.Elapsed;
            session.CloseInput = _input;
            session.CloseInputAt = _inputAt;
        }

        private void Menu_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Moving between choices, or back to the menu box as it closes, keeps focus in the menu.
            if (_session != null && !IsWithinMenu(e.NewFocus))
                Record("focus", $"moved from {Describe(e.OldFocus)} to {Describe(e.NewFocus)}");
        }

        private void Menu_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_session is not { } session) return;
            // The menu releases capture as it closes, while WPF still handles any input that closed it.
            if (!_menu.IsDropDownOpen) MarkCloseStart(session);
            // A release to nothing is recorded from the window's own message, which tells the close's release apart.
            if (Mouse.Captured != null && !IsWithinMenu(Mouse.Captured))
                Record("capture", $"moved to {Describe(Mouse.Captured)}");
        }

        private void Menu_PreviewMouseButton(object sender, MouseButtonEventArgs e)
        {
            if (_session == null) return;
            ComboBoxItem? choice = ChoiceOf(e.OriginalSource);
            bool pressed = e.ButtonState == MouseButtonState.Pressed;
            // While the menu holds capture, a press anywhere off its choices reaches the menu itself.
            string target = choice != null ? $"choice {Label(choice.Content)}"
                : new Rect(_menu.RenderSize).Contains(e.GetPosition(_menu)) ? "the menu box" : "outside the menu";
            BeginInput($"{e.ChangedButton} mouse {(pressed ? "down" : "up")} on {target}");
        }

        private void Menu_PreviewKeyDown(object sender, KeyEventArgs e) =>
            BeginInput($"key {(e.Key == Key.System ? e.SystemKey : e.Key)}");

        // Input that closes the menu does so while WPF handles it, before the event bubbles back up to the menu.
        private void BeginInput(string input)
        {
            if (_session is not { } session) return;
            _input = input;
            _inputAt = session.Clock.Elapsed;
            Record("input", input);
        }

        private void EndInput(object sender, RoutedEventArgs e) => _input = null;

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (message)
            {
                case WM_ACTIVATE when (wParam.ToInt64() & 0xFFFF) == 0:
                    Record("activation", $"its window was deactivated for {DescribeWindow(lParam)}");
                    break;
                case WM_ACTIVATEAPP when wParam == IntPtr.Zero:
                    Record("activation", $"the app was deactivated for {DescribeThread((uint)lParam.ToInt64())}");
                    break;
                case WM_KILLFOCUS:
                    Record("focus", $"its window lost Win32 focus to {DescribeWindow(wParam)}");
                    break;
                case WM_CAPTURECHANGED when lParam == IntPtr.Zero && !_menu.IsDropDownOpen:
                    Record("capture released", "as the menu closed");
                    break;
                case WM_CAPTURECHANGED:
                    Record("capture", $"its window lost Win32 capture to {DescribeWindow(lParam)}");
                    break;
                case WM_CANCELMODE:
                    Record("cancel mode", "sent to its window");
                    break;
            }
            return IntPtr.Zero;
        }

        private bool IsWithinMenu(object? element)
        {
            for (var node = element as DependencyObject; node != null; node = Parent(node))
            {
                if (ReferenceEquals(node, _menu)) return true;
            }
            return false;

            // Choices live in the menu's Popup, a separate visual tree; follow their logical links back.
            static DependencyObject? Parent(DependencyObject node) => node switch
            {
                ComboBoxItem item => ItemsControl.ItemsControlFromItemContainer(item),
                FrameworkElement element => element.Parent ?? element.TemplatedParent ?? VisualTreeHelper.GetParent(element),
                Visual visual => VisualTreeHelper.GetParent(visual),
                _ => LogicalTreeHelper.GetParent(node)
            };
        }

        private static ComboBoxItem? ChoiceOf(object? source)
        {
            for (var node = source as DependencyObject; node != null;
                 node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (node is ComboBoxItem choice) return choice;
            }
            return null;
        }

        private string Describe(object? element)
        {
            if (element == null) return "none";
            if (ChoiceOf(element) is { } choice) return $"choice {Label(choice.Content)}";
            if (ReferenceEquals(element, _menu)) return "the menu box";
            if (element is Window window) return $"the {window.GetType().Name} window";
            string name = element is FrameworkElement { Name.Length: > 0 } named
                ? $"{named.GetType().Name} '{named.Name}'" : element.GetType().Name;
            return element is DependencyObject node && Window.GetWindow(node) is { } owner &&
                !ReferenceEquals(owner, Window.GetWindow(_menu)) ? $"{name} in {owner.GetType().Name}" : name;
        }

        private static string Label(object? item) => item == null ? "none" : $"'{item}'";

        private static string Labels(IList items) =>
            items.Count == 0 ? "none" : string.Join(", ", items.Cast<object?>().Select(Label));

        /// <summary>Names another process and its window class, never a window title.</summary>
        private static string DescribeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "no window";
            GetWindowThreadProcessId(hwnd, out uint processId);
            var className = new StringBuilder(256);
            string windowClass = GetClassName(hwnd, className, className.Capacity) > 0 ? className.ToString() : "unknown class";
            if (processId != (uint)Environment.ProcessId) return $"{ProcessName(processId)} (pid {processId}, {windowClass})";
            try
            {
                return HwndSource.FromHwnd(hwnd)?.RootVisual is { } root ? $"this app's {root.GetType().Name}" : $"this app's {windowClass}";
            }
            catch (InvalidOperationException)
            {
                return $"this app's {windowClass}";
            }
        }

        private static string DescribeThread(uint threadId)
        {
            IntPtr thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, threadId);
            if (thread == IntPtr.Zero) return $"thread {threadId}";
            try
            {
                uint processId = GetProcessIdOfThread(thread);
                return processId == (uint)Environment.ProcessId ? "this app" : $"{ProcessName(processId)} (pid {processId})";
            }
            finally
            {
                CloseHandle(thread);
            }
        }

        private static string ProcessName(uint processId)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return "an exited process";
            }
        }
    }
}
