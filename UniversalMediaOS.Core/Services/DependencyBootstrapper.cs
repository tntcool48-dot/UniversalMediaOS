using System;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UniversalMediaOS.Core.Services
{
    public class DependencyBootstrapper
    {
        private readonly string _servicesDir;
        private readonly ILogger<DependencyBootstrapper>? _logger;
        private static readonly SemaphoreSlim _nodeLock = new SemaphoreSlim(1, 1);
        private static readonly HttpClient _httpClient;

        static DependencyBootstrapper()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15)
            };
            _httpClient = new HttpClient(handler);
        }

        /// <summary>
        /// Path to the detected qBittorrent executable, or null if not found.
        /// </summary>
        public string? DetectedQBitPath { get; private set; }
        public bool IsFfmpegAvailable { get; private set; }
        public string FfmpegStatus { get; private set; } = "Not checked.";
        public bool IsUBlockOriginAvailable { get; private set; }
        public string UBlockOriginStatus { get; private set; } = "Not checked.";
        public string ServicesDirectory => _servicesDir;

        public DependencyBootstrapper(string baseDirectory) : this(baseDirectory, null)
        {
        }

        public DependencyBootstrapper(string baseDirectory, ILogger<DependencyBootstrapper>? logger = null)
        {
            // Always use AppData — avoids write permission issues in Program Files / sandboxed dirs
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _servicesDir = Path.Combine(appData, "UniversalMediaOS", "Services");
            Directory.CreateDirectory(_servicesDir);
            _logger = logger;
        }

        public async Task EnsureDependenciesAsync()
        {
            await Task.Yield();

            await RunDependencyStepAsync("qBittorrent detection", () =>
            {
                DetectQBittorrent();
                return Task.CompletedTask;
            });

            await RunDependencyStepAsync("FFmpeg verification", () =>
            {
                IsFfmpegAvailable = VerifyFFmpeg();
                return Task.CompletedTask;
            });

            await RunDependencyStepAsync("uBlock Origin setup", EnsureUBlockOriginAsync);
        }

        private async Task RunDependencyStepAsync(string name, Func<Task> step)
        {
            try
            {
                await step();
            }
            catch (Exception ex)
            {
                LogWarning("{0} failed: {1}", name, ex.Message);
            }
        }

        private async Task EnsureNodeAsync()
        {
            string nodeUrl;
            string nodeBinaryName;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                nodeUrl = "https://nodejs.org/dist/v20.11.1/win-x64/node.exe";
                nodeBinaryName = "node.exe";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                nodeUrl = "https://nodejs.org/dist/v20.11.1/node-v20.11.1-darwin-x64.tar.gz";
                nodeBinaryName = "node";
            }
            else
            {
                nodeUrl = "https://nodejs.org/dist/v20.11.1/node-v20.11.1-linux-x64.tar.gz";
                nodeBinaryName = "node";
            }

            string nodePath = Path.Combine(_servicesDir, nodeBinaryName);

            await _nodeLock.WaitAsync();
            try
            {
                if (File.Exists(nodePath)) return;

                LogInformation("Downloading portable Node.js from {0}...", nodeUrl);

                string tempPath = nodePath + ".tmp";
                bool downloadSuccessful = false;

                try
                {
                    int maxRetry = 3;
                    for (int attempt = 1; attempt <= maxRetry; attempt++)
                    {
                        try
                        {
                            using var response = await _httpClient.GetAsync(nodeUrl, HttpCompletionOption.ResponseHeadersRead);
                            response.EnsureSuccessStatusCode();

                            using var contentStream = await response.Content.ReadAsStreamAsync();
                            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
                            await contentStream.CopyToAsync(fileStream);
                            downloadSuccessful = true;
                            break; // Success
                        }
                        catch (Exception ex)
                        {
                            LogWarning("Attempt {0} of {1} failed to download Node.js: {2}", attempt, maxRetry, ex.Message);
                            if (attempt == maxRetry)
                            {
                                throw;
                            }
                            await Task.Delay(1000);
                        }
                    }

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        try
                        {
                            File.Move(tempPath, nodePath, overwrite: true);
                        }
                        catch (IOException)
                        {
                            if (File.Exists(nodePath))
                            {
                                return; // another thread won the race
                            }
                            throw;
                        }
                    }
                    else
                    {
                        // Non-Windows tar.gz decompression and extraction
                        string extractDir = Path.Combine(_servicesDir, "node_extract_temp");
                        try
                        {
                            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                            Directory.CreateDirectory(extractDir);

                            using (var fs = File.OpenRead(tempPath))
                            using (var gzip = new GZipStream(fs, CompressionMode.Decompress))
                            {
                                TarFile.ExtractToDirectory(gzip, extractDir, overwriteFiles: true);
                            }

                            // Find the nested node binary in the extracted directory
                            var files = Directory.GetFiles(extractDir, "node", SearchOption.AllDirectories);
                            string? foundNode = null;
                            foreach (var f in files)
                            {
                                var parts = f.Split(Path.DirectorySeparatorChar);
                                if (Array.Exists(parts, p => p == "bin"))
                                {
                                    foundNode = f;
                                    break;
                                }
                            }
                            if (foundNode == null && files.Length > 0)
                            {
                                foundNode = files[0];
                            }

                            if (foundNode == null || !File.Exists(foundNode))
                            {
                                throw new FileNotFoundException("Portable Node.js binary not found inside extracted archive.");
                            }

                            File.Move(foundNode, nodePath, overwrite: true);

                            // Set executable permissions (chmod +x)
                            File.SetUnixFileMode(nodePath, 
                                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                        }
                        finally
                        {
                            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
                        }
                    }
                }
                finally
                {
                    if (!downloadSuccessful || !File.Exists(nodePath))
                    {
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    }
                }
            }
            finally
            {
                _nodeLock.Release();
            }
        }

        private void DetectQBittorrent()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                DetectedQBitPath = null;
                LogInformation("qBittorrent detection skipped: UniversalMediaOS is Windows-only.");
                return;
            }

            string[] candidatePaths =
            {
                @"C:\Program Files\qBittorrent\qbittorrent.exe",
                @"C:\Program Files (x86)\qBittorrent\qbittorrent.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "qBittorrent", "qbittorrent.exe")
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    DetectedQBitPath = path;
                    LogInformation("qBittorrent detected at: {0}", path);
                    return;
                }
            }

            LogInformation("qBittorrent not found on this system. P2P tier will rely on WebUI or OS shell handler.");
            DetectedQBitPath = null;
        }

        private bool VerifyFFmpeg()
        {
            string[] binaries = { "ffmpeg.exe", "ffprobe.exe" };
            var missing = new System.Collections.Generic.List<string>();

            foreach (var bin in binaries)
            {
                if (!File.Exists(Path.Combine(_servicesDir, bin)))
                {
                    // Managed path not found — check PATH
                    var pathEnv = Environment.GetEnvironmentVariable("PATH");
                    bool found = false;
                    if (pathEnv != null)
                    {
                        var invalidChars = Path.GetInvalidPathChars();
                        foreach (var path in pathEnv.Split(Path.PathSeparator))
                        {
                            string cleanedPath = path.Trim().Replace("\"", "");
                            var sb = new System.Text.StringBuilder();
                            foreach (char c in cleanedPath)
                            {
                                if (!Array.Exists(invalidChars, invalid => invalid == c))
                                {
                                    sb.Append(c);
                                }
                            }
                            cleanedPath = sb.ToString();

                            try
                            {
                                if (string.IsNullOrWhiteSpace(cleanedPath)) continue;
                                string fullPath = Path.Combine(cleanedPath, bin);
                                if (File.Exists(fullPath))
                                {
                                    found = true;
                                    break;
                                }
                            }
                            catch (ArgumentException)
                            {
                                // Ignore invalid path combination
                            }
                            catch (Exception)
                            {
                                // Ignore other exceptions
                            }
                        }
                    }

                    if (!found)
                    {
                        missing.Add(bin);
                    }
                }
            }

            if (missing.Count > 0)
            {
                FfmpegStatus = $"Missing {string.Join(", ", missing)} in managed services directory or PATH.";
                LogWarning("FFmpeg verification warning: {0}", FfmpegStatus);
                return false;
            }

            FfmpegStatus = "FFmpeg and ffprobe verified.";
            LogInformation("FFmpeg dependencies verified.");
            return true;
        }

        /// <summary>
        /// Downloads the latest uBlock Origin Chromium extension from GitHub releases
        /// and extracts it to %LocalAppData%\UniversalMediaOS\Extensions\ublock-origin\.
        /// Skipped if manifest.json already exists (already installed).
        /// </summary>
        public async Task EnsureUBlockOriginAsync()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string uboDir = Path.Combine(appData, "UniversalMediaOS", "Extensions", "ublock-origin");
            string manifestPath = Path.Combine(uboDir, "manifest.json");

            if (File.Exists(manifestPath))
            {
                IsUBlockOriginAvailable = true;
                UBlockOriginStatus = $"Installed at {uboDir}";
                LogInformation("uBlock Origin already installed at: {0}", uboDir);
                return;
            }

            LogInformation("Downloading uBlock Origin from GitHub...");

            try
            {
                // 1. Fetch latest release metadata
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.github.com/repos/gorhill/uBlock/releases/latest");
                req.Headers.TryAddWithoutValidation("User-Agent", "UniversalMediaOS/1.0");
                using var resp = await _httpClient.SendAsync(req);
                resp.EnsureSuccessStatusCode();

                using var doc = System.Text.Json.JsonDocument.Parse(
                    await resp.Content.ReadAsStringAsync());

                // 2. Find asset ending with .chromium.zip — name is versioned e.g. uBlock0_1.58.0.chromium.zip
                string? downloadUrl = null;
                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".chromium.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }

                if (downloadUrl == null)
                {
                    IsUBlockOriginAvailable = false;
                    UBlockOriginStatus = "Could not find a Chromium extension asset in the latest release.";
                    LogWarning("Could not find .chromium.zip asset in uBlock Origin release. Skipping.");
                    return;
                }

                // 3. Download zip
                Directory.CreateDirectory(uboDir);
                string zipPath = uboDir + ".zip";

                using var zipResp = await _httpClient.GetAsync(downloadUrl);
                zipResp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await zipResp.Content.CopyToAsync(fs);
                fs.Close();

                // 4. Extract — zip root contains manifest.json directly (unpacked extension)
                ZipFile.ExtractToDirectory(zipPath, uboDir, overwriteFiles: true);
                File.Delete(zipPath);

                IsUBlockOriginAvailable = File.Exists(manifestPath);
                UBlockOriginStatus = IsUBlockOriginAvailable
                    ? $"Installed at {uboDir}"
                    : "Download completed, but manifest.json was not found.";
                LogInformation("uBlock Origin installed to: {0}", uboDir);
            }
            catch (Exception ex)
            {
                // Non-fatal — WebView2 will work without it, just without ad-blocking
                IsUBlockOriginAvailable = false;
                UBlockOriginStatus = ex.Message;
                LogWarning("Failed to download uBlock Origin: {0}", ex.Message);
            }
        }

        private void LogInformation(string message, params object?[] args)
        {
            if (_logger != null)
            {
                _logger.LogInformation(message, args);
            }
            else
            {
                string formatted = args.Length > 0 ? string.Format(message, args) : message;
                UniversalMediaOS.Core.Helpers.AppLogger.Log(formatted, "INFO");
            }
        }

        private void LogWarning(string message, params object?[] args)
        {
            if (_logger != null)
            {
                _logger.LogWarning(message, args);
            }
            else
            {
                string formatted = args.Length > 0 ? string.Format(message, args) : message;
                UniversalMediaOS.Core.Helpers.AppLogger.Log(formatted, "WARNING");
            }
        }

        private void LogError(string message, Exception? ex = null, params object?[] args)
        {
            if (_logger != null)
            {
                if (ex != null)
                {
                    _logger.LogError(ex, message, args);
                }
                else
                {
                    _logger.LogError(message, args);
                }
            }
            else
            {
                string formatted = args.Length > 0 ? string.Format(message, args) : message;
                if (ex != null)
                {
                    formatted += $" Exception: {ex.Message}";
                }
                UniversalMediaOS.Core.Helpers.AppLogger.Log(formatted, "ERROR");
            }
        }
    }
}
