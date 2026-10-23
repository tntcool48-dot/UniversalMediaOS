using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace UniversalMediaOS.WPF.Helpers
{
    /// <summary>
    /// A busy command disables its own button, and WPF then moves keyboard focus to the nearest focusable
    /// container (an unnamed scroll pane) or nowhere. Set on a view, this returns focus to the control once it is
    /// available again, so a keyboard or screen-reader user continues from where they acted.
    /// </summary>
    public static class KeyboardFocusReturn
    {
        public static readonly DependencyProperty AfterDisabledProperty =
            DependencyProperty.RegisterAttached("AfterDisabled", typeof(bool), typeof(KeyboardFocusReturn),
                new PropertyMetadata(false, OnAfterDisabledChanged));

        public static bool GetAfterDisabled(DependencyObject element) => (bool)element.GetValue(AfterDisabledProperty);
        public static void SetAfterDisabled(DependencyObject element, bool value) => element.SetValue(AfterDisabledProperty, value);

        private static void OnAfterDisabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is not UIElement view) return;
            view.RemoveHandler(Keyboard.LostKeyboardFocusEvent, (KeyboardFocusChangedEventHandler)OnLostKeyboardFocus);
            if ((bool)e.NewValue)
                view.AddHandler(Keyboard.LostKeyboardFocusEvent, (KeyboardFocusChangedEventHandler)OnLostKeyboardFocus, true);
        }

        private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Only focus taken by the control becoming unavailable; a move the user made is kept.
            if (e.OldFocus is not UIElement control || control.IsEnabled || !FellBackFrom(e.NewFocus, control)) return;

            void Returned(object _, DependencyPropertyChangedEventArgs enabled)
            {
                if (!(bool)enabled.NewValue) return;
                control.IsEnabledChanged -= Returned;
                if (control.IsVisible && FellBackFrom(Keyboard.FocusedElement, control)) control.Focus();
            }
            control.IsEnabledChanged += Returned;
        }

        /// <summary>True when focus is nowhere or on one of the control's own containers.</summary>
        private static bool FellBackFrom(IInputElement? focus, UIElement control) =>
            focus == null || (focus is Visual container && container.IsAncestorOf(control));
    }
}
