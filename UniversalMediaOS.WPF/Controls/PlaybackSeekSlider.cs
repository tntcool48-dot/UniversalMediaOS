using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using UniversalMediaOS.WPF.Helpers;

namespace UniversalMediaOS.WPF.Controls;

/// <summary>
/// The seek bar's range is in milliseconds. Screen readers read a slider's
/// value pattern when it has one, so give them the displayed clock instead.
/// </summary>
public sealed class PlaybackSeekSlider : Slider
{
    // Keep the Slider theme template; the app's implicit Slider style is set in XAML.
    static PlaybackSeekSlider() => DefaultStyleKeyProperty.OverrideMetadata(
        typeof(PlaybackSeekSlider), new FrameworkPropertyMetadata(typeof(Slider)));

    public static readonly DependencyProperty PositionTextProperty = DependencyProperty.Register(
        nameof(PositionText), typeof(string), typeof(PlaybackSeekSlider),
        new PropertyMetadata(string.Empty, OnClockTextChanged));

    public static readonly DependencyProperty DurationTextProperty = DependencyProperty.Register(
        nameof(DurationText), typeof(string), typeof(PlaybackSeekSlider),
        new PropertyMetadata(string.Empty, OnClockTextChanged));

    public string PositionText
    {
        get => (string)GetValue(PositionTextProperty);
        set => SetValue(PositionTextProperty, value);
    }

    public string DurationText
    {
        get => (string)GetValue(DurationTextProperty);
        set => SetValue(DurationTextProperty, value);
    }

    internal string ClockText => string.Format(CultureInfo.CurrentCulture,
        LocalizationRuntime.Translate("{0} of {1}"), PositionText, DurationText);

    protected override AutomationPeer OnCreateAutomationPeer() => new SeekSliderAutomationPeer(this);

    private static void OnClockTextChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        // Announce the clock only while someone is on the seek bar; playback
        // updates it several times a second.
        if (owner is PlaybackSeekSlider { IsKeyboardFocused: true } slider &&
            AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) &&
            UIElementAutomationPeer.FromElement(slider) is SeekSliderAutomationPeer peer)
            peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, string.Empty, slider.ClockText);
    }

    private sealed class SeekSliderAutomationPeer(PlaybackSeekSlider slider) : SliderAutomationPeer(slider), IValueProvider
    {
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

        string IValueProvider.Value => ((PlaybackSeekSlider)Owner).ClockText;

        bool IValueProvider.IsReadOnly => true;

        void IValueProvider.SetValue(string value) =>
            throw new InvalidOperationException("Seek with the slider's range value.");
    }
}
