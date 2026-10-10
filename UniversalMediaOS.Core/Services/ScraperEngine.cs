using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public record ScraperSearchResult(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("url")] string Url);

    public record ScraperStreamResult(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("user_agent")] string? UserAgent,
        [property: JsonPropertyName("cookie")] string? Cookie,
        [property: JsonPropertyName("key_url")] string? KeyUrl,
        [property: JsonPropertyName("referer")] string? Referer,
        [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers,
        [property: JsonPropertyName("requires_webview")] bool RequiresWebView,
        [property: JsonPropertyName("error")] string? Error)
    {
        [JsonPropertyName("subtitles")]
        public MediaSubtitleTrack[]? Subtitles { get; init; }

        [JsonPropertyName("audio_languages")]
        public string[]? AudioLanguages { get; init; }

        // The selected provider frame, not a claim about spoken language.
        [JsonPropertyName("selected_audio")]
        public string? SelectedAudio { get; init; }

        // Advertised video variant whose first media payload passed validation.
        [JsonPropertyName("validated_hls_variant")]
        public string? ValidatedHlsVariant { get; init; }
    }

    /// <summary>
    /// Invokes the stateless scraper.py CLI as a subprocess.
    /// Parses stdout JSON and routes results through the HLS loopback proxy.
    /// </summary>
    public sealed class ScraperEngine : IScraperResolver
    {
        private readonly PythonBootstrapper _python;

        // Total budget across all mirrors — the Python script manages per-mirror 8s timeouts internally
        private const int SearchTimeoutMs = 40_000;
        private const int ExtractDefaultTimeoutMs = 45_000;
        private const int ResolveMinTimeoutMs = 45_000;
        private const int ResolveMaxTimeoutMs = 120_000;

        public bool IsAvailable => _python.IsAvailable;

        public ScraperEngine(PythonBootstrapper python)
        {
            _python = python;
        }

        public Task EnsureReadyAsync(CancellationToken token = default) =>
            _python.EnsureScraperReadyAsync(token);

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Runs: python scraper.py search "{query}"
        /// Returns parsed array of search results or empty array on failure.
        /// </summary>
        public async Task<ScraperSearchResult[]> SearchAsync(
            string query,
            CancellationToken token = default,
            Action<string>? progressLog = null)
        {
            try
            {
                string stdout = await RunScraperAsync(token, SearchTimeoutMs, progressLog, "search", query);
                if (string.IsNullOrWhiteSpace(stdout)) return [];

                var results = JsonSerializer.Deserialize<ScraperSearchResult[]>(stdout);
                return results ?? [];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ScraperEngine] Search failed: {ex.Message}", "WARNING");
                return [];
            }
        }

        /// <summary>
        /// Runs: python scraper.py extract "{episodeUrl}"
        /// Returns parsed stream result, or null if scraper failed or returned an error.
        /// </summary>
        public async Task<ScraperStreamResult?> ExtractAsync(
            string episodeUrl,
            CancellationToken token = default,
            Action<string>? progressLog = null,
            int timeoutMs = ExtractDefaultTimeoutMs,
            string audioPreference = "sub")
        {
            try
            {
                int boundedTimeoutMs = Math.Clamp(timeoutMs, 20_000, 120_000);
                int pythonBudgetSeconds = Math.Max(15, boundedTimeoutMs / 1000 - 5);
                string stdout = await RunScraperAsync(
                    token,
                    boundedTimeoutMs,
                    progressLog,
                    "extract",
                    episodeUrl,
                    NormalizeAudioPreference(audioPreference),
                    pythonBudgetSeconds.ToString());
                if (string.IsNullOrWhiteSpace(stdout)) return null;

                var result = JsonSerializer.Deserialize<ScraperStreamResult>(stdout);
                if (result?.Error != null)
                {
                    AppLogger.Log($"[ScraperEngine] Scraper returned error: {result.Error}", "WARNING");
                    return null;
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ScraperEngine] Extract failed: {ex.Message}", "WARNING");
                return null;
            }
        }

        public async Task<ScraperStreamResult?> ResolveAsync(
            string query,
            string episodeId,
            int maxSiteAttempts,
            CancellationToken token = default,
            Action<string>? progressLog = null,
            string audioPreference = "sub",
            bool preferNative = true,
            IReadOnlyList<string>? titleAliases = null,
            int aniListId = 0,
            int malId = 0,
            IReadOnlyList<string>? titleSynonyms = null)
        {
            try
            {
                int requestedSiteAttempts = maxSiteAttempts < 0 ? 6 : maxSiteAttempts;
                int timeoutMs = ComputeResolveTimeoutMs(requestedSiteAttempts);
                int pythonBudgetSeconds = Math.Max(35, timeoutMs / 1000 - 10);

                string stdout = await RunScraperAsync(
                    token,
                    timeoutMs,
                    progressLog,
                    "resolve",
                    query,
                    episodeId,
                    requestedSiteAttempts.ToString(),
                    NormalizeAudioPreference(audioPreference),
                    pythonBudgetSeconds.ToString(),
                    preferNative ? "native" : "website",
                    JsonSerializer.Serialize(titleAliases ?? []),
                    JsonSerializer.Serialize(new { anilist = Math.Max(0, aniListId), mal = Math.Max(0, malId) }),
                    JsonSerializer.Serialize(titleSynonyms ?? []));

                if (string.IsNullOrWhiteSpace(stdout)) return null;

                var result = JsonSerializer.Deserialize<ScraperStreamResult>(stdout);
                if (result?.Error != null)
                {
                    AppLogger.Log($"[ScraperEngine] Resolve returned error: {result.Error}", "WARNING");
                    return null;
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ScraperEngine] Resolve failed: {ex.Message}", "WARNING");
                return null;
            }
        }

        // ── Subprocess Runner ────────────────────────────────────────────────

        private static string NormalizeAudioPreference(string? audioPreference) =>
            (audioPreference ?? string.Empty).Equals("dub", StringComparison.OrdinalIgnoreCase)
                ? "dub"
                : "sub";

        private static int ComputeResolveTimeoutMs(int maxSiteAttempts)
        {
            // Zero means all indexed sites, but it retains the existing maximum
            // process deadline rather than increasing network/runtime limits.
            int timeoutSiteBudget = maxSiteAttempts == 0
                ? 30
                : Math.Clamp(maxSiteAttempts, 1, 30);
            int timeoutMs = 25_000 + timeoutSiteBudget * 8_000;
            return Math.Clamp(timeoutMs, ResolveMinTimeoutMs, ResolveMaxTimeoutMs);
        }

        private async Task<string> RunScraperAsync(
            CancellationToken externalToken,
            int timeoutMs,
            Action<string>? progressLog,
            string mode,
            params string[] arguments)
        {
            await _python.EnsureScraperReadyAsync(externalToken);

            string? pythonExe = _python.ResolvePythonExecutable();
            if (pythonExe == null)
                throw new PythonPreparationException(_python.Snapshot);

            string scraperPath = _python.GetScraperPath();
            if (!File.Exists(scraperPath))
                throw new FileNotFoundException($"scraper.py not found at: {scraperPath}");

            AppLogger.Log($"[ScraperEngine] Starting mode={mode}, python='{pythonExe}', scraper='{scraperPath}', args={arguments.Length}");

            // Combine caller token with our hard timeout
            using var timeoutCts = new CancellationTokenSource(Math.Max(5_000, timeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                externalToken, timeoutCts.Token);

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
            // Both sides of redirected pipes must use the same encoding.
            // Windows' legacy Python pipe encoding can otherwise corrupt
            // titles/logs or reject non-Latin caption JSON before it arrives.
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.ArgumentList.Add(scraperPath);
            psi.ArgumentList.Add(mode);
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();

            // Read stdout and stderr concurrently to prevent deadlock
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrLines = new List<string>();
            var stderrTask = Task.Run(async () =>
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    string? line = await proc.StandardError.ReadLineAsync();
                    if (line == null)
                    {
                        break;
                    }

                    string normalized = LogSanitizer.RedactSensitiveUrls(line.Trim());
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        continue;
                    }

                    stderrLines.Add(normalized);
                    if (progressLog != null)
                    {
                        progressLog(normalized);
                    }
                    else
                    {
                        AppLogger.Log($"[ScraperEngine] {normalized}");
                    }
                }
            }, linked.Token);

            try
            {
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask)
                        .WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // The process has been killed; bounded drain failures are non-fatal.
                }

                if (externalToken.IsCancellationRequested)
                {
                    AppLogger.Log($"[ScraperEngine] {mode} cancelled by the caller.");
                    throw new OperationCanceledException(externalToken);
                }

                AppLogger.Log($"[ScraperEngine] {mode} timed out.", "WARNING");
                progressLog?.Invoke($"[ScraperEngine] {mode} timed out after {Math.Max(5_000, timeoutMs) / 1000}s; resolution stopped.");
                return string.Empty;
            }

            string stdout = await stdoutTask;
            try
            {
                await stderrTask;
            }
            catch (OperationCanceledException)
            {
            }

            if (stderrLines.Count > 0 && progressLog == null)
                AppLogger.Log($"[ScraperEngine] stderr lines={stderrLines.Count}", "INFO");

            AppLogger.Log($"[ScraperEngine] {mode} exit={proc.ExitCode}, stdout length={stdout.Length}");
            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                AppLogger.Log($"[ScraperEngine] {mode} exited with code {proc.ExitCode} and no JSON response.", "WARNING");
            }
            return stdout.Trim();
        }
    }
}
