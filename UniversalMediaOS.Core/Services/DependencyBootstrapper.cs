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
        public static string DetectedQBitPath { get; private set; }

        public DependencyBootstrapper(string baseDirectory)
        {
            _servicesDir = Path.Combine(baseDirectory, "services");
            Directory.CreateDirectory(_servicesDir);
        }

        public async Task EnsureDependenciesAsync()
        {
            await EnsureNodeAsync();
            DetectQBittorrent();
        }

        private async Task EnsureNodeAsync()
        {
            string nodePath = Path.Combine(_servicesDir, "node.exe");
            if (File.Exists(nodePath)) return;

            Console.WriteLine("Downloading portable Node.js...");
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
                    Console.WriteLine($"qBittorrent detected at: {path}");
                    return;
                }
            }

            Console.WriteLine("qBittorrent not found on this system. P2P tier will rely on WebUI or OS shell handler.");
            DetectedQBitPath = null;
        }
    }
}
