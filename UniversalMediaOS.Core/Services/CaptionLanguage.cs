using System.Globalization;

namespace UniversalMediaOS.Core.Services;

/// <summary>Language tags of caption and audio tracks, as decoders, providers and file names declare them.</summary>
public static class CaptionLanguage
{
    // Matroska and other containers commonly tag tracks with ISO 639-2/B codes
    // (Sintel's German track is "ger"); .NET cultures only know the /T form.
    private static readonly Dictionary<string, string> Bibliographic = new(StringComparer.Ordinal)
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho",
        ["cze"] = "ces", ["dut"] = "nld", ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu",
        ["gre"] = "ell", ["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa",
        ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk", ["tib"] = "bod", ["wel"] = "cym"
    };

    private static readonly CultureInfo[] Neutral = CultureInfo.GetCultures(CultureTypes.NeutralCultures);
    private static readonly HashSet<string> Known = CultureInfo.GetCultures(CultureTypes.AllCultures)
        .Where(culture => culture.Name.Length > 0).Select(culture => culture.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The two-letter code for a declared code or language name; an unrecognized tag is returned
    /// lower-cased, and an empty or undetermined one as empty.</summary>
    public static string Normalize(string? value)
    {
        string code = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        if (code is "" or "und" or "unknown") return string.Empty;
        if (Bibliographic.TryGetValue(code, out string? terminology)) code = terminology;
        return Neutral.FirstOrDefault(culture => culture.TwoLetterISOLanguageName == code ||
            culture.ThreeLetterISOLanguageName == code ||
            culture.EnglishName.Equals(code, StringComparison.OrdinalIgnoreCase) ||
            culture.NativeName.Equals(code, StringComparison.OrdinalIgnoreCase))?.TwoLetterISOLanguageName ?? code;
    }

    /// <summary>Whether a normalized code names an actual language rather than an arbitrary tag.</summary>
    public static bool IsKnown(string code) => code.Length > 0 && Known.Contains(code);

    public static string? EnglishName(string? value)
    {
        string code = Normalize(value);
        if (code.Length == 0) return null;
        try { return CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (CultureNotFoundException) { return code; }
    }

    /// <summary>The language a caption file declares by its last name segment, such as
    /// "caption-02.fr.vtt" or "Episode 01.fre.ass"; empty when it declares none.</summary>
    public static string FromFileName(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        int dot = stem.LastIndexOf('.');
        if (dot < 0) return string.Empty;
        string code = Normalize(stem[(dot + 1)..]);
        return IsKnown(code) ? code : string.Empty;
    }

    /// <summary>The name segment that keeps a saved caption's declared language, such as ".fr";
    /// empty when the declared tag is not a known language.</summary>
    public static string FileSuffix(string? value)
    {
        string code = Normalize(value);
        return IsKnown(code) && code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            ? "." + code : string.Empty;
    }
}
