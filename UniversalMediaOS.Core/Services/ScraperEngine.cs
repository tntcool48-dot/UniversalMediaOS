using System;
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
        [property: JsonPropertyName("error")] string? Error);

    /// <summary>
    /// Invokes the stateless scraper.py CLI as a subprocess.
    /// Parses stdout JSON and routes results through the HLS loopback proxy.
    /// </summary>
    public sealed class ScraperEngine
    {
        private readonly PythonBootstrapper _python;

        // Total budget across all mirrors — the Python script manages per-mirror 8s timeouts internally
        private const int TotalTimeoutMs = 55_000;

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
            CancellationToken token = default)
        {
            try
            {
                string stdout = await RunScraperAsync(token, "search", query);
                if (string.IsNullOrWhiteSpace(stdout)) return [];

                var results = JsonSerializer.Deserialize<ScraperSearchResult[]>(stdout);
                return results ?? [];
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
            CancellationToken token = default)
        {
            try
            {
                string stdout = await RunScraperAsync(token, "extract", episodeUrl);
                if (string.IsNullOrWhiteSpace(stdout)) return null;

                var result = JsonSerializer.Deserialize<ScraperStreamResult>(stdout);
                if (result?.Error != null)
                {
                    AppLogger.Log($"[ScraperEngine] Scraper returned error: {result.Error}", "WARNING");
                    return null;
                }

                return result;
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
            CancellationToken token = default)
        {
            try
            {
                string stdout = await RunScraperAsync(
                    token,
                    "resolve",
                    query,
                    episodeId,
                    Math.Clamp(maxSiteAttempts, 1, 30).ToString());

                if (string.IsNullOrWhiteSpace(stdout)) return null;

                var result = JsonSerializer.Deserialize<ScraperStreamResult>(stdout);
                if (result?.Error != null)
                {
                    AppLogger.Log($"[ScraperEngine] Resolve returned error: {result.Error}", "WARNING");
                    return null;
                }

                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[ScraperEngine] Resolve failed: {ex.Message}", "WARNING");
                return null;
            }
        }

        // ── Subprocess Runner ────────────────────────────────────────────────

        private async Task<string> RunScraperAsync(
            CancellationToken externalToken,
            string mode,
            params string[] arguments)
        {
            string? pythonExe = _python.ResolvePythonExecutable();
            if (pythonExe == null)
                throw new InvalidOperationException("Python executable not found on this system.");

            string scraperPath = _python.GetScraperPath();
            if (!File.Exists(scraperPath))
                throw new FileNotFoundException($"scraper.py not found at: {scraperPath}");

            AppLogger.Log($"[ScraperEngine] Starting mode={mode}, python='{pythonExe}', scraper='{scraperPath}', args={arguments.Length}");

            // Combine caller token with our hard timeout
            using var timeoutCts = new CancellationTokenSource(TotalTimeoutMs);
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
            var stderrTask = proc.StandardError.ReadToEndAsync(linked.Token);

            try
            {
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                AppLogger.Log($"[ScraperEngine] {mode} timed out or cancelled.", "WARNING");
                return string.Empty;
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (!string.IsNullOrEmpty(stderr))
                AppLogger.Log($"[ScraperEngine] stderr: {stderr.Trim()}", "INFO");

            AppLogger.Log($"[ScraperEngine] {mode} exit={proc.ExitCode}, stdout length={stdout.Length}");
            return stdout.Trim();
        }
    }
}
