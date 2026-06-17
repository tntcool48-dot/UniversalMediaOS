using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Streaming;

namespace UniversalMediaOS.Core.Routing
{
    public class PlaybackSource
    {
        public SourceTier Tier { get; set; }
        public string UrlOrPath { get; set; } = string.Empty;
        public string EmbedOrigin { get; set; } = string.Empty;
        public string? UserAgent { get; set; }
        public string? Cookie { get; set; }
    }

    public enum SourceTier
    {
        Tier1_PythonScraper,
        Tier2_WebViewEmbed
    }

    /// <summary>
    /// 2-tier streaming router:
    ///   Tier 1: Python scraper → HLS loopback proxy → LibVLC
    ///   Tier 2: Embedded WebView2 + uBlock Origin fallback
    /// P2P downloads are a completely separate system (SeasonDownloader).
    /// </summary>
    public class TripleNetHandoff
    {
        private const string DesktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private static readonly HttpClient _httpClient = new();

        private readonly Configuration.DomainHotSwapper _config;
        private readonly ScraperEngine _scraper;
        private readonly HlsLoopbackProxy _proxy;

        public TripleNetHandoff(
            Configuration.DomainHotSwapper config,
            ScraperEngine scraper,
            HlsLoopbackProxy proxy)
        {
            _config = config;
            _scraper = scraper;
            _proxy = proxy;
        }

        /// <summary>
        /// Resolves the best streaming source for an episode.
        /// Tier 1: Python scraper (dynamic mirror pool + 4-stage extraction waterfall)
        /// Tier 2: Embedded WebView2 (URL heuristic resolver)
        /// </summary>
        public async Task<PlaybackSource?> ResolveBestSourceAsync(
            string query,
            string episodeId,
            string providerDomain,
            Action<string>? onStatusUpdate = null,
            SourceTier minimumTier = SourceTier.Tier1_PythonScraper,
            CancellationToken token = default)
        {
            void Log(string msg)
            {
                onStatusUpdate?.Invoke(msg);
                System.Diagnostics.Debug.WriteLine(msg);
                AppLogger.Log(msg);
            }

            // ── Tier 1: Python Scraper → HLS Loopback Proxy ──────────────────
            if (minimumTier <= SourceTier.Tier1_PythonScraper && !_scraper.IsAvailable)
            {
                try
                {
                    Log("> [Tier 1] Preparing Python scraper...");
                    await _scraper.EnsureReadyAsync(token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"> [Tier 1] Scraper preparation failed: {ex.Message}");
                }
            }

            if (minimumTier <= SourceTier.Tier1_PythonScraper && _scraper.IsAvailable)
            {
                try
                {
                    int maxSiteAttempts = GetScraperSiteAttemptLimit();
                    Log($"> [Tier 1] Python scraper: resolving '{query}' across up to {maxSiteAttempts} indexed sites...");

                    var stream = await _scraper.ResolveAsync(query, episodeId, maxSiteAttempts, token);
                    if (stream?.Url != null)
                    {
                        string sessionId = _proxy.RegisterSession(new ProxySession(
                            stream.Url,
                            stream.UserAgent,
                            stream.Cookie,
                            stream.KeyUrl,
                            stream.Referer,
                            DateTime.UtcNow));

                        string localUrl = $"http://127.0.0.1:19475/stream?id={sessionId}";
                        Log($"> [Tier 1] SUCCESS - proxied stream registered: {localUrl}");

                        return new PlaybackSource
                        {
                            Tier = SourceTier.Tier1_PythonScraper,
                            UrlOrPath = localUrl,
                            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"
                        };
                    }

                    Log("> [Tier 1] Scraper exhausted indexed sites. Falling to Tier 2...");
                    /*
                    if (results.Length == 0)
                    {
                        Log("> [Tier 1] No search results from scraper. Falling to Tier 2...");
                    }
                    else
                    {
                        var best = PickBestResult(results, query);
                        if (best != null)
                        {
                            string epUrl = MapEpisodeUrl(best, episodeId);
                            Log($"> [Tier 1] Extracting stream from: {epUrl}");

                            var stream = await _scraper.ExtractAsync(epUrl, token);
                            if (stream?.Url != null)
                            {
                                string sessionId = _proxy.RegisterSession(new ProxySession(
                                    stream.Url,
                                    stream.UserAgent,
                                    stream.Cookie,
                                    stream.KeyUrl,
                                    stream.Referer,
                                    DateTime.UtcNow));

                                string localUrl = $"http://127.0.0.1:19475/stream?id={sessionId}";
                                Log($"> [Tier 1] SUCCESS — proxied stream registered: {localUrl}");

                                return new PlaybackSource
                                {
                                    Tier = SourceTier.Tier1_PythonScraper,
                                    UrlOrPath = localUrl,
                                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"
                                };
                            }
                            else
                            {
                                Log("> [Tier 1] Scraper exhausted all mirrors. Falling to Tier 2...");
                            }
                        }
                        else
                        {
                            Log("> [Tier 1] No suitable result matched the query. Falling to Tier 2...");
                        }
                    }
                    */
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"> [Tier 1] Scraper error: {ex.Message}. Falling to Tier 2...");
                }
            }
            else if (minimumTier <= SourceTier.Tier1_PythonScraper && !_scraper.IsAvailable)
            {
                Log("> [Tier 1] Python not available. Falling to Tier 2...");
            }

