using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Services;
using MonoTorrent;
using MonoTorrent.Client;

namespace UniversalMediaOS.Core.Routing
{
    public class PlaybackSource
    {
        public SourceTier Tier { get; set; }
        public string UrlOrPath { get; set; } = string.Empty;
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
        private readonly string _downloadDir;
        private readonly Configuration.DomainHotSwapper _config;

        // Timeouts
        private const int MetadataTimeoutSeconds = 60;
        private const int DownloadTimeoutSeconds = 1800; // 30 min max

        public TripleNetHandoff(Configuration.DomainHotSwapper config)
        {
            _rssParser = new DualTrackerRssParser();
            
            _config = config;
            
            // Read qBit config from config.json
            string qbitPort = _config.GetSetting("QBitPort");
            if (string.IsNullOrEmpty(qbitPort)) qbitPort = "8080";
            _qbit = new QBitLogicGate($"http://localhost:{qbitPort}");
            
            string dDir = _config.GetSetting("DownloadDirectory");
            _downloadDir = string.IsNullOrEmpty(dDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads") : dDir;
            
            Directory.CreateDirectory(_downloadDir);
        }

        public async Task<List<TorrentResult>> GetTorrentsAsync(string query, string episodeId, Action<string>? onStatusUpdate = null)
        {
            string fullQuery = $"{query} {episodeId}".Trim();
            var torrents = await _rssParser.SearchAsync(fullQuery, onStatusUpdate);

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

        public async Task<PlaybackSource?> InjectTorrentAsync(TorrentResult bestTorrent, Action<string>? onStatusUpdate = null)
        {
            void Log(string msg) { onStatusUpdate?.Invoke(msg); System.Diagnostics.Debug.WriteLine(msg); }

            if (bestTorrent == null || string.IsNullOrEmpty(bestTorrent.MagnetLink))
            {
                Log("> [Tier 1] No valid torrent or magnet link provided.");
                return null;
            }

            return await _injectMagnetInternalAsync(bestTorrent, Log);
        }

        public async Task<PlaybackSource?> ResolveBestSourceAsync(string query, string episodeId, string providerDomain, Action<string>? onStatusUpdate = null, SourceTier minimumTier = SourceTier.Tier1_LocalP2P)
        {
            void Log(string msg) { onStatusUpdate?.Invoke(msg); System.Diagnostics.Debug.WriteLine(msg); }

            // ── Tier 1: P2P Local (Nyaa → qBit / MonoTorrent) ──
            if (minimumTier <= SourceTier.Tier1_LocalP2P)
            {
                try
                {
                    string fullQuery = $"{query} {episodeId}".Trim();
                    Log($"> [Tier 1] Querying Nyaa RSS for '{fullQuery}'...");
                    var torrents = await _rssParser.SearchAsync(fullQuery);

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
                            bestTorrent.DownloadedFilePath = existingFile;
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
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(10);

                    string consumetBase = _config.GetSetting("ConsumetApiBase");
                    if (string.IsNullOrEmpty(consumetBase)) consumetBase = "http://localhost:3000";
                    string searchUrl = $"{consumetBase.TrimEnd('/')}/anime/gogoanime/{Uri.EscapeDataString(query)}";
                    Log($"> [Tier 2] Searching: {searchUrl}");
                    var searchResponse = await client.GetAsync(searchUrl);

                    if (searchResponse.IsSuccessStatusCode)
                    {
                        var searchJson = await searchResponse.Content.ReadAsStringAsync();
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
                            string watchUrl = $"{consumetBase2.TrimEnd('/')}/anime/gogoanime/watch/{Uri.EscapeDataString(resolvedEpisodeId)}";
                            Log($"> [Tier 2] Fetching stream: {watchUrl}");
                            var watchResponse = await client.GetAsync(watchUrl);

                            if (watchResponse.IsSuccessStatusCode)
                            {
                                var watchJson = await watchResponse.Content.ReadAsStringAsync();
                                using var watchDoc = JsonDocument.Parse(watchJson);

                                if (watchDoc.RootElement.TryGetProperty("sources", out var sources) && sources.GetArrayLength() > 0)
                                {
                                    var firstSource = sources[0];
                                    if (firstSource.TryGetProperty("url", out var urlProp))
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
                                Log($"> [Tier 2] Watch endpoint returned {watchResponse.StatusCode}. Falling to Tier 3...");
                            }
                        }
                        else
                        {
                            Log("> [Tier 2] No results from Consumet search. Falling to Tier 3...");
                        }
                    }
                    else
                    {
                        Log($"> [Tier 2] Consumet API returned {searchResponse.StatusCode}. Falling to Tier 3...");
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
            string finalUrl = providerDomain.Contains("{query}")
                ? providerDomain.Replace("{query}", safeQuery)
                : $"{providerDomain}/search?keyword={safeQuery}";

            return new PlaybackSource
            {
                Tier = SourceTier.Tier3_WebViewEmbed,
                UrlOrPath = finalUrl
            };
        }

        // ── Shared magnet injection helper ──
        private async Task<PlaybackSource?> _injectMagnetInternalAsync(TorrentResult torrent, Action<string> log)
        {
            string magnetLink = torrent.MagnetLink;

            // 1. Try qBit WebUI first
            log("> [Tier 1] Attempting injection into local qBittorrent WebUI...");
            
            string qbitUser = _config.GetSetting("QBitUsername");
            string qbitPass = _config.GetSetting("QBitPassword");
            if (string.IsNullOrEmpty(qbitUser)) qbitUser = "admin";
            if (string.IsNullOrEmpty(qbitPass)) qbitPass = "adminadmin";
            
            bool qbitAuth = await _qbit.AuthenticateAsync(log, qbitUser, qbitPass);
            if (qbitAuth && await _qbit.AddMagnetAsync(magnetLink, _downloadDir))
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
                    bool completed = await _qbit.MonitorDownloadAsync(infoHash, log, DownloadTimeoutSeconds);
                    if (completed)
                    {
                        var files = await _qbit.GetTorrentFilesAsync(infoHash);
                        if (files.Count > 0)
                        {
                            // Find the largest video file
                            string? bestFile = files
                                .Where(f => f.EndsWith(".mkv") || f.EndsWith(".mp4") || f.EndsWith(".avi") || f.EndsWith(".webm"))
                                .OrderByDescending(f => 
                                {
                                    string fullPath = Path.Combine(_downloadDir, f);
                                    return File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                                })
                                .FirstOrDefault();
                            
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
                using (var engine = new ClientEngine())
                {
                    var magnet = MagnetLink.Parse(magnetLink);
                    var manager = await engine.AddAsync(magnet, _downloadDir);
                    await manager.StartAsync();

                    // Metadata resolution with timeout
                    log("> [Tier 1] Resolving magnet metadata...");
                    var metadataDeadline = DateTime.UtcNow.AddSeconds(MetadataTimeoutSeconds);
                    while (!manager.HasMetadata)
                    {
                        if (DateTime.UtcNow > metadataDeadline)
                        {
                            log($"> [Tier 1] Metadata resolution timed out after {MetadataTimeoutSeconds}s.");
                            await manager.StopAsync();
                            return null;
                        }
                        await Task.Delay(1000);
                    }

                    log($"> [Tier 1] Downloading {manager.Torrent.Name}...");

                    // Download with timeout
                    var downloadDeadline = DateTime.UtcNow.AddSeconds(DownloadTimeoutSeconds);
                    while (manager.State != TorrentState.Seeding && manager.State != TorrentState.Stopped && manager.State != TorrentState.Error)
                    {
                        if (DateTime.UtcNow > downloadDeadline)
                        {
                            log($"> [Tier 1] Download timed out after {DownloadTimeoutSeconds / 60} minutes.");
                            await manager.StopAsync();
                            return null;
                        }
                        log($"> [Tier 1] Progress: {manager.Progress:0.00}% - {manager.Monitor.DownloadRate / 1024.0 / 1024.0:0.00} MB/s");
                        await Task.Delay(2000);
                        if (manager.Progress >= 100.0) break;
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

                foreach (var file in Directory.EnumerateFiles(_downloadDir, "*", SearchOption.AllDirectories))
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
            // 1. Check "Season X"
            var match = Regex.Match(title, @"Season\s*(\d+)", RegexOptions.IgnoreCase);
            if (match.Success) return int.Parse(match.Groups[1].Value);

            // 2. Check "S X" (e.g. S1, S2, S01, S02) with boundary support for S02E05
            match = Regex.Match(title, @"\bS(\d+)(?=E\d|\b)", RegexOptions.IgnoreCase);
            if (match.Success) return int.Parse(match.Groups[1].Value);

            // 3. Check "Xnd Season" ordinals (e.g. 2nd Season, 3rd Season)
            match = Regex.Match(title, @"(\d+)(?:st|nd|rd|th)\s*Season", RegexOptions.IgnoreCase);
            if (match.Success) return int.Parse(match.Groups[1].Value);

            return 1; // Default to Season 1
        }
    }
}
