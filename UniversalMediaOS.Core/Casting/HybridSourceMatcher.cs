using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace UniversalMediaOS.Core.Casting
{
    public class HybridSourceMatcher
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly DatabaseContext _db;
        private readonly string _aniListUrl;
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

        public HybridSourceMatcher(DatabaseContext db, UniversalMediaOS.Core.Configuration.DomainHotSwapper? config = null)
        {
            _db = db;
            _aniListUrl = config?.GetSetting("AniListUrl") ?? "https://graphql.anilist.co";
            if (string.IsNullOrEmpty(_aniListUrl)) _aniListUrl = "https://graphql.anilist.co";
        }

        private void Log(string message, string level = "INFO")
        {
            AppLogger.Log($"[Casting] {message}", level);
            System.Diagnostics.Debug.WriteLine($"[Casting] {level}: {message}");
        }

        /// <summary>
        /// Queries AniList GraphQL API for the cast and English voice actors, 
        /// caching them in SQLite for fast O(1) retrieval.
        /// </summary>
        public async Task<List<DubCastHash>> FetchAndCacheCastAsync(int mediaId, string showTitle = "", System.Threading.CancellationToken token = default)
        {
            var castList = new List<DubCastHash>();

            if (mediaId <= 0)
            {
                Log($"Invalid mediaId {mediaId} provided.", "WARNING");
                return castList;
            }

            try
            {
                // Check SQLite cache first
                var cached = await _db.DubHashes
                    .Where(d => d.MediaId == mediaId)
                    .ToListAsync(token);

                if (cached.Count > 0)
                {
                    Log($"Cache HIT for mediaId {mediaId} — loaded {cached.Count} VAs.");
                    return cached;
                }

                Log($"Cache MISS for mediaId {mediaId} — fetching from AniList GraphQL...");

                string gqlQuery = @"
                query ($id: Int) {
                  Media(id: $id, type: ANIME) {
                    title { romaji english }
                    characters(sort: ROLE_DESC, page: 1, perPage: 12) {
                      edges {
                        role
                        node {
                          name { full }
                          image { large }
                        }
                        voiceActors(language: ENGLISH) {
                          name { full }
                          image { large }
                        }
                      }
                    }
                  }
                }";

                using var request = new HttpRequestMessage(HttpMethod.Post, _aniListUrl);
                request.Headers.Add("User-Agent", UserAgent);
                request.Headers.Add("Accept", "application/json");

                var requestBody = new { query = gqlQuery, variables = new { id = mediaId } };
                string jsonBody = JsonSerializer.Serialize(requestBody);
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    Log($"AniList GraphQL request failed with status: {response.StatusCode}", "ERROR");
                    return castList;
                }

                string responseJson = await response.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(responseJson);

                if (doc.RootElement.TryGetProperty("data", out var data) && 
                    data.TryGetProperty("Media", out var media))
                {
                    string resolvedTitle = showTitle;
                    if (string.IsNullOrEmpty(resolvedTitle))
                    {
                        if (media.TryGetProperty("title", out var titleObj))
                        {
                            string english = titleObj.TryGetProperty("english", out var e) && e.ValueKind != JsonValueKind.Null ? e.GetString() ?? "" : "";
                            string romaji = titleObj.TryGetProperty("romaji", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetString() ?? "" : "";
                            resolvedTitle = !string.IsNullOrEmpty(english) ? english : romaji;
                        }
                    }

                    if (media.TryGetProperty("characters", out var characters) && 
                        characters.TryGetProperty("edges", out var edgesArray))
                    {
                        foreach (var edge in edgesArray.EnumerateArray())
                        {
                            if (!edge.TryGetProperty("node", out var characterNode) || characterNode.ValueKind == JsonValueKind.Null) continue;
                            
                            string charName = "";
                            if (characterNode.TryGetProperty("name", out var nameProp) && nameProp.TryGetProperty("full", out var fullProp))
                            {
                                charName = fullProp.GetString() ?? "";
                            }
                            string charImg = characterNode.TryGetProperty("image", out var ci) && ci.TryGetProperty("large", out var cil) ? cil.GetString() ?? "" : "";

                            if (!edge.TryGetProperty("voiceActors", out var vaProp) || vaProp.ValueKind != JsonValueKind.Array || vaProp.GetArrayLength() == 0) continue;

                            var firstVa = vaProp[0];
                            string vaName = "";
                            if (firstVa.TryGetProperty("name", out var vaNameProp) && vaNameProp.TryGetProperty("full", out var vaFullProp))
                            {
                                vaName = vaFullProp.GetString() ?? "";
                            }
                            string vaImg = firstVa.TryGetProperty("image", out var vi) && vi.TryGetProperty("large", out var vil) ? vil.GetString() ?? "" : "";

                            if (!string.IsNullOrEmpty(charName) && !string.IsNullOrEmpty(vaName))
                            {
                                var item = new DubCastHash
                                {
                                    MediaId = mediaId,
                                    ShowTitle = resolvedTitle,
                                    CharacterName = charName,
                                    CharacterImageUrl = charImg,
                                    VoiceActorName = vaName,
                                    VoiceActorImageUrl = vaImg
                                };
                                
                                castList.Add(item);
                                _db.DubHashes.Add(item);
                            }
                        }

                        if (castList.Count > 0)
                        {
                            using var transaction = await _db.Database.BeginTransactionAsync(token);
                            try
                            {
                                await _db.SaveChangesAsync(token);
                                await transaction.CommitAsync(token);
                                Log($"Saved {castList.Count} VAs to SQLite db.");
                            }
                            catch (Exception dbEx)
                            {
                                await transaction.RollbackAsync(token);
                                Log($"Failed to save cast entries to DB: {dbEx.Message}", "ERROR");
                                throw;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Casting service error: {ex.Message}", "ERROR");
            }

            return castList;
        }

        /// <summary>
        /// Returns other characters this voice actor has voiced across the SQLite completed history catalog.
        /// </summary>
        public async Task<List<DubCastHash>> GetCharacterSwapGalleryAsync(string voiceActorName, System.Threading.CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(voiceActorName)) return new List<DubCastHash>();
            return await _db.DubHashes
                .Where(d => d.VoiceActorName != null && d.VoiceActorName.ToLower() == voiceActorName.ToLower())
                .ToListAsync(token);
        }
    }
}
