using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace UniversalMediaOS.Core.Casting
{
    public class HybridSourceMatcher
    {
        private readonly DatabaseContext _db;
        private const string AniListUrl = "https://graphql.anilist.co";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

        public HybridSourceMatcher(DatabaseContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Queries AniList GraphQL API for the cast and English voice actors, 
        /// caching them in SQLite for fast O(1) retrieval.
        /// </summary>
        public async Task<List<DubCastHash>> FetchAndCacheCastAsync(int mediaId, string showTitle = "")
        {
            var castList = new List<DubCastHash>();

            try
            {
                // Check SQLite cache first
                var cached = await _db.DubHashes
                    .Where(d => d.MediaId == mediaId)
                    .ToListAsync();

                if (cached.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[Casting] Cache HIT for mediaId {mediaId} — loaded {cached.Count} VAs.");
                    return cached;
                }

                System.Diagnostics.Debug.WriteLine($"[Casting] Cache MISS for mediaId {mediaId} — fetching from AniList GraphQL...");
                
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                client.DefaultRequestHeaders.Add("Accept", "application/json");

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

                var requestBody = new { query = gqlQuery, variables = new { id = mediaId } };
                string jsonBody = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                
                var response = await client.PostAsync(AniListUrl, content);
                if (!response.IsSuccessStatusCode) return castList;

                string responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);

                if (doc.RootElement.TryGetProperty("data", out var data) && 
                    data.TryGetProperty("Media", out var media))
                {
                    string resolvedTitle = showTitle;
                    if (string.IsNullOrEmpty(resolvedTitle))
                    {
                        var titleObj = media.GetProperty("title");
                        string english = titleObj.TryGetProperty("english", out var e) && e.ValueKind != JsonValueKind.Null ? e.GetString() ?? "" : "";
                        string romaji = titleObj.TryGetProperty("romaji", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetString() ?? "" : "";
                        resolvedTitle = !string.IsNullOrEmpty(english) ? english : romaji;
                    }

                    if (media.TryGetProperty("characters", out var characters) && 
                        characters.TryGetProperty("edges", out var edgesArray))
                    {
                        foreach (var edge in edgesArray.EnumerateArray())
                        {
                            var characterNode = edge.GetProperty("node");
                            string charName = characterNode.GetProperty("name").GetProperty("full").GetString() ?? "";
                            string charImg = characterNode.TryGetProperty("image", out var ci) && ci.TryGetProperty("large", out var cil) ? cil.GetString() ?? "" : "";

                            var vaArray = edge.GetProperty("voiceActors");
                            if (vaArray.ValueKind == JsonValueKind.Array && vaArray.GetArrayLength() > 0)
                            {
                                var firstVa = vaArray[0];
                                string vaName = firstVa.GetProperty("name").GetProperty("full").GetString() ?? "";
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
                        }

                        if (castList.Count > 0)
                        {
                            await _db.SaveChangesAsync();
                            System.Diagnostics.Debug.WriteLine($"[Casting] Saved {castList.Count} VAs to SQLite db.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Casting service error: {ex.Message}");
            }



            return castList;
        }

        /// <summary>
        /// Returns other characters this voice actor has voiced across the SQLite completed history catalog.
        /// </summary>
        public async Task<List<DubCastHash>> GetCharacterSwapGalleryAsync(string voiceActorName)
        {
            return await _db.DubHashes
                .Where(d => d.VoiceActorName.ToLower() == voiceActorName.ToLower())
                .ToListAsync();
        }
    }
}
