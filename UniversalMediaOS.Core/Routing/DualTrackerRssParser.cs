using System;
using System.Collections.Generic;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Linq;
using System.Threading;
using System.Globalization;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.Routing
{
    public class DualTrackerRssParser
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly System.Text.RegularExpressions.Regex _seedersRegex = 
            new System.Text.RegularExpressions.Regex(@"Seeders:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

        private readonly string _nyaaUrl;
        private readonly string _animeToshoUrl;

        static DualTrackerRssParser()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        }

        public DualTrackerRssParser(DomainHotSwapper? config = null)
        {
            _nyaaUrl = config?.GetSetting("NyaaUrl") ?? "";
            if (string.IsNullOrEmpty(_nyaaUrl)) _nyaaUrl = "https://nyaa.si/?page=rss&c=1_2&f=0&q=";

            _animeToshoUrl = config?.GetSetting("AnimeToshoUrl") ?? "";
            if (string.IsNullOrEmpty(_animeToshoUrl)) _animeToshoUrl = "https://feed.animetosho.org/rss2?q=";
        }

        public async Task<List<TorrentResult>> SearchAsync(string query, Action<string>? logger = null, CancellationToken token = default)
        {
            void Log(string msg) { logger?.Invoke(msg); System.Diagnostics.Debug.WriteLine(msg); }
            var results = new List<TorrentResult>();
            var escapedQuery = Uri.EscapeDataString(query ?? string.Empty);

            try 
            {
                Log("> [Tier 1] Fetching RSS feed from Nyaa...");
                results = await FetchAndParseFeed(_nyaaUrl + escapedQuery, "Nyaa", Log, token);
                if (results.Count > 0)
                {
                    Log($"> [Tier 1] Found {results.Count} results on Nyaa!");
                    return results;
                }
                Log("> [Tier 1] Nyaa returned 0 results.");
            }
            catch (TaskCanceledException ex)
            {
                Log($"> [Tier 1] Nyaa search timed out: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log($"> [Tier 1] Nyaa search failed: {ex.Message}");
            }

            try 
            {
                Log("> [Tier 1] Falling back to AnimeTosho RSS...");
                results = await FetchAndParseFeed(_animeToshoUrl + escapedQuery, "AnimeTosho", Log, token);
                if (results.Count > 0)
                {
                    Log($"> [Tier 1] Found {results.Count} results on AnimeTosho!");
                    return results;
                }
                Log("> [Tier 1] AnimeTosho returned 0 results.");
            }
            catch (TaskCanceledException ex)
            {
                Log($"> [Tier 1] AnimeTosho search timed out: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log($"> [Tier 1] AnimeTosho search failed: {ex.Message}");
            }

            return results;
        }

        private async Task<List<TorrentResult>> FetchAndParseFeed(string url, string source, Action<string> log, CancellationToken token = default)
        {
            var results = new List<TorrentResult>();
            
            using var internalCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(internalCts.Token, token);
            var mergedToken = linkedCts.Token;

            log($"> [Tier 1] Awaiting {source} response (10s timeout)...");
            
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, mergedToken);
            response.EnsureSuccessStatusCode();
            
            log($"> [Tier 1] Reading {source} stream...");
            using var stream = await response.Content.ReadAsStreamAsync(mergedToken);
            
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };

            using var reader = XmlReader.Create(stream, settings);
            var feed = SyndicationFeed.Load(reader);

            foreach (var item in feed.Items)
            {
                string magnet = string.Empty;
                string infoHash = string.Empty;
                int seeders = 0;
                string title = item.Title?.Text ?? string.Empty;

                if (source == "Nyaa")
                {
                    foreach (var ext in item.ElementExtensions)
                    {
                        var el = ext.GetObject<XElement>();
                        if (el.Name.LocalName == "seeders")
                        {
                            int.TryParse(el.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seeders);
                        }
                        if (el.Name.LocalName == "infoHash")
                        {
                            infoHash = el.Value;
                        }
                    }
                    if (item.Links.Count > 0)
                    {
                        magnet = item.Links.FirstOrDefault(l => l.Uri?.ToString()?.Contains("magnet:") == true)?.Uri?.ToString() ?? string.Empty;
                    }
                    
                    if (string.IsNullOrEmpty(magnet) && !string.IsNullOrEmpty(infoHash))
                    {
                        magnet = $"magnet:?xt=urn:btih:{infoHash}&dn={Uri.EscapeDataString(title)}&tr=http%3A%2F%2Fnyaa.tracker.wf%3A7777%2Fannounce&tr=udp%3A%2F%2Fopen.stealth.si%3A80%2Fannounce";
                    }
                }
                else if (source == "AnimeTosho")
                {
                    var magnetLink = item.Links.FirstOrDefault(l => l.Uri?.ToString()?.StartsWith("magnet:") == true);
                    if (magnetLink != null)
                    {
                        magnet = magnetLink.Uri?.ToString() ?? string.Empty;
                    }

                    // Parse seeders from summary text (e.g. "Seeders: 15")
                    if (item.Summary != null && !string.IsNullOrEmpty(item.Summary.Text))
                    {
                        var match = _seedersRegex.Match(item.Summary.Text);
                        if (match.Success)
                        {
                            int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seeders);
                        }
                    }
                    
                    // Fallback to torznab seeders if available
                    foreach (var ext in item.ElementExtensions)
                    {
                        if (ext.OuterName == "attr")
                        {
                            var el = ext.GetObject<XElement>();
                            if (el.Attribute("name")?.Value == "seeders")
                            {
                                int.TryParse(el.Attribute("value")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seeders);
                            }
                        }
                    }
                }

                results.Add(new TorrentResult 
                { 
                    Title = title, 
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
    }
}
