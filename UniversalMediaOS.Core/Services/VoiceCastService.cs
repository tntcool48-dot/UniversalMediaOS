using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public sealed class VoiceCastService
    {
        private const string AniListLanguageJapanese = "JAPANESE";
        private const string AniListLanguageEnglish = "ENGLISH";
        private const string AnimeVoiceOverBaseUrl = "https://animevoiceover.fandom.com";
        private const string BtvaBaseUrl = "https://www.behindthevoiceactors.com";
        private const string DesktopUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

        private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex WikiHrefRegex = new("href=[\"'](?<href>/wiki/[^\"'#?]+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BtvaHrefRegex = new("href=[\"'](?<href>/tv-shows/[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RowRegex = new("<tr\\b[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex CellRegex = new("<t[dh]\\b[^>]*>(?<cell>.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex ListItemRegex = new("<li\\b[^>]*>(?<item>.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex VaAsCharacterRegex = new("^(?<va>.{3,90}?)(?:\\s+-\\s+|\\s+–\\s+|\\s+—\\s+|\\s+as\\s+|:\\s+)(?<character>.{2,100})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CharacterParenVaRegex = new("^(?<character>.{2,100}?)\\s*[\\(\\[](?<va>.{3,90}?)[\\)\\]]$", RegexOptions.Compiled);

        private readonly DomainHotSwapper _config;
        private readonly HttpClient _httpClient;

        public VoiceCastService(DomainHotSwapper config)
            : this(config, new HttpClient())
        {
        }

        internal VoiceCastService(DomainHotSwapper config, HttpClient httpClient)
        {
            _config = config;
            _httpClient = httpClient;
        }

        public async Task<VoiceCastFetchResult> FetchAndCacheCastAsync(
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            string manualUrl = "",
            bool bypassCache = false,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(media.MediaKey) || string.IsNullOrWhiteSpace(media.Title))
            {
                return new VoiceCastFetchResult(mode, string.Empty, false, true, Array.Empty<VoiceCastRecord>());
            }

            await using var db = new DatabaseContext();
            db.EnsureVaDetectSchema();

            if (!bypassCache && string.IsNullOrWhiteSpace(manualUrl))
            {
                var cached = await LoadCachedAsync(db, media.MediaKey, mode, token);
                if (cached.Count > 0)
                {
                    bool notFound = cached.All(item => item.NotFound);
                    return new VoiceCastFetchResult(mode, cached[0].Source, true, notFound, cached.Where(item => !item.NotFound).ToList());
                }
            }

            IReadOnlyList<VoiceCastRecord> cast = Array.Empty<VoiceCastRecord>();
            string source = string.Empty;
            string effectiveManualUrl = manualUrl.Trim();

            if (!string.IsNullOrWhiteSpace(effectiveManualUrl))
            {
                cast = await FetchFromManualUrlAsync(media, mode, effectiveManualUrl, token);
                source = "Manual";
            }
            else if (mode == VoiceLanguageMode.Sub)
            {
                cast = await FetchFromAniListAsync(media, mode, AniListLanguageJapanese, token);
                source = cast.Count > 0 ? "AniList Japanese" : string.Empty;
                if (cast.Count == 0)
                {
                    cast = await FetchFromMalCharactersPageAsync(media, mode, token);
                    source = cast.Count > 0 ? "MAL characters" : source;
                }
            }
            else
            {
                cast = await FetchFromAniListAsync(media, mode, AniListLanguageEnglish, token);
                source = cast.Count > 0 ? "AniList English" : string.Empty;
                if (cast.Count == 0)
                {
                    cast = await FetchFromAnimeVoiceOverAsync(media, mode, token);
                    source = cast.Count > 0 ? "AnimeVoiceOver" : source;
                }
                if (cast.Count == 0)
                {
                    cast = await FetchFromBtvaAsync(media, mode, token);
                    source = cast.Count > 0 ? "BTVA" : source;
                }
            }

            if (cast.Count == 0)
            {
                source = string.IsNullOrWhiteSpace(source) ? "Not found" : source;
                await ReplaceCachedAsync(db, media, mode, source, Array.Empty<VoiceCastRecord>(), effectiveManualUrl, notFound: true, token);
                return new VoiceCastFetchResult(mode, source, false, true, Array.Empty<VoiceCastRecord>());
            }

            await ReplaceCachedAsync(db, media, mode, source, cast, effectiveManualUrl, notFound: false, token);
            var saved = await LoadCachedAsync(db, media.MediaKey, mode, token);
            return new VoiceCastFetchResult(mode, source, false, false, saved.Where(item => !item.NotFound).ToList());
        }

        public async Task<IReadOnlyList<MalLibraryEntry>> GetLibraryEntriesMissingCastAsync(
            VoiceLanguageMode mode,
            int maxItems,
            CancellationToken token = default)
        {
            await using var db = new DatabaseContext();
            db.EnsureVaDetectSchema();
            string modeText = mode.ToString();
            var entries = await db.MalLibraryEntries
                .OrderByDescending(entry => entry.UserScore)
                .ThenBy(entry => entry.DefaultTitle)
                .ToListAsync(token);

            return entries
                .Where(entry => !db.VoiceCastRecords.Any(record => record.MediaKey == $"mal:{entry.MalId}" && record.LanguageMode == modeText))
                .Take(Math.Max(1, maxItems))
                .ToList();
        }

        private async Task<IReadOnlyList<VoiceCastRecord>> FetchFromAniListAsync(
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            string language,
            CancellationToken token)
        {
            int aniListId = media.AniListId;
            if (aniListId <= 0 && media.MalId > 0)
            {
                aniListId = await ResolveAniListIdFromMalIdAsync(media.MalId, token);
                if (aniListId > 0)
                {
                    await UpdateLibraryAniListIdAsync(media.MalId, aniListId, token);
                }
            }

            if (aniListId <= 0)
            {
                return Array.Empty<VoiceCastRecord>();
            }

            string gql = """
                query ($id: Int, $language: StaffLanguage) {
                  Media(id: $id, type: ANIME) {
                    id
                    idMal
                    title { romaji english native }
                    coverImage { large }
                    characters(sort: ROLE_DESC, page: 1, perPage: 50) {
                      edges {
                        role
                        node {
                          name { full }
                          image { large }
                        }
                        voiceActors(language: $language) {
                          name { full }
                          image { large }
                        }
                      }
                    }
                  }
                }
                """;

            using var doc = await PostAniListAsync(gql, new { id = aniListId, language }, token);
            return ParseAniListCast(doc.RootElement, media, mode, language == AniListLanguageJapanese ? "AniList Japanese" : "AniList English");
        }

        private async Task<int> ResolveAniListIdFromMalIdAsync(int malId, CancellationToken token)
        {
            string gql = """
                query ($idMal: Int) {
                  Media(idMal: $idMal, type: ANIME) {
                    id
                  }
                }
                """;

            try
            {
                using var doc = await PostAniListAsync(gql, new { idMal = malId }, token);
                return doc.RootElement.TryGetProperty("data", out var data) &&
                       data.TryGetProperty("Media", out var media) &&
                       media.ValueKind == JsonValueKind.Object &&
                       media.TryGetProperty("id", out var id) &&
                       id.ValueKind == JsonValueKind.Number
                    ? id.GetInt32()
                    : 0;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"VA Detect AniList id lookup failed for MAL {malId}: {ex.Message}", "WARNING");
                return 0;
            }
        }

        private async Task<JsonDocument> PostAniListAsync(string query, object variables, CancellationToken token)
        {
            string url = _config.GetSetting("AniListUrl");
            if (string.IsNullOrWhiteSpace(url))
            {
                url = "https://graphql.anilist.co";
            }

            string payload = JsonSerializer.Serialize(new { query, variables });
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.UserAgent.ParseAdd("UniversalMediaOS/1.0");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request, token);
            string json = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"AniList returned {(int)response.StatusCode}: {json}");
            }

            return JsonDocument.Parse(json);
        }

        private static IReadOnlyList<VoiceCastRecord> ParseAniListCast(
            JsonElement root,
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            string source)
        {
            var records = new List<VoiceCastRecord>();
            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("Media", out var mediaElement) ||
                mediaElement.ValueKind != JsonValueKind.Object ||
                !mediaElement.TryGetProperty("characters", out var characters) ||
                !characters.TryGetProperty("edges", out var edges) ||
                edges.ValueKind != JsonValueKind.Array)
            {
                return records;
            }

            foreach (var edge in edges.EnumerateArray())
            {
                string roleType = TryGetString(edge, "role").Equals("MAIN", StringComparison.OrdinalIgnoreCase)
                    ? "Main"
                    : "Supporting";
                if (!edge.TryGetProperty("node", out var node))
                {
                    continue;
                }

                string character = TryGetNestedString(node, "name", "full");
                string characterImage = TryGetNestedString(node, "image", "large");
                if (!edge.TryGetProperty("voiceActors", out var voiceActors) || voiceActors.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var va in voiceActors.EnumerateArray())
                {
                    string vaName = TryGetNestedString(va, "name", "full");
                    if (string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(vaName))
                    {
                        continue;
                    }

                    records.Add(CreateRecord(media, mode, source, roleType, character, characterImage, vaName, TryGetNestedString(va, "image", "large")));
                }
            }

            return records
                .GroupBy(record => $"{record.CharacterName}|{record.VoiceActorName}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private async Task<IReadOnlyList<VoiceCastRecord>> FetchFromMalCharactersPageAsync(
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            CancellationToken token)
        {
            if (media.MalId <= 0)
            {
                return Array.Empty<VoiceCastRecord>();
            }

            string url = $"https://myanimelist.net/anime/{media.MalId}/characters";
            string html = await GetStringAsync(url, token);
            return ParseGenericCastHtml(html, media, mode, "MAL characters");
        }

        private async Task<IReadOnlyList<VoiceCastRecord>> FetchFromManualUrlAsync(
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            string manualUrl,
            CancellationToken token)
        {
            string html = await GetStringAsync(manualUrl, token);
            return ParseGenericCastHtml(html, media, mode, "Manual");
        }

        private async Task<IReadOnlyList<VoiceCastRecord>> FetchFromAnimeVoiceOverAsync(
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            CancellationToken token)
        {
            string searchTitle = Uri.EscapeDataString(SelectBestTitle(media));
            string searchHtml = await GetStringAsync($"{AnimeVoiceOverBaseUrl}/wiki/Special:Search?query={searchTitle}", token);
            string? href = WikiHrefRegex.Matches(searchHtml)
                .Select(match => match.Groups["href"].Value)
                .FirstOrDefault(href => !href.Contains("Special:", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(href))
            {
                return Array.Empty<VoiceCastRecord>();
            }

            string html = await GetStringAsync(AnimeVoiceOverBaseUrl + href, token);
            return ParseGenericCastHtml(html, media, mode, "AnimeVoiceOver");
        }

        private async Task<IReadOnlyList<VoiceCastRecord>> FetchFromBtvaAsync(
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            CancellationToken token)
        {
            string searchTitle = Uri.EscapeDataString(SelectBestTitle(media));
            string searchHtml = await GetStringAsync($"{BtvaBaseUrl}/search?q={searchTitle}", token);
            string? href = BtvaHrefRegex.Matches(searchHtml)
                .Select(match => match.Groups["href"].Value)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(href))
            {
                return Array.Empty<VoiceCastRecord>();
            }

            string html = await GetStringAsync(BtvaBaseUrl + href, token);
            return ParseGenericCastHtml(html, media, mode, "BTVA");
        }

        private async Task<string> GetStringAsync(string url, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(DesktopUserAgent);
            using var response = await _httpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            return await response.Content.ReadAsStringAsync(token);
        }

        private static IReadOnlyList<VoiceCastRecord> ParseGenericCastHtml(
            string html,
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            string source)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return Array.Empty<VoiceCastRecord>();
            }

            var records = new List<VoiceCastRecord>();
            foreach (Match rowMatch in RowRegex.Matches(html))
            {
                var cells = CellRegex.Matches(rowMatch.Groups["row"].Value)
                    .Select(match => CleanText(match.Groups["cell"].Value))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
                if (cells.Length < 2)
                {
                    continue;
                }

                string character = cells[0];
                string va = mode == VoiceLanguageMode.Sub
                    ? cells.Skip(1).FirstOrDefault(text => !LooksEnglishHeader(text)) ?? string.Empty
                    : cells.LastOrDefault() ?? string.Empty;

                AddIfValid(records, media, mode, source, "Supporting", character, va);
            }

            foreach (Match liMatch in ListItemRegex.Matches(html))
            {
                string text = CleanText(liMatch.Groups["item"].Value);
                var parsed = TryParseCastLine(text);
                if (parsed.HasValue)
                {
                    AddIfValid(records, media, mode, source, "Supporting", parsed.Value.Character, parsed.Value.Va);
                }
            }

            return records
                .GroupBy(record => $"{record.CharacterName}|{record.VoiceActorName}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(120)
                .ToList();
        }

        private static (string Character, string Va)? TryParseCastLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var match = VaAsCharacterRegex.Match(text);
            if (match.Success)
            {
                return (CleanCastName(match.Groups["character"].Value), CleanCastName(match.Groups["va"].Value));
            }

            match = CharacterParenVaRegex.Match(text);
            return match.Success
                ? (CleanCastName(match.Groups["character"].Value), CleanCastName(match.Groups["va"].Value))
                : null;
        }

        private static void AddIfValid(
            List<VoiceCastRecord> records,
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            string source,
            string roleType,
            string character,
            string va)
        {
            character = CleanCastName(character);
            va = CleanCastName(va);
            if (string.IsNullOrWhiteSpace(character) ||
                string.IsNullOrWhiteSpace(va) ||
                character.Equals(va, StringComparison.OrdinalIgnoreCase) ||
                va.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
                va.Contains("tba", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            records.Add(CreateRecord(media, mode, source, roleType, character, string.Empty, va, string.Empty));
        }

        private static VoiceCastRecord CreateRecord(
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            string source,
            string roleType,
            string character,
            string characterImage,
            string va,
            string vaImage)
        {
            return new VoiceCastRecord
            {
                MediaKey = media.MediaKey,
                MalId = media.MalId,
                AniListId = media.AniListId,
                ShowTitle = media.Title,
                LanguageMode = mode.ToString(),
                Source = source,
                RoleType = roleType,
                CharacterName = character,
                CharacterImageUrl = characterImage,
                VoiceActorName = va,
                VoiceActorImageUrl = vaImage,
                FetchedAtUtc = DateTime.UtcNow
            };
        }

        private static async Task<List<VoiceCastRecord>> LoadCachedAsync(
            DatabaseContext db,
            string mediaKey,
            VoiceLanguageMode mode,
            CancellationToken token)
        {
            return await db.VoiceCastRecords
                .Where(record => record.MediaKey == mediaKey && record.LanguageMode == mode.ToString())
                .OrderBy(record => record.NotFound)
                .ThenBy(record => record.RoleType == "Main" ? 0 : 1)
                .ThenBy(record => record.CharacterName)
                .ToListAsync(token);
        }

        private static async Task ReplaceCachedAsync(
            DatabaseContext db,
            VoiceCastMedia media,
            VoiceLanguageMode mode,
            string source,
            IReadOnlyList<VoiceCastRecord> cast,
            string manualUrl,
            bool notFound,
            CancellationToken token)
        {
            var existing = await db.VoiceCastRecords
                .Where(record => record.MediaKey == media.MediaKey && record.LanguageMode == mode.ToString())
                .ToListAsync(token);
            db.VoiceCastRecords.RemoveRange(existing);

            if (notFound)
            {
                await db.VoiceCastRecords.AddAsync(new VoiceCastRecord
                {
                    MediaKey = media.MediaKey,
                    MalId = media.MalId,
                    AniListId = media.AniListId,
                    ShowTitle = media.Title,
                    LanguageMode = mode.ToString(),
                    Source = source,
                    ManualUrl = manualUrl,
                    FetchedAtUtc = DateTime.UtcNow,
                    NotFound = true
                }, token);
            }
            else
            {
                foreach (var record in cast)
                {
                    record.Source = source;
                    record.ManualUrl = manualUrl;
                    record.FetchedAtUtc = DateTime.UtcNow;
                    record.NotFound = false;
                    await db.VoiceCastRecords.AddAsync(record, token);
                }
            }

            await db.SaveChangesAsync(token);
        }

        private static async Task UpdateLibraryAniListIdAsync(int malId, int aniListId, CancellationToken token)
        {
            if (malId <= 0 || aniListId <= 0)
            {
                return;
            }

            try
            {
                await using var db = new DatabaseContext();
                db.EnsureVaDetectSchema();
                var entry = await db.MalLibraryEntries.FirstOrDefaultAsync(item => item.MalId == malId, token);
                if (entry != null && entry.AniListId <= 0)
                {
                    entry.AniListId = aniListId;
                    await db.SaveChangesAsync(token);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"VA Detect failed to persist AniList id {aniListId} for MAL {malId}: {ex.Message}", "WARNING");
            }
        }

        private static string SelectBestTitle(VoiceCastMedia media)
        {
            return new[] { media.EnglishTitle, media.Title, media.RomajiTitle, media.NativeTitle }
                .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)) ?? string.Empty;
        }

        private static bool LooksEnglishHeader(string text)
        {
            return text.Contains("english", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("dub", StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanText(string html)
        {
            string decoded = System.Net.WebUtility.HtmlDecode(HtmlTagRegex.Replace(html ?? string.Empty, " "));
            return Regex.Replace(decoded, "\\s+", " ").Trim();
        }

        private static string CleanCastName(string value)
        {
            string cleaned = Regex.Replace(value ?? string.Empty, "\\[[^\\]]+\\]", " ");
            cleaned = Regex.Replace(cleaned, "\\([^)]*(?:ep|episode)[^)]*\\)", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, "\\s+", " ").Trim();
            return cleaned.Trim('-', '–', '—', ':', ' ');
        }

        private static string TryGetString(JsonElement element, string propertyName)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string TryGetNestedString(JsonElement element, string objectName, string propertyName)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(objectName, out var obj) &&
                   obj.ValueKind == JsonValueKind.Object &&
                   obj.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }
    }
}
