using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Helpers;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Dht;

namespace UniversalMediaOS.Core.Routing
{
    public class PlaybackSource
    {
        public SourceTier Tier { get; set; }
        public string UrlOrPath { get; set; } = string.Empty;
        public string EmbedOrigin { get; set; } = string.Empty;
    }

    public enum SourceTier
    {
        Tier1_LocalP2P,
        Tier2_ConsumetHttp,
        Tier3_WebViewEmbed
    }

    public class TripleNetHandoff
    {
        private readonly DualTrackerRssParser _rssParser;
        private readonly QBitLogicGate _qbit;
        private readonly Configuration.DomainHotSwapper _config;
        private readonly string _downloadDir;
        private static readonly System.Net.Http.HttpClient _httpClient = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(30) };

        private static readonly Dictionary<string, (List<string> Files, DateTime Timestamp)> _directoryCache = new Dictionary<string, (List<string>, DateTime)>();
        private static readonly object _cacheLock = new object();

        private List<string> GetCachedFiles(string dir)
        {
            lock (_cacheLock)
            {
                if (_directoryCache.TryGetValue(dir, out var cache) && (DateTime.UtcNow - cache.Timestamp).TotalSeconds < 10)
                {
                    return cache.Files;
                }
                
                var files = new List<string>();
                try
                {
                    if (Directory.Exists(dir))
                    {
                        files.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
                    }
                }
                catch { }

                _directoryCache[dir] = (files, DateTime.UtcNow);
                return files;
            }
        }

        // Timeouts
        private const int MetadataTimeoutSeconds = 60;
        private const int DownloadTimeoutSeconds = 1800; // 30 min max

        public TripleNetHandoff(Configuration.DomainHotSwapper config)
        {
            _rssParser = new DualTrackerRssParser(config);
            
            _config = config;
            
            // Read qBit config from config.json
            string qbitPort = _config.GetSetting("QBitPort");
            if (string.IsNullOrEmpty(qbitPort)) qbitPort = "8080";
            string qbitHost = _config.GetSetting("QBitHost");
            if (string.IsNullOrEmpty(qbitHost)) qbitHost = "localhost";
            _qbit = new QBitLogicGate($"http://{qbitHost}:{qbitPort}");
            
            string dDir = _config.GetSetting("DownloadDirectory");
            _downloadDir = string.IsNullOrEmpty(dDir) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalMediaOS", "Downloads") : dDir;
            
            Directory.CreateDirectory(_downloadDir);
        }

