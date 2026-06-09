using System;
using System.Collections.Generic;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Linq;
using System.Threading;

namespace UniversalMediaOS.Core.Routing
{
    public class DualTrackerRssParser
    {
        private const string NyaaUrl = "https://nyaa.si/?page=rss&c=1_2&f=0&q=";
        private const string AnimeToshoUrl = "https://feed.animetosho.org/rss2?q=";

        public async Task<List<TorrentResult>> SearchAsync(string query, Action<string> logger = null)
        {
            void Log(string msg) { logger?.Invoke(msg); Console.WriteLine(msg); }
            var results = new List<TorrentResult>();

            try 
            {
                Log("> [Tier 1] Fetching RSS feed from Nyaa...");
                results = await FetchAndParseFeed(NyaaUrl + Uri.EscapeDataString(query), "Nyaa", Log);
                if (results.Count > 0)
                {
                    Log($"> [Tier 1] Found {results.Count} results on Nyaa!");
                    return results;
                }
                Log("> [Tier 1] Nyaa returned 0 results.");
            }
            catch (Exception ex)
            {
                Log($"> [Tier 1] Nyaa search failed or timed out: {ex.Message}");
            }

            try 
            {
                Log("> [Tier 1] Falling back to AnimeTosho RSS...");
                results = await FetchAndParseFeed(AnimeToshoUrl + Uri.EscapeDataString(query), "AnimeTosho", Log);
                if (results.Count > 0)
                {
                    Log($"> [Tier 1] Found {results.Count} results on AnimeTosho!");
                }
                else
                {
                    Log("> [Tier 1] AnimeTosho returned 0 results.");
                }
            }
            catch (Exception ex)
            {
                Log($"> [Tier 1] AnimeTosho search failed or timed out: {ex.Message}");
            }

            return results;
        }

        private async Task<List<TorrentResult>> FetchAndParseFeed(string url, string source, Action<string> log)
        {
            var results = new List<TorrentResult>();
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            log($"> [Tier 1] Awaiting {source} response (10s timeout)...");
            
            using var response = await client.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();
            
            log($"> [Tier 1] Reading {source} stream into memory...");
            var xmlContent = await response.Content.ReadAsStringAsync(cts.Token);
            using var stringReader = new System.IO.StringReader(xmlContent);
            using var reader = XmlReader.Create(stringReader);
            var feed = SyndicationFeed.Load(reader);

            foreach (var item in feed.Items)
            {
                string magnet = string.Empty;
                string infoHash = string.Empty;
                int seeders = 0;

                if (source == "Nyaa")
                {
                    foreach (var ext in item.ElementExtensions)
                    {
                        var el = ext.GetObject<XElement>();
                        if (el.Name.LocalName == "seeders") int.TryParse(el.Value, out seeders);
                        if (el.Name.LocalName == "infoHash") infoHash = el.Value;
                    }
                    if (item.Links.Count > 0) magnet = item.Links.FirstOrDefault(l => l.Uri.ToString().Contains("magnet:"))?.Uri.ToString() ?? "";
                    
                    if (string.IsNullOrEmpty(magnet) && !string.IsNullOrEmpty(infoHash))
                    {
                        magnet = $"magnet:?xt=urn:btih:{infoHash}&dn={Uri.EscapeDataString(item.Title.Text)}&tr=http%3A%2F%2Fnyaa.tracker.wf%3A7777%2Fannounce&tr=udp%3A%2F%2Fopen.stealth.si%3A80%2Fannounce";
                    }
                }
                else if (source == "AnimeTosho")
                {
                    var magnetLink = item.Links.FirstOrDefault(l => l.Uri.ToString().StartsWith("magnet:"));
                    if (magnetLink != null) magnet = magnetLink.Uri.ToString();
                }

                results.Add(new TorrentResult 
                { 
                    Title = item.Title.Text, 
                    MagnetLink = magnet, 
                    InfoHash = infoHash, 
                    Seeders = seeders,
                    Source = source
                });
            }
            return results;
        }
    }

    public class TorrentResult
    {
        public string Title { get; set; } = string.Empty;
        public string MagnetLink { get; set; } = string.Empty;
        public string InfoHash { get; set; } = string.Empty;
        public int Seeders { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? DownloadedFilePath { get; set; }
    }
}