            // ── Tier 2: Embedded WebView2 ─────────────────────────────────────
            Log("> [Tier 2] Falling back to embedded WebView2...");
            string finalUrl = await BuildWebViewUrlAsync(providerDomain, query, episodeId, Log, token);

            return new PlaybackSource
            {
                Tier = SourceTier.Tier2_WebViewEmbed,
                UrlOrPath = finalUrl
            };
        }

        // ── Helper: pick best search result ─────────────────────────────────

        private ScraperSearchResult? PickBestResult(ScraperSearchResult[] results, string query)
        {
            if (results.Length == 0) return null;

            // Prefer exact or closest title match
            string cleanQuery = NormalizeTitle(query);

            ScraperSearchResult? exact = results.FirstOrDefault(r =>
                NormalizeTitle(r.Title).Equals(cleanQuery, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // Partial match — pick result whose title contains the most query words
            var queryWords = cleanQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return results
                .OrderByDescending(r =>
                    queryWords.Count(w => NormalizeTitle(r.Title).Contains(w, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault();
        }

        /// <summary>
        /// Constructs the episode URL from a search result + episode number.
        /// Handles GogoAnime, AnimePahe, HiAnime naming conventions.
        /// </summary>
        private static string MapEpisodeUrl(ScraperSearchResult result, string episodeId)
        {
            string baseUrl = result.Url.TrimEnd('/');
            string provider = result.Provider.ToLowerInvariant();

            if (provider is "gogoanime" or "anitaku")
            {
                // baseUrl is like https://anitaku.so/category/show-name
                // episode URL is like https://anitaku.so/show-name-episode-1
                string slug = Regex.Match(baseUrl, @"/category/(.+)$").Groups[1].Value;
                if (string.IsNullOrEmpty(slug))
                    slug = baseUrl.Split('/').Last();
                string domain = Regex.Match(baseUrl, @"https?://[^/]+").Value;
                return $"{domain}/{slug}-episode-{episodeId}";
            }

            if (provider == "animepahe")
            {
                // baseUrl is like https://animepahe.ru/anime/{session}
                // episode constructed as {session}-episode-{num}
                string session = baseUrl.Split('/').Last();
                return $"https://animepahe.ru/play/{session}/{episodeId}";
            }

            if (provider is "hianime" or "zoro" or "aniwatchtv")
            {
                // baseUrl is like https://hianime.to/watch/show-name-{id}
                return $"{baseUrl}?ep={episodeId}";
            }

            // Generic fallback: append episode number
            return $"{baseUrl}-episode-{episodeId}";
        }

        // ── Helper: WebView URL builder ──────────────────────────────────────

        private async Task<string> BuildWebViewUrlAsync(
            string providerDomain,
            string query,
            string episodeId,
            Action<string> log,
            CancellationToken token)
        {
            string safeQuery = Uri.EscapeDataString(query);

            if (providerDomain.Contains("{slug}", StringComparison.OrdinalIgnoreCase) ||
                providerDomain.Contains("{episode}", StringComparison.OrdinalIgnoreCase))
            {
                string slug = GenerateSlug(query);
                string url = Regex.Replace(providerDomain, @"\{slug\}", slug, RegexOptions.IgnoreCase);
                url = Regex.Replace(url, @"\{episode\}", episodeId, RegexOptions.IgnoreCase);
                return await ResolveExplicitProviderUrlAsync(url, query, episodeId, log, token);
            }

            if (providerDomain.Contains("{query}", StringComparison.OrdinalIgnoreCase))
            {
                string url = Regex.Replace(providerDomain, @"\{query\}", safeQuery, RegexOptions.IgnoreCase);
                if (!IsSearchUrl(url))
                {
                    log($"> [Tier 2] Configured provider pattern is not a search URL. Trying fallback routes from: {url}");
                    return await ResolveHomepageFromUrlAsync(url, query, episodeId, log, token);
                }

                return await ResolveExplicitProviderUrlAsync(url, query, episodeId, log, token);
            }

            if (providerDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                providerDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Domain-only fallback: probe common provider routes and search pages.
                return await ResolveHomepageDomainAsync(providerDomain, query, episodeId, log, token);
            }

            string normalizedDomain = $"https://{providerDomain.TrimEnd('/')}";
            return await ResolveHomepageDomainAsync(normalizedDomain, query, episodeId, log, token);
        }

        private Task<string> ResolveHomepageFromUrlAsync(
            string url,
            string query,
            string episodeId,
            Action<string> log,
            CancellationToken token)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return ResolveHomepageDomainAsync($"{uri.Scheme}://{uri.Authority}", query, episodeId, log, token);
            }

            return ResolveHomepageDomainAsync(url, query, episodeId, log, token);
        }

        private async Task<string> ResolveExplicitProviderUrlAsync(
            string url,
            string query,
            string episodeId,
            Action<string> log,
            CancellationToken token)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                log($"> [Tier 2] Provider URL is not absolute. Loading as-is: {url}");
                return url;
            }

            try
            {
                log($"> [Tier 2] Probing configured provider URL: {url}");

                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                probeCts.CancelAfter(TimeSpan.FromSeconds(6));

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(DesktopUserAgent);

                bool isSearchUrl = IsSearchUrl(url);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    probeCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    string html = await response.Content.ReadAsStringAsync(probeCts.Token);

                    if (isSearchUrl)
                    {
                        string domainRoot = $"{uri.Scheme}://{uri.Authority}";
                        string? directLink = ExtractBestLinkFromSearchHtml(html, domainRoot, query, episodeId);
                        if (!string.IsNullOrWhiteSpace(directLink))
                        {
                            log($"> [Tier 2] Configured search URL resolved to: {directLink}");
                            return directLink;
                        }
                    }
                    else if (LooksLikeHtmlResponse(response) && !HtmlLooksRelevant(html, query))
                    {
                        log("> [Tier 2] Configured provider URL returned a generic page. Trying fallback routes...");
                        return await ResolveHomepageDomainAsync($"{uri.Scheme}://{uri.Authority}", query, episodeId, log, token);
                    }

                    log($"> [Tier 2] Configured provider URL is reachable.");
                    return url;
                }

                log($"> [Tier 2] Configured provider URL failed ({(int)response.StatusCode}). Trying fallback routes...");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                log("> [Tier 2] Configured provider URL probe timed out. Trying fallback routes...");
            }
            catch (Exception ex)
            {
                log($"> [Tier 2] Configured provider URL probe failed: {ex.Message}. Trying fallback routes...");
            }

            return await ResolveHomepageDomainAsync($"{uri.Scheme}://{uri.Authority}", query, episodeId, log, token);
        }

