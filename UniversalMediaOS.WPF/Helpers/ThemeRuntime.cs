using System;
using System.Windows;
using System.Windows.Media;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.WPF.Helpers
{
    public static class ThemeRuntime
    {
        public static void ApplyFromConfig(DomainHotSwapper config)
        {
            Apply(config.GetSetting("IsDarkMode") != "false", config.GetSetting("AccentColor"));
            ApplyUiMetrics(
                config.GetSetting("UiDensity"),
                int.TryParse(config.GetSetting("CornerRadiusPreview"), out int radius) ? radius : 8,
                config.GetSetting("ReduceMotion") == "true");
            LocalizationRuntime.SetLanguage(config.GetSetting("SelectedLanguage"));
        }

        public static void Apply(bool isDarkMode, string? accentColor)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => Apply(isDarkMode, accentColor));
                return;
            }

            if (Application.Current?.Resources is { } resources)
                ApplyPalette(resources, isDarkMode, accentColor);
        }

        internal static void ApplyPalette(ResourceDictionary resources, bool isDarkMode, string? accentColor)
        {
            var accent = ResolveAccent(accentColor);
            ApplyBrush(resources, "AccentPrimary", accent);
            ApplyBrush(resources, "AccentSoft", Color.FromArgb(0x24, accent.R, accent.G, accent.B));
            ApplyBrush(resources, SystemColors.HighlightBrushKey, accent);
            ApplyBrush(resources, SystemColors.HighlightTextBrushKey, Color.FromRgb(0x03, 0x14, 0x12));

            ApplyBrush(resources, "AccentText", Colors.Black);
            ApplyBrush(resources, "DangerText", Colors.Black);
            ApplyBrush(resources, "ControlHoverBackground", isDarkMode ? Color.FromRgb(0x26, 0x34, 0x49) : Color.FromRgb(0xDB, 0xE5, 0xF1));
            ApplyBrush(resources, "ControlPressedBackground", isDarkMode ? Color.FromRgb(0x33, 0x41, 0x55) : Color.FromRgb(0xCB, 0xD5, 0xE1));
            ApplyBrush(resources, "FocusBorder", isDarkMode ? Color.FromRgb(0x38, 0xBD, 0xF8) : Color.FromRgb(0x03, 0x69, 0xA1));
            ApplyBrush(resources, SystemColors.WindowTextBrushKey, isDarkMode ? Colors.White : Color.FromRgb(0x0F, 0x17, 0x2A));
            ApplyBrush(resources, SystemColors.ControlTextBrushKey, isDarkMode ? Colors.White : Color.FromRgb(0x0F, 0x17, 0x2A));
            ApplyBrush(resources, SystemColors.GrayTextBrushKey, isDarkMode ? Color.FromRgb(0x94, 0xA3, 0xB8) : Color.FromRgb(0x64, 0x74, 0x8B));

            if (isDarkMode)
            {
                ApplyBrush(resources, "BgApp", Color.FromRgb(0x07, 0x0A, 0x12));
                ApplyBrush(resources, "BgChrome", Color.FromArgb(0xF2, 0x0A, 0x0F, 0x1D));
                ApplyBrush(resources, "BgPanel", Color.FromArgb(0xF2, 0x14, 0x1A, 0x2A));
                ApplyBrush(resources, "BgPanelAlt", Color.FromRgb(0x10, 0x18, 0x27));
                ApplyBrush(resources, "BgElevated", Color.FromRgb(0x18, 0x20, 0x33));
                ApplyBrush(resources, "BgInput", Color.FromRgb(0x0F, 0x16, 0x26));
                ApplyBrush(resources, "TextPrimary", Colors.White);
                ApplyBrush(resources, "TextSecondary", Color.FromRgb(0xCB, 0xD5, 0xE1));
                ApplyBrush(resources, "TextMuted", Color.FromRgb(0x94, 0xA3, 0xB8));
                ApplyBrush(resources, "BorderSubtle", Color.FromRgb(0x2A, 0x2A, 0x35));
                ApplyBrush(resources, "BorderStrong", Color.FromRgb(0x33, 0x41, 0x55));
            }
            else
            {
                ApplyBrush(resources, "BgApp", Color.FromRgb(0xF7, 0xF8, 0xFC));
                ApplyBrush(resources, "BgChrome", Color.FromRgb(0xEC, 0xF0, 0xF7));
                ApplyBrush(resources, "BgPanel", Color.FromRgb(0xFF, 0xFF, 0xFF));
                ApplyBrush(resources, "BgPanelAlt", Color.FromRgb(0xF1, 0xF5, 0xF9));
                ApplyBrush(resources, "BgElevated", Color.FromRgb(0xEA, 0xF0, 0xF7));
                ApplyBrush(resources, "BgInput", Color.FromRgb(0xFF, 0xFF, 0xFF));
                ApplyBrush(resources, "TextPrimary", Color.FromRgb(0x0F, 0x17, 0x2A));
                ApplyBrush(resources, "TextSecondary", Color.FromRgb(0x33, 0x41, 0x55));
                ApplyBrush(resources, "TextMuted", Color.FromRgb(0x64, 0x74, 0x8B));
                ApplyBrush(resources, "BorderSubtle", Color.FromRgb(0xCB, 0xD5, 0xE1));
                ApplyBrush(resources, "BorderStrong", Color.FromRgb(0x94, 0xA3, 0xB8));
            }
        }

        private static void ApplyBrush(ResourceDictionary resources, object key, Color color)
        {
            // Preserve live brush instances so both static and dynamic consumers update.
            if (resources[key] is SolidColorBrush existing && !existing.IsFrozen)
            {
                existing.Color = color;
            }
            else
            {
                resources[key] = new SolidColorBrush(color);
            }
        }

        public static void ApplyUiMetrics(string? density, int cornerRadius, bool reduceMotion)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => ApplyUiMetrics(density, cornerRadius, reduceMotion));
                return;
            }

            var resources = Application.Current?.Resources;
            if (resources == null)
            {
                return;
            }

            int radius = Math.Clamp(cornerRadius, 0, 16);
            resources["RadiusSm"] = new CornerRadius(Math.Max(0, radius - 4));
            resources["RadiusMd"] = new CornerRadius(radius);
            resources["RadiusLg"] = new CornerRadius(Math.Min(20, radius + 4));

            switch ((density ?? "Comfortable").Trim().ToLowerInvariant())
            {
                case "compact":
                    ApplyDensity(new Thickness(12), new Thickness(10, 6, 10, 6), new Thickness(10, 7, 10, 7),
                        new Thickness(10, 7, 10, 7), new Thickness(9, 6, 9, 6), 30, 34, 13, 13);
                    break;
                case "spacious":
                    ApplyDensity(new Thickness(20), new Thickness(16, 10, 16, 10), new Thickness(14, 12, 14, 12),
                        new Thickness(14, 11, 14, 11), new Thickness(12, 9, 12, 9), 38, 44, 14, 15);
                    break;
                default:
                    ApplyDensity(new Thickness(16), new Thickness(14, 8, 14, 8), new Thickness(12, 10, 12, 10),
                        new Thickness(12, 9, 12, 9), new Thickness(10, 7, 10, 7), 34, 38, 14, 14);
                    break;
            }

            resources["MotionFast"] = new Duration(reduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(140));
            resources["MotionStandard"] = new Duration(reduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(240));
            resources["MotionEmphasized"] = new Duration(reduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(420));
        }

        private static void ApplyDensity(
            Thickness surfaceCardPadding,
            Thickness appButtonPadding,
            Thickness navButtonPadding,
            Thickness textBoxPadding,
            Thickness comboBoxPadding,
            double appButtonMinHeight,
            double appInputMinHeight,
            double appControlFontSize,
            double navButtonFontSize)
        {
            var resources = Application.Current?.Resources;
            if (resources == null)
            {
                return;
            }

            resources["SurfaceCardPadding"] = surfaceCardPadding;
            resources["AppButtonPadding"] = appButtonPadding;
            resources["NavButtonPadding"] = navButtonPadding;
            resources["AppTextBoxPadding"] = textBoxPadding;
            resources["AppComboBoxPadding"] = comboBoxPadding;
            resources["CompactInputPadding"] = new Thickness(appControlFontSize <= 13 ? 7 : 8, 4, 7, 4);
            resources["CompactInputHeight"] = Math.Clamp(appInputMinHeight - 4, 30, 36);
            resources["AppButtonMinHeight"] = appButtonMinHeight;
            resources["AppInputMinHeight"] = appInputMinHeight;
            resources["AppControlFontSize"] = appControlFontSize;
            resources["NavButtonFontSize"] = navButtonFontSize;
        }

        private static Color ResolveAccent(string? name)
        {
            return (name ?? "Teal").Trim().ToLowerInvariant() switch
            {
                "purple" => Color.FromRgb(0x8B, 0x5C, 0xF6),
                "pink" => Color.FromRgb(0xEC, 0x48, 0x99),
                "cyan" => Color.FromRgb(0x06, 0xB6, 0xD4),
                "green" => Color.FromRgb(0x10, 0xB9, 0x81),
                "amber" => Color.FromRgb(0xF5, 0x9E, 0x0B),
                "red" => Color.FromRgb(0xEF, 0x44, 0x44),
                _ => Color.FromRgb(0x14, 0xB8, 0xA6)
            };
        }
    }
}
