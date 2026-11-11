using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;

namespace UniversalMediaOS.Core.OtherMedia
{
    public record AudiovisualScraperSearchResult(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("url")] string Url)
    {
        [JsonPropertyName("evidence")]
        public AudiovisualSourceEvidence? Evidence { get; init; }
    }

    public record AudiovisualScraperStreamResult(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("user_agent")] string? UserAgent,
        [property: JsonPropertyName("cookie")] string? Cookie,
        [property: JsonPropertyName("referer")] string? Referer,
        [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers,
        [property: JsonPropertyName("requires_webview")] bool RequiresWebView,
        [property: JsonPropertyName("error")] string? Error)
    {
        [JsonPropertyName("media_validated")]
        public bool MediaValidated { get; init; }
        [JsonPropertyName("content_type")]
        public string? ContentType { get; init; }
        [JsonPropertyName("audio_languages")]
        public string[]? AudioLanguages { get; init; }
        [JsonPropertyName("validated_hls_variant")]
        public string? ValidatedHlsVariant { get; init; }
        [JsonPropertyName("evidence")]
        public AudiovisualSourceEvidence? Evidence { get; init; }
        [JsonPropertyName("subtitles")]
        public MediaSubtitleTrack[]? Subtitles { get; init; }
    }

    public class AudiovisualScraperEngine : IDisposable
    {
        private readonly PythonBootstrapper _python;
        // Runs still in progress, so closing the app can stop them, and every run folder not yet deleted
        // (a browser that is still exiting can hold its files past a run's own cleanup).
        private readonly ConcurrentDictionary<Process, string> _running = new();
        private readonly ConcurrentDictionary<string, byte> _undeletedProfileRoots = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _lifetime = new();

        public bool IsAvailable => _python.IsAudiovisualScraperAvailable;

        public AudiovisualScraperEngine(PythonBootstrapper python)
        {
            _python = python ?? throw new ArgumentNullException(nameof(python));
        }

        public virtual async Task<AudiovisualScraperSearchResult[]> SearchAsync(
            string query,
            string kind,
            int? year,
            int? season,
            int? episode,
            string mirrorUrl,
            CancellationToken token = default)
        {
            try
            {
                string stdout = await RunScraperAsync(
                    token,
                    25000,
                    "search",
                    query,
                    kind,
                    year?.ToString() ?? string.Empty,
                    season?.ToString() ?? string.Empty,
                    episode?.ToString() ?? string.Empty,
                    mirrorUrl);
                    
                if (string.IsNullOrWhiteSpace(stdout)) return Array.Empty<AudiovisualScraperSearchResult>();

                var results = JsonSerializer.Deserialize<AudiovisualScraperSearchResult[]>(stdout);
                return results ?? Array.Empty<AudiovisualScraperSearchResult>();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AudiovisualScraperEngine] Search failed: {ex.Message}", "WARNING");
                return Array.Empty<AudiovisualScraperSearchResult>();
            }
        }

        public virtual async Task<AudiovisualScraperStreamResult?> ResolveAsync(
            string embedUrl,
            CancellationToken token = default,
            bool requireItemEvidence = false)
        {
            try
            {
                string stdout = await RunScraperAsync(token, 45000,
                    requireItemEvidence ? "resolve-verified" : "resolve", embedUrl);
                if (string.IsNullOrWhiteSpace(stdout)) return null;

                var result = JsonSerializer.Deserialize<AudiovisualScraperStreamResult>(stdout);
                if (result?.Error != null)
                {
                    AppLogger.Log($"[AudiovisualScraperEngine] Scraper returned error: {result.Error}", "WARNING");
                    return null;
                }

                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[AudiovisualScraperEngine] Resolve failed: {ex.Message}", "WARNING");
                return null;
            }
        }

        private async Task<string> RunScraperAsync(
            CancellationToken externalToken,
            int timeoutMs,
            string mode,
            params string[] arguments)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            await _python.EnsureScraperReadyAsync(externalToken);

            string? pythonExe = _python.ResolvePythonExecutable();
            if (pythonExe == null)
                throw new PythonPreparationException(_python.Snapshot);

            string scraperPath = _python.GetAudiovisualScraperPath();
            if (!File.Exists(scraperPath))
                throw new FileNotFoundException($"audiovisual_scraper.py not found at: {scraperPath}");

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, timeoutCts.Token, _lifetime.Token);

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            psi.ArgumentList.Add(scraperPath);
            psi.ArgumentList.Add(mode);
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            // Each run owns its browser profiles. Cancellation and timeouts kill the process tree
            // before the script can remove them, so the engine deletes this run's folder itself.
            string profileRoot = Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "BrowserProfiles",
                "umos_av_run_" + Guid.NewGuid().ToString("N"));
            psi.Environment["UMOS_AV_PROFILE_ROOT"] = profileRoot;
            _undeletedProfileRoots[profileRoot] = 0;

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();
            _running[proc] = profileRoot;
            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
                var stderrTask = Task.Run(async () =>
                {
                    while (!linked.Token.IsCancellationRequested)
                    {
                        string? line = await proc.StandardError.ReadLineAsync(linked.Token);
                        if (line == null) break;
                        AppLogger.Log($"[AudiovisualScraper] {line.Trim()}");
                    }
                }, linked.Token);

                try
                {
                    await proc.WaitForExitAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    if (externalToken.IsCancellationRequested)
                        throw new OperationCanceledException(externalToken);
                    return string.Empty;
                }

                string stdout = await stdoutTask;
                try { await stderrTask; } catch { }

                return stdout.Trim();
            }
            finally
            {
                if (await RemoveProfileRootAsync(proc, profileRoot)) _undeletedProfileRoots.TryRemove(profileRoot, out _);
                else _ = RemoveLaterAsync(profileRoot);
                _running.TryRemove(proc, out _);
            }
        }

        /// <summary>Closing the app stops lookups still running, which would otherwise outlive it and start
        /// browsers on their own, and removes their browser profiles. Only this engine's run folders are touched.</summary>
        public void Dispose()
        {
            if (_lifetime.IsCancellationRequested) return;
            _lifetime.Cancel();
            foreach (Process proc in _running.Keys.ToArray())
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }

            foreach (string profileRoot in _undeletedProfileRoots.Keys.ToArray())
            {
                for (int attempt = 0; !TryDeleteProfileRoot(profileRoot) && attempt < 10; attempt++)
                {
                    Thread.Sleep(200);
                }
            }
            GC.SuppressFinalize(this);
        }

        // A browser that is still exiting holds its files for a while: keep trying, and again when the app closes.
        private async Task RemoveLaterAsync(string profileRoot)
        {
            for (int attempt = 0; attempt < 30 && !_lifetime.IsCancellationRequested; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (TryDeleteProfileRoot(profileRoot))
                {
                    _undeletedProfileRoots.TryRemove(profileRoot, out _);
                    return;
                }
            }
        }

        private static bool TryDeleteProfileRoot(string profileRoot)
        {
            try
            {
                if (Directory.Exists(profileRoot)) Directory.Delete(profileRoot, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static async Task<bool> RemoveProfileRootAsync(Process proc, string profileRoot)
        {
            try
            {
                // A killed browser can hold its profile files until the tree has exited.
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await proc.WaitForExitAsync(exit.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }

            for (int attempt = 0; !TryDeleteProfileRoot(profileRoot); attempt++)
            {
                if (attempt >= 10)
                {
                    AppLogger.Log("[AudiovisualScraperEngine] Browser profile still in use; removing it once released.", "WARNING");
                    return false;
                }
                await Task.Delay(200);
            }
            return true;
        }
    }
}