        private async Task<string> ResolveHomepageDomainAsync(
            string baseDomain,
            string query,
            string episodeId,
            Action<string> log,
            CancellationToken token)
        {
            log($"> [Tier 2] Resolving fallback provider routes for '{baseDomain}'...");

            string slug = GenerateSlug(query);
            string safeQuery = Uri.EscapeDataString(query);
            string domainRoot = baseDomain.TrimEnd('/');

            try
            {
                var uri = new Uri(baseDomain);
                domainRoot = $"{uri.Scheme}://{uri.Authority}";
            }
            catch (Exception ex)
            {
                log($"> [Tier 2] Could not normalize fallback domain: {ex.Message}");
            }

            var candidates = new[]
            {
                $"{domainRoot}/anime/{slug}",
                $"{domainRoot}/watch/{slug}-episode-{episodeId}",
                $"{domainRoot}/watch/{slug}",
                $"{domainRoot}/category/{slug}",
                $"{domainRoot}/search?q={safeQuery}",
                $"{domainRoot}/search?keyword={safeQuery}",
                $"{domainRoot}/search.html?keyword={safeQuery}"
            };

            foreach (string url in candidates)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    log($"> [Tier 2] Probing fallback URL: {url}");

                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(6));

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.UserAgent.ParseAdd(DesktopUserAgent);

