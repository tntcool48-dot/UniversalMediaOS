using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Views;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

// What a screen reader reads for selectors and the seek bar, from the actual
// automation peers. A closed, never-opened selector has no item containers, so
// its selected item is read through the item peer, which falls back to the
// item's ToString(); a slider's value pattern is read in place of its range.
public sealed class PlaybackScreenReaderTextTests
{
    [Fact]
    public void PlayerSelectorsAndSeekBarAnnounceTheirVisibleText()
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            LocalizationRuntime.SetLanguage("English");
            var resources = ControlThemeRenderTests.LoadResources(true);
            resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
            var state = new PlayerState();
            var view = new PlaybackView(resources) { DataContext = state };
            try
            {
                var overlay = (Grid)view.FindName("NativeOverlay");
                overlay.DataContext = state;
                ((Expander)view.FindName("PlaybackOptions")).IsExpanded = true;
                void Flush()
                {
                    overlay.Measure(new Size(1280, 800));
                    overlay.Arrange(new Rect(0, 0, 1280, 800));
                    overlay.UpdateLayout();
                    view.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                }
                Flush();
                var elements = Descendants(overlay).OfType<FrameworkElement>().ToArray();
                FrameworkElement Named(string name) =>
                    Assert.Single(elements, element => AutomationProperties.GetName(element) == name);
                string SeekValue(string name) => UIElementAutomationPeer.CreatePeerForElement(Named(name))
                    .GetPattern(PatternInterface.Value) is IValueProvider value ? value.Value : "<no value pattern>";
                var mismatches = new List<string>();
                void Expect(string expected, string actual, string what)
                {
                    if (expected != actual) mismatches.Add($"{what}: expected '{expected}', read '{actual}'");
                }

                Expect("720p", SelectedItemName((ComboBox)Named("Video quality")), "quality");
                Expect("English (downloaded)", SelectedItemName((ComboBox)Named("Subtitle track")), "subtitles");
                Expect("English · ENG", SelectedItemName((ComboBox)Named("Audio track")), "audio");
                Expect("01:01 of 03:00", SeekValue("Seek video"), "seek bar");
                var range = (IRangeValueProvider)UIElementAutomationPeer.CreatePeerForElement(Named("Seek video"))
                    .GetPattern(PatternInterface.RangeValue);
                Expect("61192", range.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "seek range");

                LocalizationRuntime.SetLanguage("Arabic");
                Flush();
                elements = Descendants(overlay).OfType<FrameworkElement>().ToArray();
                Expect("01:01 من 03:00", SeekValue("تغيير موضع الفيديو"), "Arabic seek bar");
                Expect("720p", SelectedItemName((ComboBox)Named("جودة الفيديو")), "Arabic quality");
                Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
            }
            finally
            {
                view.CloseForTab();
                LocalizationRuntime.SetLanguage("English");
            }
        });
    }

    [Fact]
    public void TelevisionSeasonAndEpisodeSelectorsAnnounceTheirDisplayNames()
    {
        RecoveryLayoutTests.RunSta(() =>
        {
            var id = new AudiovisualExternalId("fixture", "episode", "1");
            var season = new AudiovisualSeasonChoice(1);
            var episode = new AudiovisualEpisodeChoice(new AudiovisualEpisodeMetadata(id, "Cat's in the Bag...", 1,
                new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 2 }, false));
            var special = new AudiovisualEpisodeChoice(new AudiovisualEpisodeMetadata(id, "Minisode", null, null, true));
            var mismatches = new List<string>();
            // As in the catalog view: DisplayMemberPath="DisplayName".
            foreach (var (items, selected) in new (object[] Items, object Selected)[]
            {
                ([new AudiovisualSeasonChoice(null), season], season),
                ([episode, special], episode),
                ([episode, special], special)
            })
            {
                var combo = new ComboBox { ItemsSource = items, DisplayMemberPath = "DisplayName", SelectedItem = selected };
                combo.Measure(new Size(300, 40));
                combo.Arrange(new Rect(0, 0, 300, 40));
                string expected = (string)selected.GetType().GetProperty("DisplayName")!.GetValue(selected)!;
                string actual = SelectedItemName(combo);
                if (expected != actual) mismatches.Add($"expected '{expected}', read '{actual}'");
            }
            Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
        });
    }

    // The selected item's peer, as the combo box creates it for a UI Automation
    // client while its menu has never been opened (no item container exists).
    private static string SelectedItemName(ComboBox combo) => new ItemPeerProbe(combo).SelectedName();

    private sealed class ItemPeerProbe(ComboBox combo) : ComboBoxAutomationPeer(combo)
    {
        public string SelectedName() => CreateItemAutomationPeer(((ComboBox)Owner).SelectedItem).GetName();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private sealed class PlayerState
    {
        public string SourceInput { get; set; } = string.Empty;
        public string MediaTitle => "Playback";
        public string AudioNotice => string.Empty;
        public bool IsTabActive => true;
        public bool IsWebViewActive => false;
        public bool IsPersistenceControlsAvailable => true;
        public bool AudioTracksAvailable => true;
        public bool CaptionsAvailable => true;
        public bool IsQualitySelectionAvailable => true;
        public double PlaybackTime { get; set; } = 61_192;
        public double PlaybackDuration => 180_000;
        public string CurrentTimeText => "01:01";
        public string DurationText => "03:00";
        public double[] PlaybackRates { get; } = [1, 1.25];
        public double PlaybackRate { get; set; } = 1.25;
        public PlaybackTrackOption SelectedCaption { get; set; } = new("external:en|dialogue", "English (downloaded)");
        public PlaybackTrackOption[] CaptionOptions => [new("off", "CC Off"), SelectedCaption];
        public PlaybackTrackOption SelectedAudioTrack { get; set; } = new("native:eng:3", "English · ENG", 3);
        public PlaybackTrackOption[] AudioOptions => [SelectedAudioTrack];
        public PlaybackQualityOption SelectedQuality { get; set; } = new("720p", "https://fixture.invalid/master/720.m3u8", 720);
        public PlaybackQualityOption[] QualityOptions => [new("Auto", "https://fixture.invalid/master.m3u8"), SelectedQuality];
    }
}
