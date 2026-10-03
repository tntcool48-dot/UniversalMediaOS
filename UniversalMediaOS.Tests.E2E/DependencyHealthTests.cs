using System.IO;
using System.Net;
using System.Net.Http;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class DependencyHealthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dependency-health-" + Guid.NewGuid().ToString("N"));

    private DependencyBootstrapper Create(IPreparationProcessRunner runner, string? searchPath = null,
        HttpClient? client = null) => new(_root, null, client ?? new HttpClient(new OfflineHandler()),
            TimeSpan.FromMilliseconds(100), _root, runner, () => searchPath);

    private static void AddBinaries(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ffmpeg.exe"), "fixture");
        File.WriteAllText(Path.Combine(directory, "ffprobe.exe"), "fixture");
    }

    [Fact]
    public async Task VersionChecks_PublishReadinessBeforeExtensionSetupFinishes()
    {
        var dep = Create(new VersionRunner());
        AddBinaries(dep.ServicesDirectory);
        bool readyBeforeExtension = false;
        dep.HealthChanged += (_, _) => readyBeforeExtension |= dep.IsFfmpegAvailable && !dep.IsPreparingUBlock &&
            dep.UBlockOriginStatus == "Not checked.";
        await dep.EnsureDependenciesAsync();
        Assert.True(readyBeforeExtension);
        Assert.True(dep.IsFfmpegAvailable);
        Assert.False(dep.IsCheckingFfmpeg);
        Assert.Equal(Path.Combine(dep.ServicesDirectory, "ffprobe.exe"), dep.DetectedFfprobePath);
        Assert.False(dep.IsUBlockOriginAvailable);
    }

    [Theory]
    [InlineData(1, "ffprobe version 7")]
    [InlineData(0, "unrelated executable")]
    public async Task FilePresence_DoesNotMakeBrokenExecutablesReady(int exitCode, string output)
    {
        var dep = Create(new VersionRunner(exitCode, output));
        AddBinaries(dep.ServicesDirectory);
        await dep.VerifyFfmpegAsync();
        Assert.False(dep.IsFfmpegAvailable);
        Assert.Contains("ffprobe failed", dep.FfmpegStatus);
        Assert.Empty(dep.DetectedFfprobePath);
    }

    [Fact]
    public async Task PathLookup_AcceptsQuotedAbsolutePathAndPrefersManagedDirectory()
    {
        string pathDirectory = Path.Combine(_root, "external tools");
        AddBinaries(pathDirectory);
        var dep = Create(new VersionRunner(), "relative;C:\\invalid<path>;\"" + pathDirectory + "\"");
        await dep.VerifyFfmpegAsync();
        Assert.True(dep.IsFfmpegAvailable);
        Assert.Equal(Path.Combine(pathDirectory, "ffmpeg.exe"), dep.DetectedFfmpegPath);
        AddBinaries(dep.ServicesDirectory);
        await dep.VerifyFfmpegAsync();
        Assert.Equal(Path.Combine(dep.ServicesDirectory, "ffmpeg.exe"), dep.DetectedFfmpegPath);
    }

    [Fact]
    public async Task MissingExecutables_ReportMissingWithoutStartingProcess()
    {
        var runner = new VersionRunner();
        var dep = Create(runner, "relative");
        await dep.VerifyFfmpegAsync();
        Assert.False(dep.IsFfmpegAvailable);
        Assert.Contains("ffmpeg missing; ffprobe missing", dep.FfmpegStatus);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task HungVersionCheck_IsCanceledAndNeverReportedReady()
    {
        var dep = Create(new HungRunner());
        Directory.CreateDirectory(dep.ServicesDirectory);
        File.WriteAllText(Path.Combine(dep.ServicesDirectory, "ffmpeg.exe"), "fixture");
        await dep.VerifyFfmpegAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(dep.IsFfmpegAvailable);
        Assert.False(dep.IsCheckingFfmpeg);
        Assert.Contains("timed out", dep.FfmpegStatus);
    }

    [Fact]
    public async Task ExtensionActivation_RetriesTransientRenameFailure()
    {
        string source = Path.Combine(_root, "staging"), destination = Path.Combine(_root, "installed");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "preserved.txt"), "contents");
        int attempts = 0;
        await DependencyBootstrapper.MoveDirectoryWithRetryAsync(source, destination, CancellationToken.None, (s, d) =>
        {
            if (++attempts < 3) throw new UnauthorizedAccessException("Transient fixture lock");
            Directory.Move(s, d);
        });
        Assert.Equal(3, attempts);
        Assert.Equal("contents", File.ReadAllText(Path.Combine(destination, "preserved.txt")));
    }

    [Fact]
    public async Task ExtensionActivation_PermanentFailureStopsAfterThreeAttempts()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => DependencyBootstrapper.MoveDirectoryWithRetryAsync(
            Path.Combine(_root, "stage"), Path.Combine(_root, "target"), CancellationToken.None,
            (_, _) => { attempts++; throw new IOException("Permanent fixture failure"); }));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExtensionActivation_CancellationAndSiblingGuardPreventMutation()
    {
        int attempts = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DependencyBootstrapper.MoveDirectoryWithRetryAsync(
            Path.Combine(_root, "stage"), Path.Combine(_root, "target"), cancellation.Token, (_, _) => attempts++));
        await Assert.ThrowsAsync<ArgumentException>(() => DependencyBootstrapper.MoveDirectoryWithRetryAsync(
            Path.Combine(_root, "stage"), Path.Combine(_root, "elsewhere", "target"), CancellationToken.None, (_, _) => attempts++));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task ConcurrentExtensionRepairs_DoNotOverlapAndRemainRetryableAfterTimeout()
    {
        var handler = new SlowHandler();
        using var client = new HttpClient(handler);
        var dep = Create(new VersionRunner(), client: client);
        await Task.WhenAll(dep.EnsureUBlockOriginAsync(), dep.EnsureUBlockOriginAsync());
        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, handler.MaximumActive);
        Assert.False(dep.IsPreparingUBlock);
        Assert.Contains("Timed out", dep.UBlockOriginStatus);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class VersionRunner(int probeExitCode = 0, string? probeOutput = null) : IPreparationProcessRunner
    {
        public int Calls { get; private set; }
        public Task<PreparationProcessResult> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            Calls++;
            Assert.Equal(new[] { "-version" }, args);
            string name = Path.GetFileNameWithoutExtension(executable);
            return Task.FromResult(name == "ffprobe" ? new PreparationProcessResult(probeExitCode, probeOutput ?? "ffprobe version 7") :
                new PreparationProcessResult(0, "ffmpeg version 7"));
        }
    }

    private sealed class HungRunner : IPreparationProcessRunner
    {
        public async Task<PreparationProcessResult> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        public int Calls, MaximumActive;
        private int _active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            MaximumActive = Math.Max(MaximumActive, Interlocked.Increment(ref _active));
            try { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
