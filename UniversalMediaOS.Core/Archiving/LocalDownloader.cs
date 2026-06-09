using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Archiving
{
    public class LocalDownloader
    {
        /// <summary>
        /// Downloads media via ffmpeg and optionally auto-opens the file.
        /// </summary>
        /// <param name="url">Source stream URL.</param>
        /// <param name="outputPath">Local file path to write to.</param>
        /// <param name="onStatusUpdate">Optional callback for human-readable status messages.</param>
        /// <param name="onProgress">Optional callback for download progress (0-100). Reserved for future use.</param>
        public async Task DownloadMediaAsync(
            string url,
            string outputPath,
            Action<string>? onStatusUpdate = null,
            Action<int>? onProgress = null)
        {
            try
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // --- Cache check ---
                onStatusUpdate?.Invoke("Checking cache...");
                onProgress?.Invoke(0);

                if (File.Exists(outputPath))
                {
                    onStatusUpdate?.Invoke("Cache HIT — opening file");
                    onProgress?.Invoke(100);
                    Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
                    return;
                }

                // --- Download ---
                onStatusUpdate?.Invoke("Downloading...");
                onProgress?.Invoke(5);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-i \"{url}\" -c copy \"{outputPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                var proc = Process.Start(startInfo);
                if (proc != null)
                {
                    // Read stderr so the process doesn't block on a full buffer
                    string errorOutput = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode != 0)
                    {
                        string msg = $"ffmpeg exited with code {proc.ExitCode}: {errorOutput}";
                        onStatusUpdate?.Invoke(msg);
                        Console.WriteLine(msg);
                        return;
                    }

                    onProgress?.Invoke(100);

                    if (File.Exists(outputPath))
                    {
                        onStatusUpdate?.Invoke("Download complete — auto-opening");
                        Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
                    }
                    else
                    {
                        onStatusUpdate?.Invoke("Download finished but output file not found.");
                    }
                }
                else
                {
                    onStatusUpdate?.Invoke("Failed to start ffmpeg process.");
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"Download error: {ex.Message}";
                onStatusUpdate?.Invoke(errorMsg);
                Console.WriteLine(errorMsg);
            }
        }
    }
}
