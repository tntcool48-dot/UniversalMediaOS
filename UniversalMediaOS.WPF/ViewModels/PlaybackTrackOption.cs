using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UniversalMediaOS.Core.Services;

namespace UniversalMediaOS.WPF.ViewModels;

public sealed record PlaybackTrackOption(string Key, string Label, int NativeId = -1,
    MediaSubtitleTrack? Subtitle = null)
{
    internal string NativeLanguage { get; init; } = string.Empty;

    public override string ToString() => Label;

    internal static PlaybackTrackOption? RememberedNativeAudio(
        IReadOnlyList<PlaybackTrackOption> choices, string? key, string? language)
    {
        var exact = choices.Where(choice => choice.Key == key).Take(2).ToArray();
        if (exact.Length == 1) return exact[0];
        if (string.IsNullOrEmpty(language)) return null;
        var sameLanguage = choices.Where(choice => choice.NativeLanguage == language).Take(2).ToArray();
        return sameLanguage.Length == 1 ? sameLanguage[0] : null;
    }

    internal static IReadOnlyList<PlaybackTrackOption> ExternalCaptions(IReadOnlyList<MediaSubtitleTrack> tracks)
    {
        var keys = tracks.Select(track => $"external:{LanguageCode(track.Language)}|{track.Label.Trim().ToLowerInvariant()}").ToArray();
        return tracks.Select((track, index) => new PlaybackTrackOption(
            // Missing or duplicate metadata cannot identify a choice across episodes.
            keys.Count(key => key == keys[index]) > 1 || keys[index] == "external:|"
                ? $"external-url:{track.Url}" : keys[index],
            string.IsNullOrWhiteSpace(track.Label) ? LanguageName(track.Language) ?? $"Subtitle {index + 1}" : track.Label,
            Subtitle: track)).ToArray();
    }

    internal static PlaybackTrackOption Native(int id, string name, string language, string description, string kind)
    {
        language = LanguageCode(language);
        description = description.Trim();
        name = name.Trim();
        string label = LanguageName(language) ?? name;
        if (string.IsNullOrWhiteSpace(label)) label = $"{kind} {id}";
        bool redundantDescription = description.Equals($"audio {label}", StringComparison.OrdinalIgnoreCase) ||
            description.Equals($"subtitle {label}", StringComparison.OrdinalIgnoreCase);
        if (!redundantDescription && !string.IsNullOrWhiteSpace(description) && !label.Contains(description, StringComparison.OrdinalIgnoreCase))
            label += $" · {description}";
        // Track numbers are decoder-local; never treat them as a remembered language.
        string key = string.IsNullOrEmpty(language) && string.IsNullOrEmpty(description)
            ? $"unverified:{kind}:{id}" : $"{kind}:{language}|{description.ToLowerInvariant()}";
        return new(key, label, id) { NativeLanguage = KnownLanguages.Contains(language) ? language : string.Empty };
    }

    private static readonly CultureInfo[] Languages = CultureInfo.GetCultures(CultureTypes.NeutralCultures);
    private static readonly HashSet<string> KnownLanguages = CultureInfo.GetCultures(CultureTypes.AllCultures)
        .Where(culture => culture.Name.Length > 0).Select(culture => culture.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string LanguageCode(string value)
    {
        string code = value.Trim().ToLowerInvariant().Replace('_', '-');
        if (code is "" or "und" or "unknown") return "";
        return Languages.FirstOrDefault(culture => culture.TwoLetterISOLanguageName == code ||
            culture.ThreeLetterISOLanguageName == code ||
            culture.EnglishName.Equals(code, StringComparison.OrdinalIgnoreCase) ||
            culture.NativeName.Equals(code, StringComparison.OrdinalIgnoreCase))?.TwoLetterISOLanguageName ?? code;
    }

    private static string? LanguageName(string value)
    {
        string code = LanguageCode(value);
        if (string.IsNullOrEmpty(code)) return null;
        try { return CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (CultureNotFoundException) { return code; }
    }
}
