using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Services
{
    public class DependencyBootstrapper
    {
        private readonly string _servicesDir;

        /// <summary>
        /// Path to the detected qBittorrent executable, or null if not found.
        /// </summary>
        public static string? DetectedQBitPath { get; private set; }

        public DependencyBootstrapper(string baseDirectory)
        {
            _servicesDir = Path.Combine(baseDirectory, "services");
            Directory.CreateDirectory(_servicesDir);
        }

        public async Task EnsureDependenciesAsync()
        {
            await EnsureNodeAsync();
            DetectQBittorrent();
            VerifyFFmpeg();
        }

        private async Task EnsureNodeAsync()
        {
            string nodePath = Path.Combine(_servicesDir, "node.exe");
            if (File.Exists(nodePath)) return;

            System.Diagnostics.Debug.WriteLine("Downloading portable Node.js...");
            using var client = new HttpClient();
            var nodeBytes = await client.GetByteArrayAsync("https://nodejs.org/dist/v20.11.1/win-x64/node.exe");
            await File.WriteAllBytesAsync(nodePath, nodeBytes);
        }

        private void DetectQBittorrent()
        {
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
                    System.Diagnostics.Debug.WriteLine($"qBittorrent detected at: {path}");
                    return;
                }
            }

            System.Diagnostics.Debug.WriteLine("qBittorrent not found on this system. P2P tier will rely on WebUI or OS shell handler.");
            DetectedQBitPath = null;
        }

        private void VerifyFFmpeg()
        {
            string[] binaries = { "ffmpeg.exe", "ffprobe.exe" };
            foreach (var bin in binaries)
            {
                if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, bin)))
                {
                    // Fallback to checking PATH
                    var pathEnv = Environment.GetEnvironmentVariable("PATH");
                    bool found = false;
                    if (pathEnv != null)
                    {
                        foreach (var path in pathEnv.Split(Path.PathSeparator))
                        {
                            if (File.Exists(Path.Combine(path.Trim(), bin)))
                            {
                                found = true;
                                break;
                            }
                        }
                    }

                    if (!found)
                    {
                        throw new FileNotFoundException($"Critical dependency missing: {bin} could not be found in AppDirectory or PATH.");
                    }
                }
            }
            System.Diagnostics.Debug.WriteLine("FFmpeg dependencies verified.");
        }
    }
}
