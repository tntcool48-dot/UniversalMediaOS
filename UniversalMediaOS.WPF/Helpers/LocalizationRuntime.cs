using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace UniversalMediaOS.WPF.Helpers
{
    public static class LocalizationRuntime
    {
        private static readonly DependencyProperty OriginalTextProperty =
            DependencyProperty.RegisterAttached(
                "OriginalText",
                typeof(string),
                typeof(LocalizationRuntime),
                new PropertyMetadata(null));

        private static string _language = "English";
        public static event EventHandler? LanguageChanged;

        private static readonly Dictionary<string, string> Arabic = new(StringComparer.Ordinal)
        {
            ["Anime"] = "أنمي",
            ["Manga"] = "مانغا",
            ["Movies"] = "أفلام",
            ["Books"] = "كتب",
            ["TV Shows"] = "مسلسلات",
            ["Cartoons"] = "رسوم متحركة",
            ["Watch Together"] = "مشاهدة جماعية",
            ["My List"] = "قائمتي",
            ["Downloads"] = "التنزيلات",
            ["Settings"] = "الإعدادات",
            ["SETTINGS"] = "الإعدادات",
            ["Appearance"] = "المظهر",
            ["Services"] = "الخدمات",
            ["Torrent Engine"] = "محرك التورنت",
            ["qBittorrent"] = "qBittorrent",
            ["MAL"] = "MyAnimeList",
            ["Custom Sources"] = "مصادر مخصصة",
            ["Notifications"] = "الإشعارات",
            ["Privacy"] = "الخصوصية",
            ["Language"] = "اللغة",
            ["Dark Mode"] = "الوضع الداكن",
            ["Accent Color"] = "لون التمييز",
            ["UI Zoom"] = "تكبير الواجهة",
            ["Layout Density"] = "كثافة التخطيط",
            ["Poster Image Fit"] = "ملاءمة صورة الملصق",
            ["Corner Radius"] = "استدارة الزوايا",
            ["Show service status bar"] = "إظهار شريط حالة الخدمات",
            ["Reduce motion"] = "تقليل الحركة",
            ["Start maximized"] = "البدء مكبرا",
            ["Compact"] = "مضغوط",
            ["Comfortable"] = "مريح",
            ["Spacious"] = "واسع",
            ["Contain"] = "احتواء",
            ["Cover"] = "تغطية",
            ["Smart crop"] = "قص ذكي",
            ["Reset"] = "إعادة ضبط",
            ["Save Settings"] = "حفظ الإعدادات",
            ["Auto-manage services"] = "إدارة الخدمات تلقائيا",
            ["Scraper site attempt limit"] = "حد محاولات مواقع الكشط",
            ["Auto-play after download"] = "التشغيل تلقائيا بعد التنزيل",
            ["Download directory"] = "مجلد التنزيل",
            ["Browse..."] = "استعراض...",
            ["Enable debug logging"] = "تفعيل سجل التصحيح",
            ["Refresh log size"] = "تحديث حجم السجل",
            ["Clear debug log"] = "مسح سجل التصحيح",
            ["Refresh"] = "تحديث",
            ["Host"] = "المضيف",
            ["Port"] = "المنفذ",
            ["Username"] = "اسم المستخدم",
            ["Password"] = "كلمة المرور",
            ["Open MAL API setup"] = "فتح إعدادات MyAnimeList API",
            ["Add Provider"] = "إضافة مزود",
            ["Delete"] = "حذف",
            ["New episode alerts"] = "تنبيهات الحلقات الجديدة",
            ["Auto-sync MAL progress"] = "مزامنة تقدم MAL تلقائيا",
            ["Show adult content in search"] = "إظهار محتوى البالغين في البحث",
            ["English"] = "الإنجليزية",
            ["Arabic"] = "العربية",
            ["No tabs are open"] = "لا توجد تبويبات مفتوحة",
            ["Service Health"] = "حالة الخدمات",
            ["Python Scraper"] = "كاشط بايثون",
            ["HLS Proxy"] = "وسيط HLS",
            ["uBlock Origin"] = "uBlock Origin",
            ["Ready"] = "جاهز",
            ["Listening"] = "قيد الاستماع",
            ["Loaded"] = "محمل",
            ["Detected"] = "مكتشف",
            ["Optional"] = "اختياري",
            ["Play"] = "تشغيل",
            ["Pause"] = "إيقاف مؤقت",
            ["Web"] = "ويب",
            ["Stop"] = "إيقاف",
            ["Vol"] = "الصوت",
            ["Full"] = "ملء الشاشة",
            ["CC"] = "ترجمة",
            ["Site CC"] = "ترجمة الموقع",
            ["CC Off"] = "إيقاف الترجمة",
            ["Back to Search"] = "العودة إلى البحث",
            ["Watch Options"] = "خيارات المشاهدة",
            ["Watch Now"] = "شاهد الآن",
            ["Episodes"] = "الحلقات",
            ["Episode"] = "حلقة",
            ["episodes"] = "حلقة",
            ["of"] = "من",
            ["Sub"] = "مترجم",
            ["Dub"] = "مدبلج",
            ["Favorite"] = "المفضلة",
            ["Search"] = "بحث",
            ["Search anime series..."] = "ابحث عن أنمي...",
            ["Provider name"] = "اسم المزود",
            ["Not connected"] = "غير متصل",
            ["Connected"] = "متصل"
        };

        private static readonly Lazy<Dictionary<string, string>> EnglishByArabic = new(() =>
            Arabic
                .GroupBy(pair => pair.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal));

        public static void SetLanguage(string? language)
        {
            string normalized = IsArabic(language) ? "Arabic" : "English";
            if (_language == normalized)
            {
                return;
            }

            _language = normalized;
            LocalizationBindingSource.Instance.Refresh();
            LanguageChanged?.Invoke(null, EventArgs.Empty);
            ApplyToOpenWindows();
        }

        public static bool IsArabic(string? language) =>
            string.Equals(language, "Arabic", StringComparison.OrdinalIgnoreCase);

        public static string Translate(string text) =>
            _language == "Arabic" && Arabic.TryGetValue(text, out var translated)
                ? translated
                : text;

        public static void EnableAutoApply(FrameworkElement root, Func<bool> isArabic)
        {
            root.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is DependencyObject dependencyObject)
                {
                    ApplyToVisualTree(dependencyObject, isArabic());
                }
            }), true);

        }

        public static void ApplyToOpenWindows()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.Invoke(ApplyToOpenWindows);
                return;
            }

            bool arabic = _language == "Arabic";
            var app = Application.Current;
            if (app == null)
            {
                return;
            }

            foreach (Window window in app.Windows)
            {
                ApplyToVisualTree(window, arabic);
            }
        }

        private static void ApplyToVisualTree(DependencyObject root, bool arabic)
        {
            TranslateElement(root, arabic);
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                ApplyToVisualTree(VisualTreeHelper.GetChild(root, i), arabic);
            }
        }

        private static void TranslateElement(DependencyObject element, bool arabic)
        {
            switch (element)
            {
                case TextBlock textBlock when BindingOperations.GetBindingExpression(textBlock, TextBlock.TextProperty) == null:
                    textBlock.Text = TranslateStoredText(textBlock, textBlock.Text, arabic);
                    break;

                case ContentControl contentControl
                    when contentControl.Content is string content &&
                         BindingOperations.GetBindingExpression(contentControl, ContentControl.ContentProperty) == null:
                    contentControl.Content = TranslateStoredText(contentControl, content, arabic);
                    break;
            }

            if (element is FrameworkElement frameworkElement &&
                frameworkElement.ToolTip is string tooltip)
            {
                frameworkElement.ToolTip = TranslateStoredText(frameworkElement, tooltip, arabic);
            }
        }

        private static string TranslateStoredText(DependencyObject owner, string current, bool arabic)
        {
            string? stored = (string?)owner.GetValue(OriginalTextProperty);
            string original = stored ?? RestoreEnglishIfTranslated(current);
            owner.SetValue(OriginalTextProperty, original);

            if (!arabic)
            {
                return original;
            }

            return Arabic.TryGetValue(original, out string? translated)
                ? translated
                : current;
        }

        private static string RestoreEnglishIfTranslated(string current)
        {
            if (EnglishByArabic.Value.TryGetValue(current, out string? english))
            {
                return english;
            }

            return current;
        }
    }

    public sealed class LocalizationBindingSource : INotifyPropertyChanged
    {
        public static LocalizationBindingSource Instance { get; } = new();

        private LocalizationBindingSource()
        {
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string this[string key] => LocalizationRuntime.Translate(key);

        internal void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    [MarkupExtensionReturnType(typeof(object))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension()
        {
        }

        public LocExtension(string key)
        {
            Key = key;
        }

        public string Key { get; set; } = string.Empty;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return new Binding($"[{Key}]")
            {
                Source = LocalizationBindingSource.Instance,
                Mode = BindingMode.OneWay
            }.ProvideValue(serviceProvider);
        }
    }
}
