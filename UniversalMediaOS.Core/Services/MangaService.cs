using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Services
{
    public class MangaSearchResult
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string CoverUrl { get; set; } = string.Empty;
    }

    public class MangaChapter
    {
        public string Id { get; set; } = string.Empty;
        public string ChapterNumber { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Pages { get; set; }
        public string ExternalUrl { get; set; } = string.Empty;
    }

    public class MangaService
    {
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        public async Task<List<MangaSearchResult>> SearchMangaAsync(string query, System.Threading.CancellationToken token = default)
        {
            var results = new List<MangaSearchResult>();
            if (string.IsNullOrWhiteSpace(query)) return results;

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                string url = $"https://api.mangadex.org/manga?title={Uri.EscapeDataString(query)}&limit=15&includes[]=cover_art";
                var response = await client.GetAsync(url, token);
                if (!response.IsSuccessStatusCode) return results;

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataArray.EnumerateArray())
                    {
                        string id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                        
                        string title = "Unknown";
                        if (item.TryGetProperty("attributes", out var attrs))
                        {
                            if (attrs.TryGetProperty("title", out var titleObj))
                            {
                                if (titleObj.TryGetProperty("en", out var enProp)) title = enProp.GetString() ?? title;
                                else if (titleObj.TryGetProperty("ja", out var jaProp)) title = jaProp.GetString() ?? title;
                                else
                                {
                                    // Fallback to first property value
                                    foreach (var prop in titleObj.EnumerateObject())
                                    {
                                        title = prop.Value.GetString() ?? title;
                                        break;
                                    }
                                }
                            }
                        }


                        // Parse cover art
                        string coverFileName = "";
                        if (item.TryGetProperty("relationships", out var rels) && rels.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var rel in rels.EnumerateArray())
                            {
                                if (rel.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "cover_art")
                                {
                                    if (rel.TryGetProperty("attributes", out var coverAttrs) && coverAttrs.TryGetProperty("fileName", out var fileProp))
                                    {
                                        coverFileName = fileProp.GetString() ?? "";
                                    }
                                }
                            }
                        }

                        string coverUrl = "https://images.unsplash.com/photo-1607604276583-eef5d076aa5f?w=400";
                        if (!string.IsNullOrEmpty(coverFileName) && !string.IsNullOrEmpty(id))
                        {
                            coverUrl = $"https://uploads.mangadex.org/covers/{id}/{coverFileName}";
                        }

                        results.Add(new MangaSearchResult
                        {
                            Id = id,
                            Title = title,
                            CoverUrl = coverUrl
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Manga Search Error: {ex.Message}");
            }

            return results;
        }

        public async Task<List<MangaChapter>> GetChaptersAsync(string mangaId)
        {
            var chapters = new List<MangaChapter>();
            if (string.IsNullOrEmpty(mangaId)) return chapters;

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                int offset = 0;
                int total = 1;

                while (offset < total)
                {
                    string url = $"https://api.mangadex.org/manga/{mangaId}/feed?translatedLanguage[]=en&limit=100&offset={offset}&order[chapter]=asc";
                    var response = await client.GetAsync(url);
                    if (!response.IsSuccessStatusCode) break;

                    string json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("total", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number)
                    {
                        total = totalProp.GetInt32();
                    }

                    if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                    {
                        if (dataArray.GetArrayLength() == 0) break;

                        foreach (var item in dataArray.EnumerateArray())
                        {
                            string id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                            string chNum = "";
                            string chTitle = "";
                            string extUrl = "";
                            int pages = 0;

                            if (item.TryGetProperty("attributes", out var attrs))
                            {
                                chNum = attrs.TryGetProperty("chapter", out var numProp) ? numProp.GetString() ?? "" : "";
                                chTitle = attrs.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                                extUrl = attrs.TryGetProperty("externalUrl", out var extProp) && extProp.ValueKind == JsonValueKind.String ? extProp.GetString() ?? "" : "";
                                pages = attrs.TryGetProperty("pages", out var pProp) && pProp.ValueKind == JsonValueKind.Number ? pProp.GetInt32() : 0;
                            }

                            if (!string.IsNullOrEmpty(id))
                            {
                                chapters.Add(new MangaChapter
                                {
                                    Id = id,
                                    ChapterNumber = string.IsNullOrEmpty(chNum) ? "0" : chNum,
                                    Title = string.IsNullOrEmpty(chTitle) ? $"Chapter {chNum}" : chTitle,
                                    Pages = pages,
                                    ExternalUrl = extUrl
                                });
                            }
                        }
                    }
                    else
                    {
                        break;
                    }

                    offset += 100;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Manga Chapters Error: {ex.Message}");
            }

            return chapters;
        }

        public async Task<List<string>> GetPageUrlsAsync(string chapterId)
        {
            var pages = new List<string>();
            if (string.IsNullOrEmpty(chapterId)) return pages;

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                string url = $"https://api.mangadex.org/at-home/server/{chapterId}";
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return pages;

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                var root = doc.RootElement;
                if (root.TryGetProperty("baseUrl", out var baseUrlProp) && 
                    root.TryGetProperty("chapter", out var chObj))
                {
                    string baseUrl = baseUrlProp.GetString() ?? "";
                    string hash = chObj.TryGetProperty("hash", out var hashProp) ? hashProp.GetString() ?? "" : "";
                    
                    if (chObj.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var page in dataArray.EnumerateArray())
                        {
                            string fileName = page.GetString() ?? "";
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                pages.Add($"{baseUrl}/data/{hash}/{fileName}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Manga Pages Error: {ex.Message}");
            }

            return pages;
        }
    }
}
