using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UniversalMediaOS.Core.Services
{
    public class DependencyBootstrapper
    {
        private readonly string _servicesDir;
        private readonly ILogger<DependencyBootstrapper>? _logger;
        private static readonly TimeSpan DefaultUBlockDownloadTimeout = TimeSpan.FromSeconds(20);
        internal const string PinnedUBlockVersion = "1.71.0";
        internal const string PinnedUBlockSha256 = "5313a13fdbe748c23abdde6d24671635a3711a7ab0cf53f420bfa4aecdc36bf6";
        private const long MaxUBlockArchiveBytes = 8 * 1024 * 1024;
        private static readonly Uri PinnedUBlockDownloadUri = new(
            "https://github.com/gorhill/uBlock/releases/download/1.71.0/uBlock0_1.71.0.chromium.zip");
        private static readonly HttpClient SharedHttpClient;
        private readonly HttpClient _httpClient;
        private readonly TimeSpan _uBlockDownloadTimeout;
        private readonly string _localAppData;
        private readonly IPreparationProcessRunner _processRunner;
        private readonly Func<string?> _searchPath;
        private readonly SemaphoreSlim _dependencyLock = new(1, 1);
        private readonly SemaphoreSlim _uBlockLock = new(1, 1);
        public event EventHandler? HealthChanged;
        public bool IsCheckingFfmpeg { get; private set; }
        public bool IsPreparingUBlock { get; private set; }
        public string DetectedFfmpegPath { get; private set; } = string.Empty;
        public string DetectedFfprobePath { get; private set; } = string.Empty;

        static DependencyBootstrapper()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15)
            };
            SharedHttpClient = new HttpClient(handler);
        }

        /// <summary>
        /// Path to the detected qBittorrent executable, or null if not found.
        /// </summary>
        public string? DetectedQBitPath { get; private set; }
        public bool IsFfmpegAvailable { get; private set; }
        public string FfmpegStatus { get; private set; } = "Not checked.";
        public bool IsUBlockOriginAvailable { get; private set; }
        public string UBlockOriginStatus { get; private set; } = "Not checked.";
        public string UBlockOriginDirectory { get; private set; } = string.Empty;
        public string ServicesDirectory => _servicesDir;

        public DependencyBootstrapper(string baseDirectory) : this(baseDirectory, null)
        {
        }

        public DependencyBootstrapper(string baseDirectory, ILogger<DependencyBootstrapper>? logger = null)
            : this(
                baseDirectory,
                logger,
                SharedHttpClient,
                DefaultUBlockDownloadTimeout,
                UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory)
        {
        }

        internal DependencyBootstrapper(
            string baseDirectory,
            ILogger<DependencyBootstrapper>? logger,
            HttpClient httpClient,
            TimeSpan uBlockDownloadTimeout,
            string localAppData,
            IPreparationProcessRunner? processRunner = null,
            Func<string?>? searchPath = null)
        {
            // Always use AppData — avoids write permission issues in Program Files / sandboxed dirs
            _localAppData = localAppData;
            _servicesDir = Path.Combine(_localAppData, "UniversalMediaOS", "Services");
            Directory.CreateDirectory(_servicesDir);
            _logger = logger;
            _httpClient = httpClient;
            _uBlockDownloadTimeout = uBlockDownloadTimeout;
            _processRunner = processRunner ?? new PreparationProcessRunner();
            _searchPath = searchPath ?? (() => Environment.GetEnvironmentVariable("PATH"));
        }

        public async Task EnsureDependenciesAsync()
        {
            await _dependencyLock.WaitAsync().ConfigureAwait(false);
            try { await EnsureDependenciesCoreAsync().ConfigureAwait(false); }
            finally { _dependencyLock.Release(); }
        }

        private async Task EnsureDependenciesCoreAsync()
        {
            await RunDependencyStepAsync("qBittorrent detection", () =>
            {
                DetectQBittorrent();
                return Task.CompletedTask;
            });

            await RunDependencyStepAsync("FFmpeg verification", VerifyFfmpegAsync);
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
            finally { HealthChanged?.Invoke(this, EventArgs.Empty); }
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

        internal async Task VerifyFfmpegAsync()
        {
            IsCheckingFfmpeg = true;
            IsFfmpegAvailable = false;
            DetectedFfmpegPath = DetectedFfprobePath = string.Empty;
            FfmpegStatus = "Checking FFmpeg and ffprobe...";
            var problems = new System.Collections.Generic.List<string>();
            try
            {
                HealthChanged?.Invoke(this, EventArgs.Empty);
                foreach (string name in new[] { "ffmpeg", "ffprobe" })
                {
                    string? executable = FindExecutable(name + ".exe");
                    if (executable == null) { problems.Add(name + " missing"); continue; }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        var result = await _processRunner.RunAsync(executable, ["-version"], timeout.Token).ConfigureAwait(false);
                        if (result.ExitCode != 0 || !result.Output.TrimStart().StartsWith(name + " version", StringComparison.OrdinalIgnoreCase))
                        { problems.Add(name + " failed its version check"); continue; }
                        if (name == "ffmpeg") DetectedFfmpegPath = executable; else DetectedFfprobePath = executable;
                    }
                    catch (OperationCanceledException) { problems.Add(name + " version check timed out"); }
                    catch (Exception ex) { problems.Add(name + " could not start (" + ex.GetType().Name + ")"); }
                }
                IsFfmpegAvailable = problems.Count == 0;
                FfmpegStatus = IsFfmpegAvailable ? "FFmpeg and ffprobe version checks passed." : string.Join("; ", problems) + ".";
            }
            finally
            {
                IsCheckingFfmpeg = false;
                HealthChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private string? FindExecutable(string name)
        {
            foreach (string directory in new[] { _servicesDir }.Concat((_searchPath() ?? "").Split(Path.PathSeparator)))
            {
                string clean = directory.Trim().Trim('"');
                // Ignore malformed/relative PATH entries rather than silently rewriting them.
                if (!Path.IsPathFullyQualified(clean)) continue;
                try
                {
                    string path = Path.Combine(clean, name);
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>
        /// Downloads a reviewed, version-pinned uBlock Origin Chromium extension
        /// and extracts it to %LocalAppData%\UniversalMediaOS\Extensions\ublock-origin\.
        /// The archive is accepted only when its version and SHA-256 match.
        /// </summary>
        public async Task EnsureUBlockOriginAsync()
        {
            await _uBlockLock.WaitAsync().ConfigureAwait(false);
            IsPreparingUBlock = true;
            try
            {
                HealthChanged?.Invoke(this, EventArgs.Empty);
                await EnsureUBlockOriginCoreAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                IsUBlockOriginAvailable = false;
                UBlockOriginDirectory = string.Empty;
                UBlockOriginStatus = "setup: " + ex.Message;
                LogWarning("uBlock Origin setup failed: {0}", ex.Message);
            }
            finally
            {
                IsPreparingUBlock = false;
                _uBlockLock.Release();
                HealthChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task EnsureUBlockOriginCoreAsync()
        {
            string uboDir = Path.Combine(_localAppData, "UniversalMediaOS", "Extensions", "ublock-origin");
            string? installedDir = FindUBlockManifestDirectory(uboDir);
            if (!string.IsNullOrWhiteSpace(installedDir) &&
                HasInstalledUBlockVersion(installedDir, PinnedUBlockVersion))
            {
                IsUBlockOriginAvailable = true;
                UBlockOriginDirectory = installedDir;
                UBlockOriginStatus = "Version " + PinnedUBlockVersion + " installed at " + installedDir;
                LogInformation(
                    "Pinned uBlock Origin {0} already installed at: {1}",
                    PinnedUBlockVersion,
                    installedDir);
                return;
            }

            LogInformation("Downloading pinned uBlock Origin {0} from GitHub...", PinnedUBlockVersion);

            string parentDir = Path.GetDirectoryName(uboDir)!;
            Directory.CreateDirectory(parentDir);
            string operationId = Guid.NewGuid().ToString("N");
            string zipPath = Path.Combine(parentDir, "ublock-origin-" + operationId + ".zip");
            string stagingDir = Path.Combine(parentDir, "ublock-origin-" + operationId + ".staging");
            string backupDir = Path.Combine(parentDir, "ublock-origin-" + operationId + ".backup");
            bool installed = false;
            string stage = "download";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, PinnedUBlockDownloadUri);
                request.Headers.TryAddWithoutValidation("User-Agent", "UniversalMediaOS/1.0");
                using var timeoutCts = new CancellationTokenSource(_uBlockDownloadTimeout);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength &&
                    contentLength > MaxUBlockArchiveBytes)
                {
                    throw new InvalidDataException(
                        "uBlock Origin archive is larger than the " +
                        (MaxUBlockArchiveBytes / (1024 * 1024)) +
                        " MB safety limit.");
                }

                await using (Stream input = await response.Content.ReadAsStreamAsync(timeoutCts.Token))
                await using (var output = new FileStream(
                    zipPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    long totalBytes = 0;
                    while (true)
                    {
                        int read = await input.ReadAsync(buffer, timeoutCts.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        totalBytes += read;
                        if (totalBytes > MaxUBlockArchiveBytes)
                        {
                            throw new InvalidDataException(
                                "uBlock Origin archive exceeded the " +
                                (MaxUBlockArchiveBytes / (1024 * 1024)) +
                                " MB safety limit.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);
                    }
                }

                stage = "integrity verification";
                await using (var archive = File.OpenRead(zipPath))
                {
                    string actualHash = Convert.ToHexString(
                        await SHA256.HashDataAsync(archive, timeoutCts.Token)).ToLowerInvariant();
                    if (!HasExpectedUBlockDigest(actualHash))
                    {
                        throw new InvalidDataException(
                            "uBlock Origin archive integrity check failed (SHA-256 " + actualHash + ").");
                    }
                }

                stage = "extraction";
                timeoutCts.Token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(stagingDir);
                ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: false);
                string? stagedManifestDir = FindUBlockManifestDirectory(stagingDir);
                if (string.IsNullOrWhiteSpace(stagedManifestDir) ||
                    !HasInstalledUBlockVersion(stagedManifestDir, PinnedUBlockVersion))
                {
                    throw new InvalidDataException(
                        "Verified uBlock Origin archive did not contain version " +
                        PinnedUBlockVersion + ".");
                }

                stage = "activation";
                timeoutCts.Token.ThrowIfCancellationRequested();
                if (Directory.Exists(uboDir))
                {
                    await MoveDirectoryWithRetryAsync(uboDir, backupDir, timeoutCts.Token);
                }

                try
                {
                    await MoveDirectoryWithRetryAsync(stagingDir, uboDir, timeoutCts.Token);
                    installed = true;
                    TryDeleteDirectory(backupDir);
                }
                catch
                {
                    if (!Directory.Exists(uboDir) && Directory.Exists(backupDir))
                    {
                        await MoveDirectoryWithRetryAsync(backupDir, uboDir, CancellationToken.None);
                    }
                    throw;
                }

                installedDir = FindUBlockManifestDirectory(uboDir);
                IsUBlockOriginAvailable = !string.IsNullOrWhiteSpace(installedDir);
                UBlockOriginDirectory = installedDir ?? string.Empty;
                UBlockOriginStatus = IsUBlockOriginAvailable
                    ? "Version " + PinnedUBlockVersion + " installed at " + installedDir
                    : "Verified download completed, but manifest.json was not found.";
                LogInformation(
                    "Verified uBlock Origin {0} installed to: {1}",
                    PinnedUBlockVersion,
                    uboDir);
            }
            catch (OperationCanceledException)
            {
                IsUBlockOriginAvailable = false;
                UBlockOriginStatus =
                    "Timed out after " +
                    _uBlockDownloadTimeout.TotalSeconds.ToString("0.##") +
                    " seconds during uBlock Origin " + stage + ".";
                LogWarning(
                    "uBlock Origin download timed out after {0:0.##} seconds. Continuing without the extension.",
                    _uBlockDownloadTimeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                IsUBlockOriginAvailable = false;
                UBlockOriginStatus = stage + ": " + ex.Message;
                LogWarning("Failed to download uBlock Origin: {0}", ex.Message);
            }
            finally
            {
                TryDeleteFile(zipPath);
                TryDeleteDirectory(stagingDir);
                if (!installed && !Directory.Exists(uboDir) && Directory.Exists(backupDir))
                {
                    try { Directory.Move(backupDir, uboDir); } catch { }
                }
                if (installed)
                {
                    TryDeleteDirectory(backupDir);
                }
            }
        }

        internal static async Task MoveDirectoryWithRetryAsync(string source, string destination,
            CancellationToken token, Action<string, string>? move = null)
        {
            source = Path.GetFullPath(source);
            destination = Path.GetFullPath(destination);
            if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Extension activation must rename sibling directories.");
            move ??= Directory.Move;
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try { move(source, destination); return; }
                catch (Exception ex) when (attempt < 2 && (ex is IOException or UnauthorizedAccessException))
                { await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), token).ConfigureAwait(false); }
            }
        }

        internal static bool HasExpectedUBlockDigest(string actualSha256) =>
            !string.IsNullOrWhiteSpace(actualSha256) &&
            CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actualSha256.Trim().ToLowerInvariant()),
                System.Text.Encoding.ASCII.GetBytes(PinnedUBlockSha256));

        private static bool HasInstalledUBlockVersion(string directory, string expectedVersion)
        {
            try
            {
                using var manifest = System.Text.Json.JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(directory, "manifest.json")));
                return manifest.RootElement.TryGetProperty("version", out var version) &&
                       string.Equals(version.GetString(), expectedVersion, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }

        public string GetUBlockOriginPath()
        {
            string uboDir = Path.Combine(_localAppData, "UniversalMediaOS", "Extensions", "ublock-origin");
            return FindUBlockManifestDirectory(uboDir) ?? uboDir;
        }

        private static string? FindUBlockManifestDirectory(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return null;
            }

            string directManifest = Path.Combine(root, "manifest.json");
            if (File.Exists(directManifest))
            {
                return root;
            }

            string chromiumPath = Path.Combine(root, "uBlock0.chromium");
            if (File.Exists(Path.Combine(chromiumPath, "manifest.json")))
            {
                return chromiumPath;
            }

            try
            {
                return Directory
                    .EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .OrderBy(path => path!.Split(Path.DirectorySeparatorChar).Length)
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
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

    }
}