        public async Task<List<TorrentResult>> GetTorrentsAsync(string query, string episodeId, Action<string>? onStatusUpdate = null, System.Threading.CancellationToken token = default)
        {
            string fullQuery = $"{query} {episodeId}".Trim();
            var torrents = await _rssParser.SearchAsync(fullQuery, onStatusUpdate, token);

            // 1. Filter by season to prevent mismatches (e.g. S2 under S1 query)
            int targetSeason = ExtractSeasonNumber(query);
            var filtered = torrents.Where(t => ExtractSeasonNumber(t.Title) == targetSeason).ToList();
            if (filtered.Count == 0 && torrents.Count > 0)
            {
                onStatusUpdate?.Invoke($"> [Tier 1] WARNING: No torrents matched Season {targetSeason} exactly. Using broad results.");
                filtered = torrents;
            }

            // 2. Filter by audio preference (Sub vs Dub)
            string audioPref = _config.GetSetting("DefaultAudioPref");
            if (string.IsNullOrEmpty(audioPref)) audioPref = "Sub";

            bool IsDubTitle(string title)
            {
                return title.IndexOf("dub", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       title.IndexOf("dual audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       title.IndexOf("dual-audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       title.IndexOf("multi-audio", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            var audioMatched = filtered.Where(t => IsDubTitle(t.Title) == (audioPref == "Dub")).ToList();
            if (audioMatched.Count > 0)
            {
                return audioMatched;
            }
            return filtered;
        }

        public async Task<PlaybackSource?> InjectTorrentAsync(TorrentResult bestTorrent, Action<string>? onStatusUpdate = null, System.Threading.CancellationToken token = default)
        {
            void Log(string msg) { 
                onStatusUpdate?.Invoke(msg); 
                System.Diagnostics.Debug.WriteLine(msg); 
                AppLogger.Log(msg);
            }

            if (bestTorrent == null || string.IsNullOrEmpty(bestTorrent.MagnetLink))
            {
                Log("> [Tier 1] No valid torrent or magnet link provided.");
                return null;
            }

            return await _injectMagnetInternalAsync(bestTorrent, Log, token);
        }

        public async Task<PlaybackSource?> ResolveBestSourceAsync(string query, string episodeId, string providerDomain, Action<string>? onStatusUpdate = null, SourceTier minimumTier = SourceTier.Tier1_LocalP2P, System.Threading.CancellationToken token = default)
        {
            void Log(string msg) { 
                onStatusUpdate?.Invoke(msg); 
                System.Diagnostics.Debug.WriteLine(msg); 
                AppLogger.Log(msg);
            }

            // ── Tier 1: P2P Local (Nyaa → qBit / MonoTorrent) ──
            if (minimumTier <= SourceTier.Tier1_LocalP2P)
            {
                try
                {
                    string fullQuery = $"{query} {episodeId}".Trim();
                    Log($"> [Tier 1] Querying Nyaa RSS for '{fullQuery}'...");
                    var torrents = await _rssParser.SearchAsync(fullQuery, null, token);

                    // Filter by season to prevent mismatches (e.g. S2 under S1 query)
                    int targetSeason = ExtractSeasonNumber(query);
                    var filtered = torrents.Where(t => ExtractSeasonNumber(t.Title) == targetSeason).ToList();
                    if (filtered.Count == 0 && torrents.Count > 0)
                    {
                        Log($"> [Tier 1] WARNING: No torrents matched Season {targetSeason} exactly. Using broad results.");
                        filtered = torrents;
                    }

                    // Filter by audio preference (Sub vs Dub)
                    string audioPref = _config.GetSetting("DefaultAudioPref");
                    if (string.IsNullOrEmpty(audioPref)) audioPref = "Sub";

                    bool IsDub(string title)
                    {
                        return title.IndexOf("dub", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               title.IndexOf("dual audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               title.IndexOf("dual-audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               title.IndexOf("multi-audio", StringComparison.OrdinalIgnoreCase) >= 0;
                    }

                    var audioMatched = filtered.Where(t => IsDub(t.Title) == (audioPref == "Dub")).ToList();
                    var candidates = audioMatched.Count > 0 ? audioMatched : filtered;

                    var bestTorrent = candidates.OrderByDescending(t => t.Seeders).FirstOrDefault();

                    if (bestTorrent != null && !string.IsNullOrEmpty(bestTorrent.MagnetLink) && bestTorrent.Seeders >= 3)
                    {
                        Log($"> [Tier 1] Found '{bestTorrent.Title}' with {bestTorrent.Seeders} seeders!");

                        // Check if the file already exists locally
                        string? existingFile = FindExistingEpisodeFile(bestTorrent.Title);
                        if (existingFile != null)
                        {
                            Log($"> [Tier 1] Episode already downloaded: {Path.GetFileName(existingFile)}");

                            return new PlaybackSource { Tier = SourceTier.Tier1_LocalP2P, UrlOrPath = existingFile };
                        }

                        var result = await _injectMagnetInternalAsync(bestTorrent, Log);
                        if (result != null)
                            return result;
                        
                        Log("> [Tier 1] Injection failed, falling through to Tier 2...");
                    }
                    else if (bestTorrent != null && bestTorrent.Seeders < 3)
                    {
                        Log($"> [Tier 1] Best torrent '{bestTorrent.Title}' has only {bestTorrent.Seeders} seeders (minimum 3 required). Falling to Tier 2...");
                    }
                    else
                    {
                        Log("> [Tier 1] No suitable torrents found on Nyaa. Falling to Tier 2...");
                    }
                }
                catch (Exception ex)
                {
                    Log($"> [Tier 1] P2P failed: {ex.Message}. Falling to Tier 2...");
                }
            }

            // ── Tier 2: Consumet HTTP (localhost:3000) ──
            if (minimumTier <= SourceTier.Tier2_ConsumetHttp)
            {
                try
                {
                    Log("> [Tier 2] Trying Consumet HTTP streaming API...");

                    string consumetBase = _config.GetSetting("ConsumetApiBase");
                    if (string.IsNullOrEmpty(consumetBase)) consumetBase = "http://localhost:3000";

                    string provider = _config.GetSetting("ConsumetProvider");
                    if (string.IsNullOrEmpty(provider)) provider = "gogoanime";

                    string searchUrl = $"{consumetBase.TrimEnd('/')}/anime/{provider}/{Uri.EscapeDataString(query)}";
                    Log($"> [Tier 2] Searching: {searchUrl}");
                    var searchResp = await GetWithRetriesAsync(searchUrl, Log, token);

                    if (searchResp.IsSuccessStatusCode)
                    {
                        var searchJson = await searchResp.Content.ReadAsStringAsync();
                        using var searchDoc = JsonDocument.Parse(searchJson);

                        if (searchDoc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                        {
                            string resolvedEpisodeId = episodeId;

                            if (!string.IsNullOrEmpty(episodeId) && !episodeId.Contains("-episode-"))
                            {
                                var firstResult = results[0];
                                if (firstResult.TryGetProperty("id", out var animeIdProp))
                                {
                                    string? animeId = animeIdProp.GetString();
                                    resolvedEpisodeId = $"{animeId}-episode-{episodeId}";
                                }
                            }

                            string consumetBase2 = _config.GetSetting("ConsumetApiBase");
                            if (string.IsNullOrEmpty(consumetBase2)) consumetBase2 = "http://localhost:3000";
                            string watchUrl = $"{consumetBase2.TrimEnd('/')}/anime/{provider}/watch/{Uri.EscapeDataString(resolvedEpisodeId)}";
                            Log($"> [Tier 2] Fetching stream: {watchUrl}");
                            var watchResp = await GetWithRetriesAsync(watchUrl, Log, token);

                            if (watchResp.IsSuccessStatusCode)
                            {
                                var watchJson = await watchResp.Content.ReadAsStringAsync();
                                using var watchDoc = JsonDocument.Parse(watchJson);

                                if (watchDoc.RootElement.TryGetProperty("sources", out var sources) && sources.GetArrayLength() > 0)
                                {
                                    System.Text.Json.JsonElement? bestSource = null;
                                    foreach (var source in sources.EnumerateArray())
                                    {
                                        if (source.TryGetProperty("quality", out var qProp))
                                        {
                                            string quality = qProp.GetString() ?? "";
                                            if (quality == "1080p" || quality == "default")
                                            {
                                                bestSource = source;
                                                break;
                                            }
                                        }
                                    }
                                    
                                    var selectedSource = bestSource ?? sources[0];
                                    if (selectedSource.TryGetProperty("url", out var urlProp))
                                    {
                                        string? streamUrl = urlProp.GetString();
                                        if (!string.IsNullOrEmpty(streamUrl))
                                        {
                                            string referer = "";
                                            if (watchDoc.RootElement.TryGetProperty("headers", out var headers) && headers.TryGetProperty("Referer", out var refProp))
                                            {
                                                referer = refProp.GetString() ?? "";
                                            }
                                            Log($"> [Tier 2] SUCCESS: Got streaming URL");
                                            return new PlaybackSource { Tier = SourceTier.Tier2_ConsumetHttp, UrlOrPath = streamUrl, EmbedOrigin = referer };
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Log($"> [Tier 2] Watch endpoint returned {watchResp.StatusCode}. Falling to Tier 3...");
                            }
                        }
                        else
                        {
                            Log("> [Tier 2] No results from Consumet search. Falling to Tier 3...");
                        }
                    }
                    else
                    {
                        Log($"> [Tier 2] Consumet API returned {searchResp.StatusCode}. Falling to Tier 3...");
                    }
                }
                catch (Exception ex)
                {
                    Log($"> [Tier 2] Consumet HTTP failed: {ex.Message}. Falling to Tier 3...");
                }
            }

            // ── Tier 3: Web Embed Fallback ──
            Log("> [Tier 3] Falling back to Embedded WebView...");
            string safeQuery = Uri.EscapeDataString(query);
            string finalUrl = providerDomain;

            if (providerDomain.Contains("{slug}", StringComparison.OrdinalIgnoreCase) || 
                providerDomain.Contains("{episode}", StringComparison.OrdinalIgnoreCase))
            {
                string slug = GenerateSlug(query);
                finalUrl = Regex.Replace(finalUrl, @"\{slug\}", slug, RegexOptions.IgnoreCase);
                finalUrl = Regex.Replace(finalUrl, @"\{episode\}", episodeId, RegexOptions.IgnoreCase);
            }
            else if (providerDomain.Contains("{query}", StringComparison.OrdinalIgnoreCase))
            {
                finalUrl = Regex.Replace(finalUrl, @"\{query\}", safeQuery, RegexOptions.IgnoreCase);
            }
            else if (providerDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                     providerDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                finalUrl = await ResolveHomepageDomainAsync(providerDomain, query, episodeId);
            }
            else
            {
                finalUrl = $"{providerDomain.TrimEnd('/')}/search?keyword={safeQuery}";
            }

            return new PlaybackSource
            {
                Tier = SourceTier.Tier3_WebViewEmbed,
                UrlOrPath = finalUrl
            };
        }

        // ── Shared magnet injection helper ──
        private async Task<PlaybackSource?> _injectMagnetInternalAsync(TorrentResult torrent, Action<string> log, System.Threading.CancellationToken token = default)
        {
            string magnetLink = torrent.MagnetLink;

            // 1. Try qBit WebUI first
            log("> [Tier 1] Attempting injection into local qBittorrent WebUI...");
            
            string qbitUser = _config.GetSetting("QBitUsername");
            string qbitPass = _config.GetSetting("QBitPassword");
            if (string.IsNullOrEmpty(qbitUser)) qbitUser = "admin";
            if (string.IsNullOrEmpty(qbitPass)) qbitPass = "adminadmin";
            
            bool qbitAuth = await _qbit.AuthenticateAsync(log, qbitUser, qbitPass, token);
            if (qbitAuth && await _qbit.AddMagnetAsync(magnetLink, _downloadDir, token))
            {
                log("> [Tier 1] SUCCESS: Magnet injected into qBittorrent WebUI!");
                log("> [Tier 1] Monitoring download progress...");
                
                // Monitor until complete, then find the actual file
                string infoHash = torrent.InfoHash;
                if (string.IsNullOrEmpty(infoHash))
                {
                    // Extract info hash from magnet link
                    var match = System.Text.RegularExpressions.Regex.Match(magnetLink, @"btih:([a-fA-F0-9]{40})");
                    if (match.Success) infoHash = match.Groups[1].Value;
                }

                if (!string.IsNullOrEmpty(infoHash))
                {
                    bool completed = await _qbit.MonitorDownloadAsync(infoHash, log, DownloadTimeoutSeconds, token);
                    if (completed)
                    {
                        var files = await _qbit.GetTorrentFilesAsync(infoHash, token);
                        if (files.Count > 0)
                        {
                            // Find the largest video file using qBittorrent API reported byte size
                            var bestFileObj = files
                                .Where(f => f.Name.EndsWith(".mkv") || f.Name.EndsWith(".mp4") || f.Name.EndsWith(".avi") || f.Name.EndsWith(".webm"))
                                .OrderByDescending(f => f.Size)
                                .FirstOrDefault();
                            
                            string? bestFile = bestFileObj?.Name;
                            
                            if (bestFile != null)
                            {
                                string fullPath = Path.Combine(_downloadDir, bestFile);
                                if (!File.Exists(fullPath))
                                {
                                    string fileName = Path.GetFileName(bestFile);
                                    try
                                    {
                                        var found = Directory.EnumerateFiles(_downloadDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                                        if (found != null) fullPath = found;
                                    }
                                    catch { }
                                }
                                if (File.Exists(fullPath))
                                {
                                    log($"> [Tier 1] Download complete: {Path.GetFileName(fullPath)}");
                                    return new PlaybackSource { Tier = SourceTier.Tier1_LocalP2P, UrlOrPath = fullPath };
                                }
                            }
                        }
                        
                        // Fallback: scan download dir for recently created files
                        string? foundFile = FindExistingEpisodeFile(torrent.Title);
                        if (foundFile != null)
                        {
                            log($"> [Tier 1] Found downloaded file: {Path.GetFileName(foundFile)}");
                            return new PlaybackSource { Tier = SourceTier.Tier1_LocalP2P, UrlOrPath = foundFile };
                        }
                    }
                    else
                    {
                        log("> [Tier 1] qBit download timed out.");
                    }
                }
                
                // qBit succeeded adding but we couldn't track the file — return what we know
                log("> [Tier 1] Torrent added to qBittorrent but file path could not be resolved.");
                return null;
            }

            // 2. Use embedded MonoTorrent engine natively
            log("> [Tier 1] qBittorrent WebUI unreachable. Using built-in MonoTorrent engine...");
            try
            {
                var settingsBuilder = new EngineSettingsBuilder
                {
                    AllowPortForwarding = true,
                    AutoSaveLoadDhtCache = true,
                    AutoSaveLoadFastResume = true,
                    AutoSaveLoadMagnetLinkMetadata = true,
                    DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
                    ListenEndPoints = new Dictionary<string, IPEndPoint>
                    {
                        { "ipv4", new IPEndPoint(IPAddress.Any, 0) }
                    }
                };
                using (var engine = new ClientEngine(settingsBuilder.ToSettings()))
                {
                    var magnet = MagnetLink.Parse(magnetLink);
                    var manager = await engine.AddAsync(magnet, _downloadDir);
                    await manager.StartAsync();

                    // Metadata resolution with timeout
                    log("> [Tier 1] Resolving magnet metadata...");
                    var metadataDeadline = DateTime.UtcNow.AddSeconds(MetadataTimeoutSeconds);
                    try
                    {
                        while (!manager.HasMetadata)
                        {
                            token.ThrowIfCancellationRequested();
                            if (DateTime.UtcNow > metadataDeadline)
                            {
                                log($"> [Tier 1] Metadata resolution timed out after {MetadataTimeoutSeconds}s.");
                                await manager.StopAsync();
                                return null;
                            }
                            await Task.Delay(1000, token);
                        }

                        log($"> [Tier 1] Downloading {manager.Torrent?.Name ?? "torrent"}...");

                        // Download with timeout
                        var downloadDeadline = DateTime.UtcNow.AddSeconds(DownloadTimeoutSeconds);
                        while (manager.State != TorrentState.Seeding && manager.State != TorrentState.Stopped && manager.State != TorrentState.Error)
                        {
                            token.ThrowIfCancellationRequested();
                            if (DateTime.UtcNow > downloadDeadline)
                            {
                                log($"> [Tier 1] Download timed out after {DownloadTimeoutSeconds / 60} minutes.");
                                await manager.StopAsync();
                                return null;
                            }
                            log($"> [Tier 1] Progress: {manager.Progress:0.00}% - {manager.Monitor.DownloadRate / 1024.0 / 1024.0:0.00} MB/s");
                            await Task.Delay(2000, token);
                            if (manager.Progress >= 100.0) break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        log("> [Tier 1] MonoTorrent download cancelled.");
                        await manager.StopAsync();
                        throw;
                    }

                    if (manager.Progress < 100.0)
                    {
                        log($"> [Tier 1] Download aborted or failed. Final progress: {manager.Progress:0.00}%");
                        await manager.StopAsync();
                        return null;
                    }

                    log("> [Tier 1] Download complete!");

                    var largestFile = manager.Files.OrderByDescending(f => f.Length).FirstOrDefault();
                    if (largestFile != null)
                    {
                        string finalPath = largestFile.FullPath;
                        if (!File.Exists(finalPath))
                        {
                            string fileName = Path.GetFileName(finalPath);
                            try
                            {
                                var found = Directory.EnumerateFiles(_downloadDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                                if (found != null) finalPath = found;
                            }
                            catch { }
                        }
                        log($"> [Tier 1] File: {finalPath}");
                        return new PlaybackSource { Tier = SourceTier.Tier1_LocalP2P, UrlOrPath = finalPath };
                    }
                }
            }
            catch (Exception ex)
            {
                log($"> [Tier 1] MonoTorrent failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Checks the Downloads directory for an existing file that matches the torrent title.
        /// </summary>
        private string? FindExistingEpisodeFile(string torrentTitle)
        {
            if (!Directory.Exists(_downloadDir))
                return null;

            try
            {
                string[] videoExtensions = { ".mkv", ".mp4", ".avi", ".webm" };
                var files = GetCachedFiles(_downloadDir);

                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!videoExtensions.Contains(ext))
                        continue;

                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.IndexOf(torrentTitle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        torrentTitle.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return file;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning downloads directory: {ex.Message}");
            }

            return null;
        }

        private int ExtractSeasonNumber(string title)
        {
            if (string.IsNullOrEmpty(title)) return 1;

            // 1. Check "Season X"
            var match = Regex.Match(title, @"Season\s*(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int s1)) return s1;

            // 2. Check "S X" (e.g. S1, S2, S01, S02) with boundary support for S02E05
            match = Regex.Match(title, @"\bS(\d+)(?=E\d|\b)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int s2)) return s2;

            // 3. Check "Xnd Season" ordinals (e.g. 2nd Season, 3rd Season)
            match = Regex.Match(title, @"(\d+)(?:st|nd|rd|th)\s*Season", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int s3)) return s3;

            return 1; // Default to Season 1
        }

        private async Task<HttpResponseMessage> GetWithRetriesAsync(string url, Action<string> log, System.Threading.CancellationToken token = default)
        {
            int maxRetries = 3;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var response = await _httpClient.GetAsync(url, token);
                    if (response.IsSuccessStatusCode)
                        return response;
                        
                    log($"> [Tier 2] HTTP {response.StatusCode} for {url}. Retrying...");
                }
                catch (Exception ex)
                {
                    log($"> [Tier 2] Exception for {url}: {ex.Message}. Retrying...");
                }

                if (i < maxRetries - 1)
                {
                    int delayMs = (int)Math.Pow(2, i) * 1000;
                    await Task.Delay(delayMs, token);
                }
            }
            
            // Final attempt to return whatever response we have, or a failed message
            return await _httpClient.GetAsync(url, token);
        }

        private string GenerateSlug(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string slug = input.ToLowerInvariant();
            slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
            slug = Regex.Replace(slug, @"[\s-]+", "-").Trim('-');
            return slug;
        }

        private async Task<string> ResolveHomepageDomainAsync(string baseDomain, string query, string episodeId)
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log($"Resolving homepage-only domain: '{baseDomain}' using heuristics...");
            string slug = GenerateSlug(query);
            string safeQuery = Uri.EscapeDataString(query);

            string domainRoot = baseDomain;
            try
            {
                var uri = new Uri(baseDomain);
                domainRoot = $"{uri.Scheme}://{uri.Host}";
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Error resolving domain root: {ex.Message}", "WARNING");
            }
            
            var patterns = new System.Collections.Generic.List<string>
            {
                $"{domainRoot.TrimEnd('/')}/anime/{slug}",
                $"{domainRoot.TrimEnd('/')}/watch/{slug}-episode-{episodeId}",
                $"{domainRoot.TrimEnd('/')}/watch/{slug}",
                $"{domainRoot.TrimEnd('/')}/category/{slug}",
                $"{domainRoot.TrimEnd('/')}/search?q={safeQuery}",
                $"{domainRoot.TrimEnd('/')}/search?keyword={safeQuery}",
                $"{domainRoot.TrimEnd('/')}/search.html?keyword={safeQuery}"
            };

            foreach (var url in patterns)
            {
                try
                {
                    UniversalMediaOS.Core.Helpers.AppLogger.Log($"Probing candidate URL: '{url}'...");
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    
                    bool isSearchUrl = url.Contains("/search") || url.Contains("search.html");
                    
                    using var response = await _httpClient.SendAsync(
                        request, 
                        isSearchUrl ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead, 
                        new System.Threading.CancellationTokenSource(6000).Token);

                    if (response.IsSuccessStatusCode)
                    {
                        UniversalMediaOS.Core.Helpers.AppLogger.Log($"SUCCESS: Probe matched '{url}' (Status: {response.StatusCode})");
                        
                        if (isSearchUrl)
                        {
                            string html = await response.Content.ReadAsStringAsync();
                            string? directLink = ExtractBestLinkFromSearchHtml(html, domainRoot, query, episodeId);
                            if (directLink != null)
                            {
                                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Smart Scraper successfully resolved direct watch/details link from search results: '{directLink}'");
                                return directLink;
                            }
                            UniversalMediaOS.Core.Helpers.AppLogger.Log("Smart Scraper could not extract a high-scoring direct link from search HTML. Falling back to the search URL.");
                        }
                        
                        return url;
                    }
                    else
                    {
                        UniversalMediaOS.Core.Helpers.AppLogger.Log($"Probe failed for '{url}' (Status: {response.StatusCode})");
                    }
                }
                catch (Exception ex)
                {
                    UniversalMediaOS.Core.Helpers.AppLogger.Log($"Exception probing '{url}': {ex.Message}");
                }
            }

            UniversalMediaOS.Core.Helpers.AppLogger.Log($"Heuristics yielded no matches. Falling back to base domain '{baseDomain}'.");
            return baseDomain;
        }

        private string? ExtractBestLinkFromSearchHtml(string html, string domainRoot, string query, string episodeId)
        {
            var hrefMatches = Regex.Matches(html, @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            var candidates = new System.Collections.Generic.List<(string url, int score)>();
            
            // Clean up the query to extract alphanumeric keywords
            var keywords = Regex.Matches(query.ToLowerInvariant(), @"[a-z0-9]{3,}")
                                .Cast<Match>()
                                .Select(m => m.Value)
                                .ToList();
            
            if (keywords.Count == 0) return null;

            foreach (Match match in hrefMatches)
            {
                string href = match.Groups[1].Value;
                
                // Skip static/common non-media paths
                if (href.EndsWith(".css") || href.EndsWith(".js") || href.EndsWith(".png") || href.EndsWith(".jpg") || href.EndsWith(".gif") || href.EndsWith(".woff") || href.EndsWith(".svg"))
                    continue;
                
                string lowerHref = href.ToLowerInvariant();
                bool isWatchOrDetails = lowerHref.Contains("/watch") || 
                                        lowerHref.Contains("/anime") || 
                                        lowerHref.Contains("/category") ||
                                        lowerHref.Contains("/series") ||
                                        lowerHref.Contains("/show") ||
                                        lowerHref.Contains("/play");
                                        
                if (!isWatchOrDetails) continue;

                // Resolve relative paths to absolute URLs
                string fullUrl = href;
                if (href.StartsWith("/"))
                {
                    fullUrl = $"{domainRoot.TrimEnd('/')}{href}";
                }
                else if (!href.StartsWith("http://") && !href.StartsWith("https://"))
                {
                    fullUrl = $"{domainRoot.TrimEnd('/')}/{href}";
                }

                int score = 0;
                foreach (var word in keywords)
                {
                    if (lowerHref.Contains(word))
                    {
                        score++;
                    }
                }

                if (score > 0)
                {
                    // Prioritize links that match the specific episode if episodeId is provided
                    if (!string.IsNullOrEmpty(episodeId))
                    {
                        if (lowerHref.Contains($"-episode-{episodeId}") || 
                            lowerHref.Contains($"/ep-{episodeId}") ||
                            lowerHref.EndsWith($"-{episodeId}") ||
                            lowerHref.EndsWith($"/{episodeId}"))
                        {
                            score += 5;
                        }
                    }
                    candidates.Add((fullUrl, score));
                }
            }

            var best = candidates.OrderByDescending(c => c.score).FirstOrDefault();
            if (best.score >= 1)
            {
                return best.url;
            }

            return null;
        }
    }
}
