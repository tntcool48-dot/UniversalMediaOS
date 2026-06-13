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

        public DependencyBootstrapper(string baseDirectory) : this(baseDirectory, null)
        {
        }

        public DependencyBootstrapper(string baseDirectory, ILogger<DependencyBootstrapper>? logger = null)
        {
            _servicesDir = Path.Combine(baseDirectory, "services");
            Directory.CreateDirectory(_servicesDir);
            _logger = logger;
        }

        public async Task EnsureDependenciesAsync()
        {
            // Yield control to ensure caller doesn't block synchronously
            await Task.Yield();

            try
            {
                await EnsureNodeAsync();

                await Task.Run(() =>
                {
                    DetectQBittorrent();
                    VerifyFFmpeg();
                });
            }
            catch (Exception ex)
            {
                LogError("Exception during EnsureDependenciesAsync", ex);
                throw;
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
            string[] candidatePaths;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                candidatePaths = new[]
                {
                    @"C:\Program Files\qBittorrent\qbittorrent.exe",
                    @"C:\Program Files (x86)\qBittorrent\qbittorrent.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "qBittorrent", "qbittorrent.exe")
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                candidatePaths = new[]
                {
                    "/Applications/qbittorrent.app/Contents/MacOS/qbittorrent"
                };
            }
            else
            {
                candidatePaths = new[]
                {
                    "/usr/bin/qbittorrent",
                    "/usr/local/bin/qbittorrent"
                };
            }

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

        private void VerifyFFmpeg()
        {
            string ffmpegBin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
            string ffprobeBin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
            string[] binaries = { ffmpegBin, ffprobeBin };

            foreach (var bin in binaries)
            {
                if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, bin)))
                {
                    // Fallback to checking PATH
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
                        throw new FileNotFoundException($"Critical dependency missing: {bin} could not be found in AppDirectory or PATH.");
                    }
                }
            }
            LogInformation("FFmpeg dependencies verified.");
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
