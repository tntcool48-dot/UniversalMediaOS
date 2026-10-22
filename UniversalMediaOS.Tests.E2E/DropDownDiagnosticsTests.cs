using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

/// <summary>The caption menu's close log must tell a committed choice, another window taking activation and an
/// unexplained close apart; the real-app caption tests report these lines when a menu closes early.</summary>
public sealed class DropDownDiagnosticsTests
{
    private static readonly PlaybackTrackOption Off = new("off", "CC Off");
    private static readonly PlaybackTrackOption English = new("external:en|english (downloaded)", "English (downloaded)");

    [Fact]
    public Task CommittedChoiceIsTheLoggedCause() => WithMenu(async scenario =>
    {
        await scenario.OpenAsync();
        var choice = (ComboBoxItem)scenario.Menu.ItemContainerGenerator.ContainerFromIndex(0);
        choice.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        choice.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
        string closed = await scenario.ClosedLineAsync();
        Assert.Same(Off, scenario.Menu.SelectedItem);
        Assert.Contains("cause a choice was committed (Left mouse up on choice 'CC Off'); selected 'CC Off'", closed);
        Assert.DoesNotContain("While open", closed);
    });

    [Fact]
    public Task SlowCommittedChoiceIsStillTheLoggedCause() => WithMenu(async scenario =>
    {
        await scenario.OpenAsync();
        // A caption choice reloads the player before the menu closes: the UI thread is busy for a while and the
        // menu is disabled until the new source reports its captions. Two real-app closes were once misread so.
        scenario.Menu.SelectionChanged += (_, _) =>
        {
            Thread.Sleep(400);
            scenario.Menu.IsEnabled = false;
        };
        var choice = (ComboBoxItem)scenario.Menu.ItemContainerGenerator.ContainerFromIndex(0);
        choice.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        choice.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
        string closed = await scenario.ClosedLineAsync();
        Assert.Contains("cause a choice was committed (Left mouse up on choice 'CC Off')", closed);
    });

    [Fact]
    public Task ActivationTakenByAnotherWindowIsTheLoggedCause() => WithMenu(async scenario =>
    {
        await scenario.OpenAsync();
        var other = new Window
        {
            Width = 160, Height = 90, Left = 380, Top = 40, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            other.Show();
            other.Activate();
            string closed = await scenario.ClosedLineAsync();
            Assert.False(scenario.Menu.IsDropDownOpen);
            Assert.Contains("cause activation: its window was deactivated for this app's Window", closed);
            Assert.Contains("While open:", closed);
        }
        finally { other.Close(); }
    });

    [Fact]
    public Task RebuiltChoicesKeepTheMenuOpenAndAnUnexplainedCloseNamesNoCause() => WithMenu(async scenario =>
    {
        await scenario.OpenAsync();
        // The player can refresh its choices while the menu is open; WPF keeps the menu open.
        scenario.Choices.Clear();
        scenario.Choices.Add(Off with { });
        scenario.Choices.Add(English with { });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(scenario.Menu.IsDropDownOpen);
        // As UI Automation's Collapse does, close it without input, focus or activation.
        scenario.Menu.IsDropDownOpen = false;
        string closed = await scenario.ClosedLineAsync();
        Assert.Contains("cause no recorded cause", closed);
        Assert.Contains("choices Reset, now 0", closed);
        Assert.Contains("capture released as the menu closed (closing)", closed);
    });

    private sealed class Scenario(ComboBox menu, ObservableCollection<PlaybackTrackOption> choices, Window window, List<string> lines)
    {
        internal ComboBox Menu { get; } = menu;
        internal ObservableCollection<PlaybackTrackOption> Choices { get; } = choices;

        internal async Task OpenAsync()
        {
            window.Activate();
            Menu.Focus();
            Menu.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(Menu.IsDropDownOpen);
            Assert.Contains(lines, line => line.StartsWith("[Menu] Test menu opened: 2 choices", StringComparison.Ordinal));
        }

        /// <summary>The close is logged once its burst settles, after the popup has closed.</summary>
        internal async Task<string> ClosedLineAsync()
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!lines.Any(line => line.Contains(" closed after ", StringComparison.Ordinal)) && waited.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(25);
            return Assert.Single(lines, line => line.Contains(" closed after ", StringComparison.Ordinal));
        }
    }

    private static Task WithMenu(Func<Scenario, Task> test) => NativeShortMediaCompletionTests.OnDispatcher(async () =>
    {
        var lines = new List<string>();
        var choices = new ObservableCollection<PlaybackTrackOption> { Off, English };
        var resources = ControlThemeRenderTests.LoadResources(true);
        var menu = new ComboBox
        {
            Style = (Style)resources["CompactComboBox"], Width = 180, ItemsSource = choices, SelectedItem = English
        };
        var window = new Window
        {
            Resources = resources, Width = 320, Height = 200, Left = 40, Top = 40, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = new StackPanel { Children = { menu } }
        };
        DropDownDiagnostics.Attach(menu, "Test menu", lines.Add);
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await test(new Scenario(menu, choices, window, lines));
        }
        finally { window.Close(); }
    });
}
