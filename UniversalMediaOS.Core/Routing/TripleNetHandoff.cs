using System;
using System.IO;
using System.Collections.Generic;
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
        public IReadOnlyList<MediaSubtitleTrack> Subtitles { get; init; } = [];
        public string AudioNotice { get; init; } = string.Empty;
    }

    public enum SourceTier
    {
        Tier1_PythonScraper,
        Tier2_WebViewEmbed
    }

    /// <summary>
    /// 2-tier streaming router:
    ///   Tier 1: Python scraper → HLS loopback proxy → LibVLC
    ///   Tier 2: Explicitly selected website in WebView2 + uBlock Origin
    /// P2P downloads are a completely separate system (SeasonDownloader).
    /// </summary>
    public class TripleNetHandoff
    {
        private const string DesktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private static readonly HttpClient _httpClient = new();

        private readonly Configuration.DomainHotSwapper _config;
        private readonly IScraperResolver _scraper;
        private readonly HlsLoopbackProxy _proxy;

        public TripleNetHandoff(
            Configuration.DomainHotSwapper config,
            IScraperResolver scraper,
            HlsLoopbackProxy proxy)
        {
            _config = config;
            _scraper = scraper;
            _proxy = proxy;
        }

        /// <summary>
        /// Resolves the best streaming source for an episode.
        /// Tier 1: Python scraper (dynamic mirror pool + 4-stage extraction waterfall)
        /// Tier 2: Explicit website choice; never an automatic stream replacement
        /// </summary>
        public async Task<PlaybackSource?> ResolveBestSourceAsync(
            string query,
            string episodeId,
            string providerDomain,
            Action<string>? onStatusUpdate = null,
            SourceTier minimumTier = SourceTier.Tier1_PythonScraper,
            CancellationToken token = default,
            string audioPreference = "sub",
            IReadOnlyList<string>? titleAliases = null,
            int aniListId = 0,
            int malId = 0,
            IReadOnlyList<string>? titleSynonyms = null)
        {
            audioPreference = NormalizeAudioPreference(audioPreference);
            token.ThrowIfCancellationRequested();
            bool nativeRequested = minimumTier <= SourceTier.Tier1_PythonScraper;

            void Log(string msg)
            {
                string safeMessage = LogSanitizer.RedactSensitiveUrls(msg);
                onStatusUpdate?.Invoke(safeMessage);
                System.Diagnostics.Debug.WriteLine(safeMessage);
                AppLogger.Log(safeMessage);
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
                    string siteScope = maxSiteAttempts == 0
                        ? "all indexed sites"
                        : $"up to {maxSiteAttempts} indexed sites";
                    Log($"> [Tier 1] Python scraper: resolving '{query}' episode {episodeId} audio={audioPreference} across {siteScope}...");

                    var stream = await _scraper.ResolveAsync(query, episodeId, maxSiteAttempts, token, Log,
                        audioPreference, titleAliases: titleAliases, aniListId: aniListId, malId: malId,
                        titleSynonyms: titleSynonyms);
                    if (stream?.Url != null)
                    {
                        if (!stream.RequiresWebView && audioPreference == "dub" && !HasRequestedDub(stream))
                        {
                            Log("> [Tier 1] The resolved stream has no verified English audio. Try another episode or choose Sub.");
                            return null;
                        }
                        if (stream.RequiresWebView)
                        {
                            Log("> [Tier 1] Only a website player was found. Stream will not open it automatically; choose Open website if needed.");
                            return null;
                        }

                        return RegisterScraperStream(stream, Log);
                    }

                    Log("> [Tier 1] No native stream was found within the configured indexed-site budget.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"> [Tier 1] Scraper error: {ex.Message}.");
                }
            }
            else if (minimumTier <= SourceTier.Tier1_PythonScraper && !_scraper.IsAvailable)
            {
                Log("> [Tier 1] Python scraper is unavailable.");
            }

            if (nativeRequested)
            {
                token.ThrowIfCancellationRequested();
                Log("> [Tier 1] Stream stopped. Retry later, choose another episode/audio option, or explicitly choose Open website.");
                return null;
            }

            // ── Tier 2: Embedded WebView2 ─────────────────────────────────────
            Log("> [Tier 2] Opening the explicitly selected website player...");
            if (string.IsNullOrWhiteSpace(providerDomain))
            {
                Log("> [Tier 2] Looking for the matching episode website through automatic providers...");
                try
                {
                    if (!_scraper.IsAvailable)
                    {
                        await _scraper.EnsureReadyAsync(token);
                    }

                    if (_scraper.IsAvailable)
                    {
                        var discovered = await _scraper.ResolveAsync(
                            query,
                            episodeId,
                            GetScraperSiteAttemptLimit(),
                            token,
                            Log,
                            audioPreference,
                            preferNative: false,
                            titleAliases: titleAliases,
                            aniListId: aniListId,
                            malId: malId,
                            titleSynonyms: titleSynonyms);
                        if (!string.IsNullOrWhiteSpace(discovered?.Url))
                        {
                            if (!discovered.RequiresWebView && audioPreference == "dub" && !HasRequestedDub(discovered))
                            {
                                Log("> [Tier 2] The discovered stream has no verified English audio.");
                                return null;
                            }
                            if (discovered.RequiresWebView)
                            {
                                return RegisterBrowserOnlyPage(discovered, Log);
                            }

                            if (IsLikelyBrowserPage(discovered.Referer))
                            {
                                Log($"> [Tier 2] Opening the scraper's captured player page: {discovered.Referer}");
                                return new PlaybackSource
                                {
                                    Tier = SourceTier.Tier2_WebViewEmbed,
                                    UrlOrPath = discovered.Referer!
                                };
                            }

                            Log("> [Tier 2] The automatic resolver found verified media but no safe browser page. Opening the verified native stream instead.");
                            return RegisterScraperStream(discovered, Log);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"> [Tier 2] Automatic browser discovery failed: {ex.Message}");
                }

                Log("> [Tier 2] No browser source was found in the indexed sites. Retry later or choose another episode/audio option.");
                return null;
            }

            string finalUrl = await BuildWebViewUrlAsync(providerDomain, query, episodeId, Log, token);
            return new PlaybackSource
            {
                Tier = SourceTier.Tier2_WebViewEmbed,
                UrlOrPath = finalUrl
            };
        }

        // ── Helper: pick best search result ─────────────────────────────────

        private PlaybackSource RegisterScraperStream(ScraperStreamResult stream, Action<string> log)
        {
            if (!_proxy.IsRunning)
            {
                _proxy.Start();
            }

            if (!_proxy.IsRunning)
            {
                throw new InvalidOperationException(
                    $"The HLS proxy is unavailable{(string.IsNullOrWhiteSpace(_proxy.LastStartupError) ? "." : $": {_proxy.LastStartupError}")}");
            }

            string sessionId = _proxy.RegisterSession(new ProxySession(
                stream.Url!,
                stream.UserAgent,
                stream.Cookie,
                stream.KeyUrl,
                stream.Referer,
                DateTime.UtcNow,
                stream.Headers));

            bool isHls = IsHlsUrl(stream.Url!);
            string localUrl = isHls
                ? _proxy.CreateStreamUrl(sessionId)
                : _proxy.CreateSegmentUrl(sessionId, stream.Url!);
            if (isHls && !string.IsNullOrWhiteSpace(stream.ValidatedHlsVariant))
            {
                // Keep the master audio/subtitle groups, but start only the
                // advertised variant that the scraper actually validated.
                localUrl = _proxy.CreateVariantUrl(
                    localUrl, _proxy.CreateStreamUrl(sessionId, stream.ValidatedHlsVariant));
            }
            log($"> [Tier 1] SUCCESS - proxied {(isHls ? "HLS" : "direct media")} stream registered: {localUrl}");

            return new PlaybackSource
            {
                Tier = SourceTier.Tier1_PythonScraper,
                UrlOrPath = localUrl,
                EmbedOrigin = stream.Referer ?? string.Empty,
                Cookie = stream.Cookie,
                UserAgent = DesktopUserAgent,
                Subtitles = MediaSubtitleTrack.Copy(stream.Subtitles),
                AudioNotice = !HasEnglishAudio(stream) && stream.SelectedAudio == "dub"
                    ? "Dub server selected · audio language unverified" : string.Empty
            };
        }

        private static bool IsHlsUrl(string url) =>
            url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || url.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);

        private static bool HasEnglishAudio(ScraperStreamResult stream) =>
            stream.AudioLanguages?.Any(language => string.Equals(language, "eng", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)) == true;

        private static bool HasRequestedDub(ScraperStreamResult stream) =>
            HasEnglishAudio(stream) || (stream.SelectedAudio == "dub" &&
                stream.AudioLanguages?.Any(language => !string.IsNullOrWhiteSpace(language) &&
                    !string.Equals(language, "und", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(language, "unknown", StringComparison.OrdinalIgnoreCase)) != true);

        private static bool IsLikelyBrowserPage(string? url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                   uri.Scheme is "http" or "https" &&
                   !IsHlsUrl(url) &&
                   !Regex.IsMatch(uri.AbsolutePath, @"\.(?:mp4|mkv|avi|webm|ts|m4s)$", RegexOptions.IgnoreCase);
        }

        private PlaybackSource RegisterBrowserOnlyPage(ScraperStreamResult stream, Action<string> log)
        {
            log($"> [Tier 2] Opening the explicitly selected captured website player: {stream.Url}");

            return new PlaybackSource
            {
                Tier = SourceTier.Tier2_WebViewEmbed,
                UrlOrPath = stream.Url!
            };
        }

        // ── Helper: WebView URL builder ──────────────────────────────────────

        private async Task<string> BuildWebViewUrlAsync(
            string providerDomain,
            string query,
            string episodeId,
            Action<string> log,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(providerDomain))
            {
                throw new ArgumentException(
                    "A provider URL is required when automatic browser discovery is unavailable.",
                    nameof(providerDomain));
            }

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
                $"{domainRoot}/watch/{slug}-episode-{episodeId}",
                $"{domainRoot}/watch/{slug}?ep={episodeId}",
                $"{domainRoot}/watch/{slug}",
                $"{domainRoot}/play/{slug}/{episodeId}",
                $"{domainRoot}/anime/{slug}",
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
                    else if (!isSearchUrl && !IsLikelyPlaybackPage(url))
                    {
                        log($"> [Tier 2] Probe matched a non-player page, continuing: {url}");
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

        private static bool IsLikelyPlaybackPage(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            string pathAndQuery = (uri.AbsolutePath + uri.Query).ToLowerInvariant();
            return pathAndQuery.Contains("/watch", StringComparison.OrdinalIgnoreCase) ||
                   pathAndQuery.Contains("/play", StringComparison.OrdinalIgnoreCase) ||
                   pathAndQuery.Contains("/embed", StringComparison.OrdinalIgnoreCase) ||
                   pathAndQuery.Contains("/stream", StringComparison.OrdinalIgnoreCase) ||
                   pathAndQuery.Contains("/episode", StringComparison.OrdinalIgnoreCase) ||
                   pathAndQuery.Contains("/ep-", StringComparison.OrdinalIgnoreCase) ||
                   pathAndQuery.Contains("?ep=", StringComparison.OrdinalIgnoreCase) ||
                   pathAndQuery.Contains("&ep=", StringComparison.OrdinalIgnoreCase);
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
            return int.TryParse(raw, out int limit) && limit >= 0
                ? limit
                : 6;
        }

        private static string NormalizeAudioPreference(string? audioPreference) =>
            (audioPreference ?? string.Empty).Equals("dub", StringComparison.OrdinalIgnoreCase)
                ? "dub"
                : "sub";

        private static string GenerateSlug(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string slug = input.ToLowerInvariant();
            slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
            slug = Regex.Replace(slug, @"[\s-]+", "-").Trim('-');
            return slug;
        }

    }
}
