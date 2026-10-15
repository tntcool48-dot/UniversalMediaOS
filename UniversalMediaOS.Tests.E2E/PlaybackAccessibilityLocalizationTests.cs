using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Views;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class PlaybackAccessibilityLocalizationTests
{
    [Fact]
    public void ActualPlayerPeersLocalizeLiveWithoutChangingSourceOrTrackSelections()
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
                // Exercise the production view and peer implementations without
                // showing a window or starting a media/stream/database session.
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
                var elements = Descendants(view).Concat(Descendants(overlay)).OfType<FrameworkElement>().Distinct().ToArray();
                var names = new Dictionary<string, string>
                {
                    ["Retry playback"] = "إعادة محاولة التشغيل",
                    ["Open browser fallback"] = "فتح مشغل الموقع البديل",
                    ["Exit picture in picture"] = "الخروج من النافذة المصغرة",
                    ["Previous episode"] = "الحلقة السابقة",
                    ["Next episode"] = "الحلقة التالية",
                    ["Stop playback"] = "إيقاف التشغيل",
                    ["Media URL or local file path"] = "رابط الوسائط أو مسار الملف المحلي",
                    ["Open media URL or path"] = "فتح رابط الوسائط أو مسار الملف",
                    ["Choose a local media file"] = "اختيار ملف وسائط محلي",
                    ["Seek video"] = "تغيير موضع الفيديو",
                    ["Playback volume"] = "مستوى صوت التشغيل",
                    ["Toggle picture in picture"] = "تبديل النافذة المصغرة",
                    ["Toggle fullscreen"] = "تبديل ملء الشاشة",
                    ["Play or pause"] = "تشغيل أو إيقاف مؤقت",
                    ["Skip backward 10 seconds"] = "الرجوع 10 ثوانٍ",
                    ["Skip forward 30 seconds"] = "التقدم 30 ثانية"
                };
                var actions = elements.Select(element => (Element: element, Name: AutomationProperties.GetName(element)))
                    .Where(action => names.ContainsKey(action.Name))
                    .Select(action => (action.Element, English: action.Name, Arabic: names[action.Name],
                        Peer: Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(action.Element))))
                    .ToArray();
                foreach (string name in names.Keys) Assert.Contains(actions, action => action.English == name);
                var tips = new Dictionary<string, string>
                {
                    ["Restore the app window."] = "استعادة نافذة التطبيق.",
                    ["Drag here to move the picture-in-picture window."] = "اسحب هنا لتحريك النافذة المصغرة.",
                    ["Direct media URL or local file path; Enter to open."] = "رابط وسائط مباشر أو مسار ملف محلي؛ اضغط Enter للفتح.",
                    ["Seek to a position in the video."] = "الانتقال إلى موضع في الفيديو.",
                    ["Adjust playback volume."] = "ضبط مستوى صوت التشغيل.",
                    ["Picture in picture (P)"] = "النافذة المصغرة (P)",
                    ["Fullscreen (F); Escape to return."] = "ملء الشاشة (F)؛ اضغط Escape للعودة.",
                    ["Play or pause (Space or K)"] = "تشغيل أو إيقاف مؤقت (Space أو K)",
                    ["Back 10 seconds (Left arrow)"] = "الرجوع 10 ثوانٍ (السهم الأيسر)",
                    ["Forward 30 seconds (Right arrow)"] = "التقدم 30 ثانية (السهم الأيمن)"
                };
                var tooltips = elements.Where(element => element.ToolTip is string tip && tips.ContainsKey(tip))
                    .Select(element => (Element: element, English: (string)element.ToolTip, Arabic: tips[(string)element.ToolTip])).ToArray();
                foreach (string tip in tips.Keys) Assert.Contains(tooltips, item => item.English == tip);
                var source = Assert.IsType<TextBox>(Assert.Single(actions, action => action.English == "Media URL or local file path").Element);
                var caption = Assert.Single(elements.OfType<ComboBox>(), combo => AutomationProperties.GetName(combo) == "Subtitle track");
                var audio = Assert.Single(elements.OfType<ComboBox>(), combo => AutomationProperties.GetName(combo) == "Audio track");
                var quality = Assert.Single(elements.OfType<ComboBox>(), combo => AutomationProperties.GetName(combo) == "Video quality");
                var rate = Assert.Single(elements.OfType<ComboBox>(), combo => AutomationProperties.GetName(combo) == "Playback speed");
                var notice = Assert.Single(elements.OfType<TextBlock>(), block => block.Text == state.AudioNotice);
                var title = (TextBlock)view.FindName("MediaTitleText");
                void AssertState()
                {
                    Assert.Equal(@"C:\fixture\Playback-English (downloaded).mkv", source.Text);
                    Assert.Equal(source.Text, state.SourceInput);
                    Assert.Equal("Playback", title.Text);
                    Assert.Equal("Playback", state.MediaTitle);
                    Assert.Same(state.SelectedCaption, caption.SelectedItem);
                    Assert.Equal("external:en|dialogue", state.SelectedCaption.Key);
                    Assert.Equal("English (downloaded)", state.SelectedCaption.Label);
                    Assert.Same(state.SelectedAudioTrack, audio.SelectedItem);
                    Assert.Equal("native:eng:3", state.SelectedAudioTrack.Key);
                    Assert.Same(state.SelectedQuality, quality.SelectedItem);
                    Assert.Equal("https://fixture.invalid/master/720.m3u8", state.SelectedQuality.Url);
                    Assert.Equal(1.25, rate.SelectedItem);
                    Assert.Equal("Audio tracks tagged ENG, GER; check Audio in the player.", state.AudioNotice);
                }
                Assert.All(actions, action => Assert.Equal(action.English, action.Peer.GetName()));
                AssertState();
                LocalizationRuntime.SetLanguage("Arabic");
                Flush();
                Assert.All(actions, action => Assert.Equal(action.Arabic, action.Peer.GetName()));
                Assert.All(tooltips, item => Assert.Equal(item.Arabic, item.Element.ToolTip));
                Assert.Equal("مسارات الصوت تحمل الوسوم ENG, GER؛ تحقق من مسار الصوت في المشغل.", notice.Text);
                AssertState();
                LocalizationRuntime.SetLanguage("English");
                Flush();
                Assert.All(actions, action => Assert.Equal(action.English, action.Peer.GetName()));
                Assert.All(tooltips, item => Assert.Equal(item.English, item.Element.ToolTip));
                Assert.Equal(state.AudioNotice, notice.Text);
                AssertState();
            }
            finally
            {
                view.CloseForTab();
                LocalizationRuntime.SetLanguage("English");
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    // Only input state is substituted. Controls, templates, live localization
    // bindings and automation peers come from the actual production view.
    private sealed class PlayerState
    {
        public string SourceInput { get; set; } = @"C:\fixture\Playback-English (downloaded).mkv";
        public string MediaTitle => "Playback";
        public string AudioNotice => "Audio tracks tagged ENG, GER; check Audio in the player.";
        public bool IsTabActive => true;
        public bool IsWebViewActive => false;
        public bool IsPersistenceControlsAvailable => true;
        public bool HasPlaybackError => true;
        public bool HasWebFallback => true;
        public bool AudioTracksAvailable => true;
        public bool CaptionsAvailable => true;
        public bool IsQualitySelectionAvailable => true;
        public double[] PlaybackRates { get; } = [1, 1.25];
        public double PlaybackRate { get; set; } = 1.25;
        public PlaybackTrackOption SelectedCaption { get; set; } = new("external:en|dialogue", "English (downloaded)");
        public PlaybackTrackOption[] CaptionOptions => [SelectedCaption];
        public PlaybackTrackOption SelectedAudioTrack { get; set; } = new("native:eng:3", "English · ENG", 3);
        public PlaybackTrackOption[] AudioOptions => [SelectedAudioTrack];
        public PlaybackQualityOption SelectedQuality { get; set; } = new("720p", "https://fixture.invalid/master/720.m3u8", 720);
        public PlaybackQualityOption[] QualityOptions => [SelectedQuality];
    }
}
