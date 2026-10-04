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

namespace UniversalMediaOS.Core.OtherMedia.Books
{
    public record BookScraperSearchResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("authors")] List<string> Authors,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("size")] string Size,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("year")] int? Year,
        [property: JsonPropertyName("md5")] string Md5)
    {
        [JsonPropertyName("isbns")]
        public IReadOnlyList<string> Isbns { get; init; } = Array.Empty<string>();
        [JsonPropertyName("publisher")]
        public string Publisher { get; init; } = string.Empty;
    }

    public record BookScraperResolveResult(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url")] string Url)
    {
        [JsonPropertyName("format")]
        public string Format { get; init; } = string.Empty;
    }

    public class BookScraperEngine
    {
        private readonly PythonBootstrapper _python;

        public bool IsAvailable => _python.IsBookScraperAvailable;

        public BookScraperEngine(PythonBootstrapper python)
        {
            _python = python ?? throw new ArgumentNullException(nameof(python));
        }

        public virtual async Task<BookScraperSearchResult[]> SearchAsync(
            string query,
            string mirrorUrl,
            CancellationToken token = default)
        {
            try
            {
                string stdout = await RunScraperAsync(token, 25000, "search", query, mirrorUrl);
                if (string.IsNullOrWhiteSpace(stdout)) throw new HttpRequestException("Anna's Archive returned no response.");
                using JsonDocument document = JsonDocument.Parse(stdout);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("status", out JsonElement status) &&
                        status.GetString() == "timed_out") throw new TimeoutException("Anna's Archive search timed out.");
                    throw new HttpRequestException("Anna's Archive search was unavailable.");
                }

                var results = JsonSerializer.Deserialize<BookScraperSearchResult[]>(stdout);
                return results ?? Array.Empty<BookScraperSearchResult>();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Log($"[BookScraperEngine] Search failed: {ex.Message}", "WARNING");
                throw new HttpRequestException("Anna's Archive search was unavailable.", ex);
            }
        }

        public virtual async Task<BookScraperResolveResult[]> ResolveAsync(
            string md5,
            string mirrorUrl,
            CancellationToken token = default)
        {
            try
            {
                string stdout = await RunScraperAsync(token, 25000, "resolve", md5, mirrorUrl);
                if (string.IsNullOrWhiteSpace(stdout)) return Array.Empty<BookScraperResolveResult>();

                var results = JsonSerializer.Deserialize<BookScraperResolveResult[]>(stdout);
                return results ?? Array.Empty<BookScraperResolveResult>();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[BookScraperEngine] Resolve failed: {ex.Message}", "WARNING");
                return Array.Empty<BookScraperResolveResult>();
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

            string scraperPath = _python.GetBookScraperPath();
            if (!File.Exists(scraperPath))
                throw new FileNotFoundException($"book_scraper.py not found at: {scraperPath}");

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
                    AppLogger.Log($"[BookScraper] {line.Trim()}");
                }
            }, linked.Token);

            try
            {
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { await stdoutTask; } catch { }
                try { await stderrTask; } catch { }
                if (externalToken.IsCancellationRequested)
                    throw new OperationCanceledException(externalToken);
                throw new TimeoutException("Book provider exceeded its operation deadline.");
            }

            string stdout = await stdoutTask;
            try { await stderrTask; } catch { }
            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                throw new HttpRequestException("Book provider exited without a response.");

            return stdout.Trim();
        }
    }
}