                    bool isSearchUrl = IsSearchUrl(url);

                    using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseContentRead,
                        probeCts.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        log($"> [Tier 2] Probe failed ({(int)response.StatusCode}) for {url}");
                        continue;
                    }

                    log($"> [Tier 2] Probe matched: {url}");

                    string html = await response.Content.ReadAsStringAsync(probeCts.Token);

                    if (isSearchUrl)
                    {
                        string? directLink = ExtractBestLinkFromSearchHtml(html, domainRoot, query, episodeId);
                        if (!string.IsNullOrWhiteSpace(directLink))
                        {
                            log($"> [Tier 2] Search page resolved to: {directLink}");
                            return directLink;
                        }

                        log("> [Tier 2] Search page loaded, but no matching watch/details link was found.");
                    }
                    else if (LooksLikeHtmlResponse(response) && !HtmlLooksRelevant(html, query))
                    {
                        log($"> [Tier 2] Probe returned a generic page, continuing: {url}");
                        continue;
                    }

                    return url;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    log($"> [Tier 2] Probe timed out for {url}");
                }
                catch (Exception ex)
                {
                    log($"> [Tier 2] Probe error for {url}: {ex.Message}");
                }
            }

            string searchFallback = $"{domainRoot}/search?keyword={safeQuery}";
            log($"> [Tier 2] No fallback route matched. Loading search URL: {searchFallback}");
            return searchFallback;
        }

        private static string? ExtractBestLinkFromSearchHtml(string html, string domainRoot, string query, string episodeId)
        {
            var hrefMatches = Regex.Matches(html, @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            var keywords = Regex.Matches(query.ToLowerInvariant(), @"[a-z0-9]{3,}")
                .Cast<Match>()
                .Select(m => m.Value)
                .ToList();

            if (keywords.Count == 0)
                return null;

            return hrefMatches
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Where(href => !IsStaticAssetLink(href))
                .Select(href => ScoreCandidateLink(href, domainRoot, keywords, episodeId))
                .Where(candidate => candidate.score > 0)
                .OrderByDescending(candidate => candidate.score)
                .Select(candidate => candidate.url)
                .FirstOrDefault();
        }

        private static bool IsSearchUrl(string url) =>
            url.Contains("/search", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("search.html", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("?q=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("&q=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("?query=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("&query=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("?keyword=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("&keyword=", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeHtmlResponse(HttpResponseMessage response)
        {
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            return string.IsNullOrWhiteSpace(mediaType) ||
                   mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HtmlLooksRelevant(string html, string query)
        {
            if (string.IsNullOrWhiteSpace(html))
                return false;

            var keywords = Regex.Matches(query.ToLowerInvariant(), @"[a-z0-9]{3,}")
                .Cast<Match>()
                .Select(m => m.Value)
                .Where(word => word is not ("dub" or "sub" or "eng" or "english" or "dubbed"))
                .ToList();

            if (keywords.Count == 0)
                return true;

            string lowerHtml = html.ToLowerInvariant();
            int matched = keywords.Count(word => lowerHtml.Contains(word, StringComparison.OrdinalIgnoreCase));
            int required = Math.Min(2, keywords.Count);
            return matched >= required;
        }

        private static bool IsStaticAssetLink(string href)
        {
            string lowerHref = href.ToLowerInvariant();
            return lowerHref.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
                   lowerHref.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                   lowerHref.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                   lowerHref.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   lowerHref.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   lowerHref.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                   lowerHref.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) ||
                   lowerHref.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) ||
                   lowerHref.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
        }

        private static (string url, int score) ScoreCandidateLink(
            string href,
            string domainRoot,
            System.Collections.Generic.IReadOnlyCollection<string> keywords,
            string episodeId)
        {
            string lowerHref = href.ToLowerInvariant();
            bool isWatchOrDetails = lowerHref.Contains("/watch", StringComparison.OrdinalIgnoreCase) ||
                                    lowerHref.Contains("/anime", StringComparison.OrdinalIgnoreCase) ||
                                    lowerHref.Contains("/category", StringComparison.OrdinalIgnoreCase) ||
                                    lowerHref.Contains("/series", StringComparison.OrdinalIgnoreCase) ||
                                    lowerHref.Contains("/show", StringComparison.OrdinalIgnoreCase) ||
                                    lowerHref.Contains("/play", StringComparison.OrdinalIgnoreCase);

            if (!isWatchOrDetails)
                return (string.Empty, 0);

            string fullUrl = ResolveProviderLink(domainRoot, href);
            int score = keywords.Count(word => lowerHref.Contains(word, StringComparison.OrdinalIgnoreCase));

            if (score > 0 && !string.IsNullOrWhiteSpace(episodeId))
            {
                if (lowerHref.Contains($"-episode-{episodeId}", StringComparison.OrdinalIgnoreCase) ||
                    lowerHref.Contains($"/ep-{episodeId}", StringComparison.OrdinalIgnoreCase) ||
                    lowerHref.EndsWith($"-{episodeId}", StringComparison.OrdinalIgnoreCase) ||
                    lowerHref.EndsWith($"/{episodeId}", StringComparison.OrdinalIgnoreCase))
                {
                    score += 5;
                }
            }

            return (fullUrl, score);
        }

        private static string ResolveProviderLink(string domainRoot, string href)
        {
            if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return href;
            }

            if (href.StartsWith("/", StringComparison.Ordinal))
            {
                return $"{domainRoot.TrimEnd('/')}{href}";
            }

            return $"{domainRoot.TrimEnd('/')}/{href}";
        }

        // ── Utilities ────────────────────────────────────────────────────────

        private int GetScraperSiteAttemptLimit()
        {
            string raw = _config.GetSetting("ScraperSiteAttemptLimit");
            return int.TryParse(raw, out int limit)
                ? Math.Clamp(limit, 1, 30)
                : 6;
        }

        private static string NormalizeTitle(string title) =>
            Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9\s]", " ")
                 .Replace("  ", " ").Trim();

        private static string GenerateSlug(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string slug = input.ToLowerInvariant();
            slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
            slug = Regex.Replace(slug, @"[\s-]+", "-").Trim('-');
            return slug;
        }

        /// <summary>
        /// Word-boundary safe dub detection — avoids false positives on "Dublin", "Dubious", etc.
        /// </summary>
        private static bool IsDubTitle(string title) =>
            Regex.IsMatch(title,
                @"\b(dub|dubbed|eng|english|dual[\s\-]audio|multi[\s\-]audio)\b",
                RegexOptions.IgnoreCase);
    }
}
