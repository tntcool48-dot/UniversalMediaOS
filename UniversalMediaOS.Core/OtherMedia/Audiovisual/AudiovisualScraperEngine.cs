using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    public class AudiovisualScraperEngine
    {
        private readonly PythonBootstrapper _python;

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
            CancellationToken token = default)
        {
            try
            {
                string stdout = await RunScraperAsync(token, 45000, "resolve", embedUrl);
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
            await _python.EnsureScraperReadyAsync(externalToken);

            string? pythonExe = _python.ResolvePythonExecutable();
            if (pythonExe == null)
                throw new PythonPreparationException(_python.Snapshot);

            string scraperPath = _python.GetAudiovisualScraperPath();
            if (!File.Exists(scraperPath))
                throw new FileNotFoundException($"audiovisual_scraper.py not found at: {scraperPath}");

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, timeoutCts.Token);

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
    }
}
