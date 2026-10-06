using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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

        private static readonly DependencyProperty OriginalToolTipProperty =
            DependencyProperty.RegisterAttached("OriginalToolTip", typeof(string), typeof(LocalizationRuntime),
                new PropertyMetadata(null));

        private static readonly DependencyProperty AutoApplyProperty =
            DependencyProperty.RegisterAttached("AutoApply", typeof(bool), typeof(LocalizationRuntime),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, OnAutoApplyChanged));

        private static string _language = "English";
        internal static string CurrentLanguage => _language;
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
            ["Open folder"] = "فتح المجلد",
            ["Read"] = "قراءة",
            ["Local episodes, season packs, and readable files ready for playback."] = "حلقات محلية ومواسم وملفات جاهزة للمشاهدة أو القراءة.",
            ["Download queue"] = "قائمة التنزيل",
            ["Jobs are saved across restarts. Pause stops the torrent before the worker exits; partial data is kept for resume."] = "تُحفظ التنزيلات بعد إعادة التشغيل. يوقف الإيقاف المؤقت التورنت مع الاحتفاظ بالبيانات الجزئية للاستكمال.",
            ["No downloaded files yet"] = "لا توجد ملفات منزلة بعد",
            ["Queued seasons and local media will appear here after refresh."] = "ستظهر المواسم والملفات المحلية هنا بعد التحديث.",
            ["Play this downloaded file."] = "شغّل هذا الملف المنزل.",
            ["Delete this local downloaded file."] = "احذف هذا الملف المحلي المنزل.",
            ["Rescan the download folder and refresh this list."] = "أعد فحص مجلد التنزيل وتحديث القائمة.",
            ["Open the downloads folder in File Explorer."] = "افتح مجلد التنزيل في مستكشف الملفات.",
            ["Set your preferred display language"] = "اختر لغة عرض التطبيق",
            ["Settings Saved"] = "تم حفظ الإعدادات",
            ["Settings saved. Reopen any Movie, TV, Cartoon, or Book tabs to apply provider changes."] = "تم حفظ الإعدادات. أعد فتح تبويبات الأفلام أو المسلسلات أو الرسوم المتحركة أو الكتب لتطبيق تغييرات المزودين.",
            ["Settings"] = "الإعدادات",
            ["SETTINGS"] = "الإعدادات",
            ["Appearance"] = "المظهر",
            ["Services"] = "الخدمات",
            ["Providers & Scrapers"] = "المزودون والكاشطات",
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
            ["Resume"] = "استكمال",
            ["Cancel"] = "إلغاء",
            ["Retry"] = "إعادة المحاولة",
            ["Remove"] = "إزالة",
            ["Queued"] = "في قائمة الانتظار",
            ["Downloading"] = "جارٍ التنزيل",
            ["Pausing..."] = "جارٍ الإيقاف المؤقت...",
            ["Paused"] = "متوقف مؤقتًا",
            ["Cancelling..."] = "جارٍ الإلغاء...",
            ["Completed"] = "مكتمل",
            ["Failed"] = "فشل",
            ["Cancelled"] = "ملغى",
            ["Waiting in queue"] = "في انتظار التنزيل",
            ["Paused; partial data is available for resume"] = "متوقف مؤقتًا؛ البيانات الجزئية محفوظة للاستكمال",
            ["Download and validation completed"] = "اكتمل التنزيل والتحقق",
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
            // Loaded is direct, so the window's handler cannot see later views.
            // Inherit an instance subscription into newly created descendants;
            // translate only that element, preserving data bindings and avoiding
            // a full-tree walk on every child load.
            root.SetValue(AutoApplyProperty, true);

            root.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is DependencyObject dependencyObject)
                {
                    ApplyToVisualTree(dependencyObject, isArabic());
                }
            }), true);

        }

        private static void OnAutoApplyChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
        {
            if (owner is not FrameworkElement element) return;
            if ((bool)args.NewValue)
            {
                element.Loaded += LocalizedElement_Loaded;
                if (element.IsLoaded) TranslateElement(element, IsArabic(_language));
            }
            else
            {
                element.Loaded -= LocalizedElement_Loaded;
            }
        }

        private static void LocalizedElement_Loaded(object sender, RoutedEventArgs args)
        {
            if (sender is DependencyObject element)
                TranslateElement(element, IsArabic(_language));
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
                case TextBlock textBlock when !BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty) &&
                    // WPF owns the implicit text presenter for string content.
                    // Writing a local Text value here disconnects future selected
                    // content updates, even though IsDataBound reports false.
                    !(textBlock.TemplatedParent is ContentPresenter { Content: string, ContentTemplate: null }):
                    textBlock.Text = TranslateStoredText(textBlock, textBlock.Text, arabic, OriginalTextProperty);
                    break;

                case ContentControl contentControl
                    when contentControl.Content is string content &&
                         !BindingOperations.IsDataBound(contentControl, ContentControl.ContentProperty):
                    contentControl.Content = TranslateStoredText(contentControl, content, arabic, OriginalTextProperty);
                    break;
            }

            if (element is FrameworkElement frameworkElement &&
                frameworkElement.ToolTip is string tooltip &&
                !BindingOperations.IsDataBound(frameworkElement, FrameworkElement.ToolTipProperty))
            {
                frameworkElement.ToolTip = TranslateStoredText(frameworkElement, tooltip, arabic, OriginalToolTipProperty);
            }
        }

        private static string TranslateStoredText(DependencyObject owner, string current, bool arabic,
            DependencyProperty originalProperty)
        {
            string? stored = (string?)owner.GetValue(originalProperty);
            // A selection presenter can be reused with new text. Only keep the
            // original while the displayed value still belongs to that original.
            if (stored != null && current != stored &&
                (!Arabic.TryGetValue(stored, out string? previousTranslation) || current != previousTranslation))
                stored = null;
            string original = stored ?? RestoreEnglishIfTranslated(current);
            owner.SetValue(originalProperty, original);

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

        public string Language => LocalizationRuntime.CurrentLanguage;

        internal void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
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

    // Keep live job messages bound to their source and refresh on language changes.
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class LocValueExtension(string path) : MarkupExtension
    {
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = new LocalizedValueConverter() };
            binding.Bindings.Add(new Binding(path));
            binding.Bindings.Add(new Binding(nameof(LocalizationBindingSource.Language))
                { Source = LocalizationBindingSource.Instance });
            return binding.ProvideValue(serviceProvider);
        }
    }

    public sealed class LocalizedValueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.FirstOrDefault() is string text ? LocalizationRuntime.Translate(text) : string.Empty;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
