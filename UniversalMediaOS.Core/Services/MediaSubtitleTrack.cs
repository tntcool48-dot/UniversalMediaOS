using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.RegularExpressions;

namespace UniversalMediaOS.Core.Services;

public sealed record MediaSubtitleTrack(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("label")] string Label = "",
    [property: JsonPropertyName("language")] string Language = "",
    [property: JsonPropertyName("default")] bool IsDefault = false,
    [property: JsonPropertyName("user_agent")] string UserAgent = "",
    [property: JsonPropertyName("cookie")] string Cookie = "",
    [property: JsonPropertyName("referer")] string Referer = "",
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? RequestHeaders = null)
{
    [JsonPropertyName("inline_vtt")]
    public string InlineVtt { get; init; } = string.Empty;

    internal static string NormalizeInlineVtt(string? text) =>
        text is { Length: > 0 } && text.Length <= 2 * 1024 * 1024 &&
        Encoding.UTF8.GetByteCount(text) <= 2 * 1024 * 1024 && text.StartsWith("WEBVTT\n", StringComparison.Ordinal) &&
        !Regex.IsMatch(text, @"<(?:html|!doctype|script)\b", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(text, @"(?m)^\d{2,}:\d{2}:\d{2}\.\d{3}\s+-->\s+\d{2,}:\d{2}:\d{2}\.\d{3}")
            ? text : string.Empty;

    public bool IsEnglish => Language.Equals("en", StringComparison.OrdinalIgnoreCase) ||
        Language.Equals("eng", StringComparison.OrdinalIgnoreCase) ||
        Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
        Label.Contains("English", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<MediaSubtitleTrack> Copy(IEnumerable<MediaSubtitleTrack>? tracks) =>
        (tracks ?? []).Where(track => track != null &&
            Uri.TryCreate(track.Url, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo))
        .DistinctBy(track => track.Url, StringComparer.Ordinal).Take(12)
        .Select(track => track with
        {
            Label = track.Label ?? string.Empty,
            Language = track.Language ?? string.Empty,
            InlineVtt = NormalizeInlineVtt(track.InlineVtt),
            RequestHeaders = new Dictionary<string, string>(
                track.RequestHeaders ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        }).ToArray();
}
