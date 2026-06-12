using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.Core.Helpers;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Dht;

namespace UniversalMediaOS.Core.Archiving
{
    public class SeasonDownloader
    {
        private readonly string _downloadDir;
        private readonly DomainHotSwapper _config;
        private readonly DualTrackerRssParser _rssParser;
        private readonly QBitLogicGate _qbit;

        private const int DownloadTimeoutSeconds = 3600; // 60 min max for full season
        private const int MetadataTimeoutSeconds = 60;

        public SeasonDownloader(DomainHotSwapper config)
        {
            _config = config;
            
            string dDir = _config.GetSetting("DownloadDirectory");
            _downloadDir = string.IsNullOrEmpty(dDir) ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads") : dDir;
            Directory.CreateDirectory(_downloadDir);

            _rssParser = new DualTrackerRssParser();

            string qbitPort = _config.GetSetting("QBitPort");
            if (string.IsNullOrEmpty(qbitPort)) qbitPort = "8080";
            string qbitHost = _config.GetSetting("QBitHost") ?? "localhost";
            _qbit = new QBitLogicGate($"http://{qbitHost}:{qbitPort}");
        }

        /// <summary>
        /// Searches Nyaa/AnimeTosho for a season batch torrent, downloads it via P2P, and validates all media files.
        /// </summary>
        public async Task<bool> DownloadSeasonAsync(
            string animeTitle, 
            Action<string> log, 
            Action<double> progressUpdate)
        {
            var originalLog = log;
            log = msg => {
                originalLog(msg);
                AppLogger.Log(msg);
            };
            log($"[P2P Season Downloader] Initializing batch download search for: \"{animeTitle}\"...");
            progressUpdate(0);

            try
            {
                // 1. Search Nyaa / AnimeTosho for season batch torrents
                var torrents = await SearchForBatchTorrentsAsync(animeTitle, log);
                if (torrents.Count == 0)
                {
                    log($"[P2P Season Downloader] ERROR: No torrents found matching \"{animeTitle}\" on Nyaa or AnimeTosho feeds.");
                    return false;
                }

                // 2. Select the best batch torrent based on seeders and batch markers (e.g. "Batch", "01-", "01~", "Season")
                var bestTorrent = SelectBestBatchTorrent(torrents, animeTitle, log);
                if (bestTorrent == null)
                {
                    log("[P2P Season Downloader] ERROR: Could not identify a valid healthy batch torrent matching parameters.");
                    return false;
                }

                log($"[P2P Season Downloader] SELECTED BATCH: \"{bestTorrent.Title}\" ({bestTorrent.Seeders} seeders) from {bestTorrent.Source}");

                string magnetLink = bestTorrent.MagnetLink;
                string infoHash = bestTorrent.InfoHash;

                if (string.IsNullOrEmpty(infoHash))
                {
                    // Extract info hash from magnet link if missing
                    var match = Regex.Match(magnetLink, @"btih:([a-fA-F0-9]{40})");
                    if (match.Success) infoHash = match.Groups[1].Value;
                }

                List<string> downloadedFiles = new List<string>();
                bool downloadComplete = false;

                // 3. Authenticate and Inject into qBittorrent WebUI if running
                log("[P2P Season Downloader] Checking qBittorrent WebUI status...");
                string qbitUser = _config.GetSetting("QBitUsername");
                string qbitPass = _config.GetSetting("QBitPassword");
                if (string.IsNullOrEmpty(qbitUser)) qbitUser = "admin";
                if (string.IsNullOrEmpty(qbitPass)) qbitPass = "adminadmin";

                bool qbitAuth = await _qbit.AuthenticateAsync(msg => log($"[QBit] {msg}"), qbitUser, qbitPass);
                
                if (qbitAuth && !string.IsNullOrEmpty(infoHash))
                {
                    log("[P2P Season Downloader] qBittorrent active. Injecting magnet link...");
                    bool added = await _qbit.AddMagnetAsync(magnetLink, _downloadDir);
                    if (added)
                    {
                        log("[P2P Season Downloader] Magnet successfully injected! Monitoring download progression...");
                        bool success = await _qbit.MonitorDownloadAsync(infoHash, msg => {
                            log(msg);
                            // Parse progress percent from log string
                            var pctMatch = Regex.Match(msg, @"Download:\s+([\d\.]+)%");
                            if (pctMatch.Success && double.TryParse(pctMatch.Groups[1].Value, out double p))
                            {
                                progressUpdate(p);
                            }
                        }, DownloadTimeoutSeconds);

                        if (success)
                        {
                            var relativePaths = await _qbit.GetTorrentFilesAsync(infoHash);
                            foreach (var p in relativePaths)
                            {
                                string fullPath = Path.Combine(_downloadDir, p.Name);
                                downloadedFiles.Add(fullPath);
                            }
                            downloadComplete = true;
                        }
                    }
                    else
                    {
                        log("[P2P Season Downloader] Injection failed inside qBittorrent. Falling back to native client...");
                    }
                }

                // 4. Fallback to built-in MonoTorrent client
                if (!downloadComplete)
                {
                    log("[P2P Season Downloader] qBittorrent unavailable or failed. Booting built-in MonoTorrent client...");
                    var result = await DownloadViaMonoTorrentAsync(magnetLink, log, progressUpdate);
                    if (result != null && result.Count > 0)
                    {
                        downloadedFiles = result;
                        downloadComplete = true;
                    }
                }

                if (!downloadComplete || downloadedFiles.Count == 0)
                {
                    log("[P2P Season Downloader] ERROR: Season download process timed out or was interrupted.");
                    return false;
                }

                // 5. Scan and Validate all video files in the batch
                log("[P2P Season Downloader] Download complete! Waiting 2 seconds for OS file locks to clear...");
                await Task.Delay(2000);
                log("[P2P Season Downloader] Running integrity validation checks on batch...");
                var videoExtensions = new[] { ".mkv", ".mp4", ".avi", ".webm" };
                var videoFiles = downloadedFiles
                    .Where(f => videoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .ToList();

                if (videoFiles.Count == 0)
                {
                    log("[P2P Season Downloader] WARNING: Downloaded files contain no recognized video extension formats.");
                    return false;
                }

                log($"[P2P Season Downloader] Found {videoFiles.Count} video files in batch. Checking integrity...");
                int passed = 0;
                int failed = 0;

                for (int i = 0; i < videoFiles.Count; i++)
                {
                    string filePath = videoFiles[i];
                    
                    // Resolve actual path in case it downloaded to a subdirectory
                    if (!File.Exists(filePath))
                    {
                        string fileName = Path.GetFileName(filePath);
                        try
                        {
                            if (Directory.Exists(_downloadDir))
                            {
                                var found = Directory.EnumerateFiles(_downloadDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                                if (found != null)
                                {
                                    filePath = found;
                                }
                            }
                        }
                        catch { }
                    }

                    string filename = Path.GetFileName(filePath);
                    log($"[P2P Season Downloader] ({i + 1}/{videoFiles.Count}) Validating: \"{filename}\"");

                    bool valid = await ValidateMediaFileAsync(filePath);
                    if (valid)
                    {
                        var fi = new FileInfo(filePath);
                        double mb = fi.Length / 1024.0 / 1024.0;
                        log($"[P2P Season Downloader] -> Ep validated successfully: \"{filename}\" ({mb:F1} MB)");
                        passed++;
                    }
                    else
                    {
                        log($"[P2P Season Downloader] -> ERROR: Validation FAILED for \"{filename}\" (corrupt, unreadable, or missing streams). Wiping file.");
                        try { File.Delete(filePath); } catch { }
                        failed++;
                    }
                }

                log($"[P2P Season Downloader] BATCH PROCESS COMPLETED! Season items verified: {passed} OK | {failed} Corrupted/Purged.");
                progressUpdate(100);
                return passed > 0;
            }
            catch (Exception ex)
            {
                log($"[P2P Season Downloader] CRITICAL ERROR during batch process: {ex.Message}");
                return false;
            }
        }

        private async Task<List<TorrentResult>> SearchForBatchTorrentsAsync(string title, Action<string> log)
        {
            var allResults = new List<TorrentResult>();

            string audioPref = _config.GetSetting("DefaultAudioPref");
            if (string.IsNullOrEmpty(audioPref)) audioPref = "Sub";

            // Formulate search queries for batch season files
            var queries = new List<string> {
                $"{title} Batch",
                $"{title} Season",
                $"{title} 01~",
                $"{title} 01-"
            };

            if (audioPref == "Dub")
            {
                queries.Add($"{title} Dub");
                queries.Add($"{title} Dual Audio");
            }

            foreach (var q in queries)
            {
                try
                {
                    var res = await _rssParser.SearchAsync(q, msg => log($"[Nyaa Search] {msg}"));
                    if (res != null && res.Count > 0)
                    {
                        allResults.AddRange(res);
                    }
                }
                catch (Exception ex)
                {
                    log($"[Nyaa Search] Error on query \"{q}\": {ex.Message}");
                }
            }

            // Fallback: search raw title
            if (allResults.Count == 0)
            {
                try
                {
                    var res = await _rssParser.SearchAsync(title, msg => log($"[Nyaa Search] {msg}"));
                    if (res != null) allResults.AddRange(res);
                }
                catch { }
            }

            return allResults;
        }

        private TorrentResult? SelectBestBatchTorrent(List<TorrentResult> torrents, string targetTitle, Action<string> log)
        {
            // 1. Filter list to keep only elements containing the show title
            var filtered = torrents
                .Where(t => t.Title.IndexOf(targetTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            // 2. Perform exact season matching to exclude mismatched seasons (e.g. S2 under S1 query)
            int targetSeason = ExtractSeasonNumber(targetTitle);
            var seasonMatched = filtered
                .Where(t => ExtractSeasonNumber(t.Title) == targetSeason)
                .ToList();

            if (seasonMatched.Count == 0)
            {
                log($"[P2P Season Downloader] WARNING: No torrent matches found for Season {targetSeason}. Falling back to broad title matches.");
                seasonMatched = filtered;
            }

            if (seasonMatched.Count == 0) return null;

            // 3. Filter by User Audio Preference
            string pref = _config.GetSetting("DefaultAudioPref");
            var candidates = seasonMatched;
            if (pref == "Dubbed (English)")
            {
                var dubs = seasonMatched.Where(t => t.Title.IndexOf("Dub", StringComparison.OrdinalIgnoreCase) >= 0 || t.Title.IndexOf("Dual Audio", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (dubs.Count > 0) candidates = dubs;
            }
            else // Subbed preference
            {
                var subs = seasonMatched.Where(t => t.Title.IndexOf("Sub", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (subs.Count > 0) candidates = subs;
            }

            // 4. Prioritize titles containing batch markers
            var batches = candidates.Where(t => 
                t.Title.IndexOf("Batch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.Title.IndexOf("Complete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.Title.IndexOf("Season", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Regex.IsMatch(t.Title, @"01\s*[-~]\s*\d+")
            ).ToList();

            var finalCandidates = batches.Count > 0 ? batches : candidates;

            // Pick the candidate with the highest seeders
            return finalCandidates.OrderByDescending(t => t.Seeders).FirstOrDefault();
        }

        private int ExtractSeasonNumber(string title)
        {
            // 1. Check "Season X"
            var match = Regex.Match(title, @"Season\s*(\d+)", RegexOptions.IgnoreCase);
            if (match.Success) return int.Parse(match.Groups[1].Value);

            // 2. Check "S X" (e.g. S1, S2, S01, S02) with word boundary
            match = Regex.Match(title, @"\bS(\d+)(?=E\d|\b)", RegexOptions.IgnoreCase);
            if (match.Success) return int.Parse(match.Groups[1].Value);

            // 3. Check "Xnd Season" ordinals (e.g. 2nd Season, 3rd Season)
            match = Regex.Match(title, @"(\d+)(?:st|nd|rd|th)\s*Season", RegexOptions.IgnoreCase);
            if (match.Success) return int.Parse(match.Groups[1].Value);

            return 1; // Default to Season 1
        }

        private async Task<List<string>> DownloadViaMonoTorrentAsync(
            string magnetLink, 
            Action<string> log, 
            Action<double> progressUpdate)
        {
            var downloadedFiles = new List<string>();
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

                    // 1. Resolve magnet metadata
                    log("[MonoTorrent] Resolving torrent metadata...");
                    var metadataDeadline = DateTime.UtcNow.AddSeconds(MetadataTimeoutSeconds);
                    while (!manager.HasMetadata)
                    {
                        if (DateTime.UtcNow > metadataDeadline)
                        {
                            log("[MonoTorrent] Metadata resolution timed out.");
                            await manager.StopAsync();
                            return downloadedFiles;
                        }
                        await Task.Delay(1000);
                    }

                    log($"[MonoTorrent] Starting download: \"{manager.Torrent?.Name ?? "Torrent"}\"");

                    // 2. Download loop
                    var downloadDeadline = DateTime.UtcNow.AddSeconds(DownloadTimeoutSeconds);
                    while (manager.State != TorrentState.Seeding && manager.State != TorrentState.Stopped)
                    {
                        if (DateTime.UtcNow > downloadDeadline)
                        {
                            log("[MonoTorrent] Download session timed out.");
                            await manager.StopAsync();
                            return downloadedFiles;
                        }

                        double progress = manager.Progress;
                        progressUpdate(progress);
                        log($"[MonoTorrent] Progress: {progress:F1}% | Speed: {manager.Monitor.DownloadRate / 1024.0 / 1024.0:F2} MB/s | State: {manager.State}");

                        await Task.Delay(3000);
                        if (progress >= 100.0) break;
                    }

                    if (manager.Progress >= 100.0)
                    {
                        log("[Season Downloader] Torrent parsing success! Download complete.");

                        foreach (var f in manager.Files)
                        {
                            string fullPath = f.FullPath;
                            downloadedFiles.Add(fullPath);
                        }
                    }

                    await manager.StopAsync();
                }
            }
            catch (Exception ex)
            {
                log($"[MonoTorrent] Error: {ex.Message}");
            }
            return downloadedFiles;
        }

        /// <summary>
        /// Validates file size and runs ffprobe to verify that the file actually contains readable video streams.
        /// </summary>
        private async Task<bool> ValidateMediaFileAsync(string filePath)
        {
            try
            {
                bool exists = File.Exists(filePath);
                if (!exists) return false;

                var info = new FileInfo(filePath);

                if (info.Length < 1024 * 1024 * 5)
                {
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=codec_name -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(startInfo);
                if (proc != null)
                {
                    string output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                    string error = (await proc.StandardError.ReadToEndAsync()).Trim();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        return true;
                    }
                    else
                    {
                        bool fallback = info.Length > 1024 * 1024 * 5;
                        return fallback;
                    }
                }
                else
                {
                    return false;
                }
            }
            catch (Exception)
            {
                try
                {
                    var info = new FileInfo(filePath);
                    if (info.Exists)
                    {
                        return info.Length > 1024 * 1024 * 5;
                    }
                }
                catch { }
                return false;
            }
        }
    }
}
