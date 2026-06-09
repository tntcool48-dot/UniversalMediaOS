using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Search
{
    public class FuzzyShieldSearch
    {
        private const string AniListUrl = "https://graphql.anilist.co";

        public async Task<List<MediaResult>> SearchAnimeAsync(string query)
        {
            var results = new List<MediaResult>();
            
            var gqlQuery = @"
            query ($search: String) {
                Page(page: 1, perPage: 15) {
                    media(search: $search, type: ANIME, sort: POPULARITY_DESC) {
                        id
                        title {
                            romaji
                            english
                        }
                        coverImage {
                            extraLarge
                            large
                        }
                        description(asHtml: false)
                    }
                }
            }";

            // If empty search, we can fetch trending
            if (string.IsNullOrWhiteSpace(query))
            {
                gqlQuery = @"
                query {
                    Page(page: 1, perPage: 15) {
                        media(type: ANIME, sort: TRENDING_DESC) {
                            id
                            title { romaji english }
                            coverImage { extraLarge large }
                            description(asHtml: false)
                        }
                    }
                }";
            }

            object requestBody = string.IsNullOrWhiteSpace(query) 
                ? new { query = gqlQuery } 
                : new { query = gqlQuery, variables = new { search = query } };

            string jsonBody = JsonSerializer.Serialize(requestBody);

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(AniListUrl, content);
                
                string responseJson = await response.Content.ReadAsStringAsync();
                
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseJson);
                    if (doc.RootElement.TryGetProperty("data", out var dataElement) && 
                        dataElement.TryGetProperty("Page", out var pageElement) && 
                        pageElement.TryGetProperty("media", out var mediaArray))
                    {
                        foreach (var item in mediaArray.EnumerateArray())
                        {
                            var titleElement = item.GetProperty("title");
                            string englishTitle = titleElement.TryGetProperty("english", out var e) && e.ValueKind != JsonValueKind.Null ? e.GetString() ?? "" : "";
                            string romajiTitle = titleElement.TryGetProperty("romaji", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetString() ?? "" : "";
                            
                            var coverElement = item.GetProperty("coverImage");
                            string coverUrl = coverElement.TryGetProperty("extraLarge", out var xl) && xl.ValueKind != JsonValueKind.Null ? xl.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(coverUrl))
                                coverUrl = coverElement.TryGetProperty("large", out var l) && l.ValueKind != JsonValueKind.Null ? l.GetString() ?? "" : "";

                            string synopsis = item.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null ? desc.GetString() ?? "" : "";

                            results.Add(new MediaResult
                            {
                                Id = item.GetProperty("id").GetInt32(),
                                OfficialTitle = !string.IsNullOrEmpty(englishTitle) ? englishTitle : (!string.IsNullOrEmpty(romajiTitle) ? romajiTitle : "Unknown Title"),
                                CoverImageUrl = coverUrl,
                                Synopsis = CleanHtml(synopsis)
                            });
                        }
                    }
                }
                else
                {
                    throw new Exception($"API Error: {response.StatusCode}\n{responseJson}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Search execution failed: {ex.Message}");
            }

            return results;
        }

        private string CleanHtml(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return input.Replace("<br>", "\n").Replace("<i>", "").Replace("</i>", "");
        }
    }

    public class MediaResult
    {
        public int Id { get; set; }
        public string OfficialTitle { get; set; } = string.Empty;
        public string CoverImageUrl { get; set; } = string.Empty;
        public string Synopsis { get; set; } = string.Empty;
    }
}
