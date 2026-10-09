using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class NativeTemporaryDownloadBoundaryTests
{
    private const long Reserve = 1024L * 1024 * 1024;

    [Fact]
    public async Task TemporaryEpisodeFileSharingFailureStopsProviderRotationAndCleansOnRestart()
    {
        await using var fixture = await LocalSeeder.StartAsync(128 * 1024);
        ClientEngine? engine = null;
        TorrentManager? manager = null;
        FileStream? blocker = null;
        var service = fixture.Service(_ => long.MaxValue, createEngine: settings => engine = new ClientEngine(settings));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<TemporaryEpisodeWatchResult>? transfer = null;
        try
        {
            transfer = service.DownloadAsync(1001, LocalSeeder.Title, [], 1, "Sub", message =>
            {
                fixture.Logs.Add(message);
                if (!message.StartsWith("Downloading only episode")) return;
                manager = Assert.Single(engine!.Torrents);
                string partial = manager.Files.Single(file => file.Path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)).FullPath;
                Assert.StartsWith(fixture.Temporary + Path.DirectorySeparatorChar, partial, StringComparison.OrdinalIgnoreCase);
                Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
                File.WriteAllBytes(partial, []);
                blocker = File.Open(partial, FileMode.Open, FileAccess.Read, FileShare.Read);
            }, cancellation.Token);
            await Eventually(() => transfer.IsCompleted || manager?.State == TorrentState.Error);
            await Task.WhenAny(transfer, Task.Delay(4500));
            if (!transfer.IsCompleted) cancellation.Cancel();
            var failure = await Record.ExceptionAsync(async () =>
            {
                var unexpected = await transfer.WaitAsync(TimeSpan.FromSeconds(10));
                unexpected.Lease.Dispose();
            });
            Assert.IsType<NativeTorrentStorageException>(failure);
            Assert.Contains("write", failure!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(fixture.Logs, message => message.StartsWith("Checking torrent files:"));
            Assert.True(engine!.Disposed);
            Assert.Empty(engine.Torrents);
        }
        finally
        {
            blocker?.Dispose();
            cancellation.Cancel();
            if (transfer != null) try { await transfer.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            _ = fixture.Service(_ => long.MaxValue); // Production restart cleanup after the OS blocker releases.
        }
        fixture.AssertRetainedAndClean();
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public async Task PermanentSeasonFileSharingFailureEndsPromptlyAndRetainsRetryablePieces(string operation)
    {
        await using var fixture = await LocalSeeder.StartAsync(128 * 1024);
        string? previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, fixture.Root);
        try
        {
            string downloads = Path.Combine(fixture.Root, "library");
            var executor = new SeasonDownloadJobExecutor(fixture.SeasonConfig(downloads));
            using var queue = new DownloadQueueService(Path.Combine(fixture.Root, "queue.json"), executor);
            var job = queue.Enqueue(LocalSeeder.Title, "Sub");
            await Eventually(() => ActiveManager(executor) is { State: TorrentState.Downloading } active &&
                active.Monitor.DataBytesReceived > 0 && active.Progress > 0);
            var first = ActiveManager(executor)!;
            string partial = first.Files.Single(file => file.Path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)).FullPath;
            await queue.PauseAsync(job.Id);
            await Eventually(() => job.Status == DownloadJobStatus.Paused && ActiveManager(executor) == null);
            double previous = first.Progress;
            byte[] partialHash = SHA256.HashData(File.ReadAllBytes(partial));
            string metadata = Assert.Single(Directory.GetFiles(Path.Combine(AppDataPaths.LocalBaseDirectory,
                "UniversalMediaOS", "TorrentCache", "metadata"), "*.torrent"));
            byte[] metadataHash = SHA256.HashData(File.ReadAllBytes(metadata));
            using (var blocker = File.Open(partial, FileMode.Open, FileAccess.Read,
                operation == "read" ? FileShare.None : FileShare.Read))
            {
                Assert.True(queue.Resume(job.Id));
                await Eventually(() => job.Status == DownloadJobStatus.Failed && ActiveManager(executor) == null,
                    () => $"Blocked {operation}: {job.Status}; {NativeState(ActiveManager(executor))}; error={ActiveManager(executor)?.Error?.Reason}; {job.StatusMessage}");
                Assert.Contains(operation, job.StatusMessage, StringComparison.OrdinalIgnoreCase);
                Assert.True(job.CanRetry);
            }
            Assert.Equal(partialHash, SHA256.HashData(File.ReadAllBytes(partial)));
            Assert.Equal(metadataHash, SHA256.HashData(File.ReadAllBytes(metadata)));
            Assert.True(queue.Retry(job.Id));
            await Eventually(() => ActiveManager(executor) is { State: TorrentState.Downloading } resumed &&
                resumed.Monitor.DataBytesReceived > 0 && resumed.Progress > previous + 0.000001,
                () => $"Unlocked retry: {job.Status}; {NativeState(ActiveManager(executor))}; {job.StatusMessage}");
            var final = ActiveManager(executor)!;
            await queue.PauseAsync(job.Id);
            await Eventually(() => job.Status == DownloadJobStatus.Paused && ActiveManager(executor) == null);
            Assert.Equal(final.Progress * 0.95, job.Progress, precision: 8);
            Assert.Equal("permanent retained sentinel", File.ReadAllText(Path.Combine(fixture.Root, "permanent.mkv")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(AppDataPaths.LocalBaseDirectory,
                "UniversalMediaOS", "TorrentCache", "fastresume"), "*.fresume"));
        }
        finally { Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previousRoot); }
    }

    [Fact]
    public async Task PermanentSeasonResumeReusesAllocatedPartialAndPreservesItsMetadataCache()
    {
        await using var fixture = await LocalSeeder.StartAsync(128 * 1024);
        string? previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, fixture.Root);
        try
        {
            bool resumed = false;
            using var downloader = new SeasonDownloader(fixture.SeasonConfig(Path.Combine(fixture.Root, "library")),
                _ => resumed ? Reserve + 4096 : long.MaxValue);
            double previous = 0;
            string? metadataPath = null;
            byte[]? metadataHash = null;
            (string Path, long Length)[] allocatedFiles = [];
            for (int cycle = 0; cycle < 2; cycle++)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var transfer = downloader.DownloadSeasonAsync(LocalSeeder.Title, fixture.Logs.Add, token: cancellation.Token);
                try
                {
                    await Eventually(() => transfer.IsCompleted || ActiveManager(downloader) is { } active &&
                        active.Monitor.DataBytesReceived > 0 && active.Progress > previous + 0.000001,
                        () => $"Resume cycle {cycle}: {NativeState(ActiveManager(downloader))}; {fixture.SeedState}; {string.Join(" | ", fixture.Logs.TakeLast(6))}");
                    Assert.False(transfer.IsCompleted, $"Resume refused its existing allocation. {string.Join(" | ", fixture.Logs)}");
                    var active = ActiveManager(downloader)!;
                    allocatedFiles = active.Files.Select(file => (file.FullPath, file.Length)).ToArray();
                    await downloader.PauseActiveTransferAsync();
                    previous = active.Progress;
                }
                finally
                {
                    cancellation.Cancel();
                    try { await transfer.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (OperationCanceledException) { }
                    catch (InsufficientDownloadSpaceException) when (transfer.IsFaulted) { }
                }
                string cache = Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "TorrentCache");
                Assert.NotEmpty(Directory.GetFiles(cache, "*.fresume", SearchOption.AllDirectories));
                if (cycle == 0)
                {
                    string ownedLibrary = Path.GetFullPath(Path.Combine(fixture.Root, "library")) + Path.DirectorySeparatorChar;
                    foreach (var file in allocatedFiles.Where(file => File.Exists(file.Path)))
                    {
                        Assert.StartsWith(ownedLibrary, Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase);
                        using var allocation = File.Open(file.Path, FileMode.Open, FileAccess.Write, FileShare.Read);
                        allocation.SetLength(file.Length);
                    }
                    metadataPath = Assert.Single(Directory.GetFiles(Path.Combine(cache, "metadata"), "*.torrent"));
                    metadataHash = SHA256.HashData(File.ReadAllBytes(metadataPath));
                }
                else Assert.Equal(metadataHash!, SHA256.HashData(File.ReadAllBytes(metadataPath!)));
                resumed = true;
            }
            Assert.Equal("permanent retained sentinel", File.ReadAllText(Path.Combine(fixture.Root, "permanent.mkv")));
        }
        finally { Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previousRoot); }
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("metadata")]
    [InlineData("falling")]
    public async Task PermanentSeasonRejectsInsufficientSpaceBeforePayloadAndRetainsInterruptedPieces(string mode)
    {
        await using var fixture = await LocalSeeder.StartAsync(32 * 1024);
        string? previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, fixture.Root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<bool>? transfer = null;
        try
        {
            string downloads = Path.Combine(fixture.Root, "library");
            bool reserveFallen = false;
            using var downloader = new SeasonDownloader(fixture.SeasonConfig(downloads), _ => mode switch
            {
                "initial" => 0,
                "metadata" => Reserve + fixture.VideoBytes / 2,
                _ => reserveFallen ? 0 : long.MaxValue
            });
            transfer = downloader.DownloadSeasonAsync(LocalSeeder.Title, fixture.Logs.Add, token: cancellation.Token);
            await Eventually(() => transfer.IsCompleted || ActiveManager(downloader)?.Monitor.DataBytesReceived > 0);
            if (mode == "falling")
            {
                reserveFallen = true;
                await Task.WhenAny(transfer, Task.Delay(4500));
            }
            // Abort only the old implementation's unguarded real transfer.
            if (!transfer.IsCompleted) cancellation.Cancel();
            var failure = await Record.ExceptionAsync(() => transfer.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(failure is InsufficientDownloadSpaceException,
                $"Expected space failure, got {failure?.GetType().Name ?? "successful result"}. Logs: {string.Join(" | ", fixture.Logs)}");
            Assert.Contains("space", failure!.Message, StringComparison.OrdinalIgnoreCase);
            if (mode == "falling")
            {
                Assert.True(fixture.UploadedBytes > 0);
                Assert.NotEmpty(Directory.GetFiles(downloads, "*.!mt", SearchOption.AllDirectories));
                string cache = Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "TorrentCache");
                Assert.NotEmpty(Directory.GetFiles(cache, "*.fresume", SearchOption.AllDirectories));
            }
            else Assert.Equal(0, fixture.UploadedBytes);
            Assert.Equal("permanent retained sentinel", File.ReadAllText(Path.Combine(fixture.Root, "permanent.mkv")));
        }
        finally
        {
            cancellation.Cancel();
            if (transfer != null) try { await transfer.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previousRoot);
        }
    }

    [Theory]
    [InlineData("before-metadata")]
    [InlineData("payload-cancel")]
    [InlineData("capacity")]
    [InlineData("completed-owners")]
    public async Task TemporaryEpisodeClosesNativeSessionsAcrossFailureAndFinalOwnerRelease(string mode)
    {
        await using var fixture = await LocalSeeder.StartAsync(mode == "completed-owners" ? 0 : 32 * 1024);
        var sessions = new List<(ClientEngine Engine, object Dht)>();
        var service = fixture.Service(_ => mode == "capacity" ? 0 : long.MaxValue, createEngine: settings =>
        {
            var engine = new ClientEngine(settings);
            sessions.Add((engine, typeof(ClientEngine).GetProperty("DhtEngine", PrivateInstance)!.GetValue(engine)!));
            return engine;
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var transfer = service.DownloadAsync(1001, LocalSeeder.Title, [], 1, "Sub", message =>
        {
            fixture.Logs.Add(message);
            if (mode == "before-metadata" && message.StartsWith("Checking torrent files:")) cancellation.Cancel();
        }, cancellation.Token);
        if (mode == "payload-cancel")
        {
            await Eventually(() => fixture.UploadedBytes > 0);
            cancellation.Cancel();
        }
        if (mode is "before-metadata" or "payload-cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.WaitAsync(TimeSpan.FromSeconds(10)));
        else if (mode == "capacity")
            await Assert.ThrowsAsync<InsufficientDownloadSpaceException>(() => transfer.WaitAsync(TimeSpan.FromSeconds(10)));
        else
        {
            var first = await transfer.WaitAsync(TimeSpan.FromSeconds(25));
            var second = await service.DownloadAsync(1001, LocalSeeder.Title, [], 1, "Sub", fixture.Logs.Add, cancellation.Token);
            Assert.Equal(first.FilePath, second.FilePath);
            Assert.Equal(fixture.VideoBytes, new FileInfo(first.FilePath).Length);
            first.Lease.Dispose();
            Assert.True(File.Exists(second.FilePath));
            second.Lease.Dispose();
            Assert.False(File.Exists(first.FilePath));
        }
        fixture.AssertRetainedAndClean();
        Assert.Single(sessions);
        foreach (var session in sessions)
        {
            Assert.True(session.Engine.Disposed);
            Assert.Empty(session.Engine.Torrents);
            foreach (string eventName in new[] { "PeersFound", "StateChanged" })
            {
                var handlers = (Delegate?)session.Dht.GetType().GetField(eventName, PrivateInstance)!.GetValue(session.Dht);
                Assert.DoesNotContain(handlers?.GetInvocationList() ?? [], handler => ReferenceEquals(handler.Target, session.Engine));
            }
        }
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("session-cleanup")]
    public async Task RapidSeasonPauseRetainsActualProgressAndReleasesTheClosedSession(string check)
    {
        await using var fixture = await LocalSeeder.StartAsync(128 * 1024);
        string? previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, fixture.Root);
        try
        {
            string downloads = Path.Combine(fixture.Root, "library");
            string queuePath = Path.Combine(fixture.Root, "queue.json");
            var config = new DomainHotSwapper(Path.Combine(fixture.Root, "config.json"));
            config.SetSetting("DownloadDirectory", downloads);
            config.SetSetting("NyaaUrl", fixture.Origin + "rss?q=");
            config.SetSetting("AnimeToshoUrl", fixture.Origin + "rss?q=");
            config.SetSetting("QBitHost", "127.0.0.1");
            config.SetSetting("QBitPort", new Uri(fixture.Origin).Port.ToString());
            var executor = new SeasonDownloadJobExecutor(config);
            Guid jobId;
            double finalProgress;
            using (var queue = new DownloadQueueService(queuePath, executor))
            {
                var job = queue.Enqueue(LocalSeeder.Title, "Sub");
                jobId = job.Id;
                double previousProgress = 0;
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    if (cycle > 0) Assert.True(queue.Resume(job.Id));
                    await Eventually(() => job.Status is DownloadJobStatus.Failed or DownloadJobStatus.Completed ||
                        ActiveManager(executor) is { HasMetadata: true, State: TorrentState.Downloading },
                        () => $"Native start cycle {cycle}: {NativeState(ActiveManager(executor))}; {fixture.SeedState}; status={job.Status}: {job.StatusMessage}; {string.Join(" | ", fixture.Logs.TakeLast(6))}");
                    Assert.True(ActiveManager(executor) is { HasMetadata: true, State: TorrentState.Downloading },
                        $"Expected incomplete native transfer, got {job.Status}: {job.StatusMessage}");
                    var manager = ActiveManager(executor)!;
                    var engine = manager.Engine!;
                    object oldDht = typeof(ClientEngine).GetProperty("DhtEngine", PrivateInstance)!.GetValue(engine)!;
                    await Eventually(() => manager.Monitor.DataBytesReceived > 0 && manager.Progress > previousProgress + 0.000001,
                        () => $"Native cycle {cycle}: previous={previousProgress}; {NativeState(manager)}; {fixture.SeedState}; status={job.Status}: {job.StatusMessage}");
                    Assert.True(await queue.PauseAsync(job.Id));
                    await Eventually(() => job.Status == DownloadJobStatus.Paused && ActiveManager(executor) == null);
                    Assert.Equal(TorrentState.Stopped, manager.State);
                    Assert.True(manager.Progress > previousProgress && manager.Progress < 100,
                        "Pause must retain new verified pieces without completing the fixture.");
                    Assert.NotEmpty(Directory.GetFiles(downloads, "*.!mt", SearchOption.AllDirectories));
                    Assert.NotEmpty(Directory.GetFiles(engine.Settings.FastResumeCacheDirectory));
                    Assert.NotEmpty(Directory.GetFiles(engine.Settings.MetadataCacheDirectory));
                    Assert.Equal("permanent retained sentinel", File.ReadAllText(Path.Combine(fixture.Root, "permanent.mkv")));

                    if (check == "progress") Assert.Equal(manager.Progress * 0.95, job.Progress, precision: 8);
                    else
                    {
                        Assert.Empty(engine.Torrents);
                        foreach (string eventName in new[] { "PeersFound", "StateChanged" })
                        {
                            var handlers = (Delegate?)oldDht.GetType().GetField(eventName, PrivateInstance)!.GetValue(oldDht);
                            Assert.DoesNotContain(handlers?.GetInvocationList() ?? [], handler => ReferenceEquals(handler.Target, engine));
                        }
                        Assert.True(engine.Disposed);
                    }
                    previousProgress = manager.Progress;
                }
                finalProgress = job.Progress;
                var preserved = Directory.GetFiles(downloads, "*", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "TorrentCache"), "*", SearchOption.AllDirectories))
                    .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
                await Task.Delay(200);
                Assert.All(preserved, pair => Assert.Equal(pair.Value, SHA256.HashData(File.ReadAllBytes(pair.Key))));
            }
            var persisted = JsonSerializer.Deserialize<DownloadQueueJob[]>(await File.ReadAllTextAsync(queuePath))!;
            var restored = Assert.Single(persisted);
            Assert.Equal(jobId, restored.Id);
            Assert.Equal(DownloadJobStatus.Paused, restored.Status);
            Assert.Equal(finalProgress, restored.Progress);
        }
        finally { Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previousRoot); }
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static TorrentManager? ActiveManager(SeasonDownloadJobExecutor executor)
    {
        var downloader = (SeasonDownloader?)typeof(SeasonDownloadJobExecutor).GetField("_activeDownloader", PrivateInstance)!.GetValue(executor);
        return downloader == null ? null : (TorrentManager?)typeof(SeasonDownloader).GetField("_activeMonoTorrentManager", PrivateInstance)!.GetValue(downloader);
    }

    private static TorrentManager? ActiveManager(SeasonDownloader downloader) =>
        (TorrentManager?)typeof(SeasonDownloader).GetField("_activeMonoTorrentManager", PrivateInstance)!.GetValue(downloader);

    [Theory]
    [InlineData("initial")]
    [InlineData("sidecar")]
    [InlineData("falling")]
    public async Task CapacityFailureStopsNativeTransferWithoutProviderRotationAndKeepsPermanentData(string mode)
    {
        await using var fixture = await LocalSeeder.StartAsync(128 * 1024);
        var service = fixture.Service(_ =>
        {
            return mode switch
            {
                "initial" => 0,
                "sidecar" => Reserve + fixture.VideoBytes,
                _ => fixture.UploadedBytes == 0 ? long.MaxValue : 0
            };
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            var result = await service.DownloadAsync(1001, LocalSeeder.Title, [], 1, "Sub",
                fixture.Logs.Add, deadline.Token);
            result.Lease.Dispose(); // An old implementation's unexpected success must still release its file.
        });
        Assert.IsType<InsufficientDownloadSpaceException>(failure);
        Assert.Single(fixture.Logs, line => line.StartsWith("Checking torrent files:"));
        if (mode == "falling") Assert.True(fixture.UploadedBytes > 0, "The reserve must fall after real payload bytes arrive.");
        else Assert.Equal(0, fixture.UploadedBytes);
        fixture.AssertRetainedAndClean();
    }

    [Fact]
    public async Task CancellingAfterNativePayloadArrivesDeletesOnlyTheOwnedTemporaryJob()
    {
        await using var fixture = await LocalSeeder.StartAsync(64 * 1024);
        var service = fixture.Service(_ => long.MaxValue);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var transfer = service.DownloadAsync(1001, LocalSeeder.Title, [], 1, "Sub", fixture.Logs.Add, cancellation.Token);
        await Eventually(() => fixture.UploadedBytes > 0);
        Assert.Single(Directory.GetDirectories(fixture.Temporary));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.WaitAsync(TimeSpan.FromSeconds(7)));
        fixture.AssertRetainedAndClean();
    }

    [Theory]
    [InlineData("episode")]
    [InlineData("audio")]
    [InlineData("title")]
    public async Task ChangedAnimeSelectionCannotPublishTheOldTemporaryDownload(string change)
    {
        await using var fixture = await LocalSeeder.StartAsync(1024 * 1024);
        var searching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.Service(_ => long.MaxValue, async (_, _, _) =>
        {
            searching.TrySetResult();
            await release.Task; // Deliberately ignores cancellation, as a late provider may do.
            return fixture.Candidates();
        });
        var config = new DomainHotSwapper(Path.Combine(fixture.Root, "config.json"));
        config.SetSetting(DubAvailabilityService.ProviderConfigKey, "[]");
        using var oauth = new MalOAuthService(config);
        using var vm = new AnimeDetailsViewModel(config, null!, null!, service, null!, new DubAvailabilityService(config), oauth);
        vm.Media = new MediaResult { Id = 1001, OfficialTitle = LocalSeeder.Title, AvailableSubEpisodes = 2, TotalEpisodes = 2 };
        object recipient = new();
        int opened = 0;
        WeakReferenceMessenger.Default.Register<PlayMediaMessage>(recipient, (_, message) =>
        {
            if (!message.Title.StartsWith(LocalSeeder.Title)) return;
            opened++;
            message.TemporaryWatchLease?.Dispose();
        });
        Task? download = null;
        try
        {
            download = vm.WatchViaDownloadCommand.ExecuteAsync(null);
            await searching.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsTemporaryWatchDownloading);
            switch (change)
            {
                case "episode": vm.SelectedEpisode = "2"; break;
                case "audio": vm.SelectedAudioMode = "Dub"; break;
                default: vm.Media = new MediaResult { Id = 1002, OfficialTitle = "Other selection", AvailableSubEpisodes = 2, TotalEpisodes = 2 }; break;
            }
            release.SetResult();
            await download.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, opened);
            Assert.False(vm.IsTemporaryWatchDownloading);
            Assert.Contains(vm.ScraperActivityItems, item => item.Message == "Temporary episode download cancelled.");
            fixture.AssertRetainedAndClean();
        }
        finally
        {
            vm.CancelActiveWork();
            release.TrySetResult();
            if (download != null) await download.WaitAsync(TimeSpan.FromSeconds(7));
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    private static string NativeState(TorrentManager? manager) => manager == null ? "manager absent" :
        $"state={manager.State}, progress={manager.Progress}, received={manager.Monitor.DataBytesReceived}, trackerTiers={manager.TrackerManager.Tiers.Count}, seeds={manager.Peers.Seeds}, leechs={manager.Peers.Leechs}, available={manager.Peers.Available}";

    private static async Task Eventually(Func<bool> condition, Func<string>? state = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try { while (!condition()) await Task.Delay(50, deadline.Token); }
        catch (OperationCanceledException) when (state != null) { throw new TimeoutException(state()); }
    }

    private sealed class LocalSeeder : IAsyncDisposable
    {
        public const string Title = "Queue Space Fixture";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.NativeTempBoundary", Guid.NewGuid().ToString("N"));
        public string Temporary => Path.Combine(Root, "temporary");
        public long VideoBytes { get; private set; }
        public long UploadedBytes => _manager!.Monitor.DataBytesSent;
        public string SeedState => $"seed state={_manager?.State}, running={_engine?.IsRunning}, disposed={_engine?.Disposed}, sent={UploadedBytes}, peers={_manager?.Peers.Leechs}";
        public List<string> Logs { get; } = [];
        public string Origin { get; private set; } = "";
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HttpListener _listener = new();
        private ClientEngine? _engine;
        private TorrentManager? _manager;
        private Task? _server;
        private string _magnet = "";
        private string _hash = "";

        public static async Task<LocalSeeder> StartAsync(int rate, int seconds = 20)
        {
            var fixture = new LocalSeeder();
            try
            {
                Directory.CreateDirectory(fixture.Root);
                string source = Path.Combine(fixture.Root, "source");
                Directory.CreateDirectory(source);
                string video = Path.Combine(source, Title + " - S01E01.mkv");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg",
                    ["-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25",
                     "-f", "lavfi", "-i", "sine=frequency=440", "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "mpeg4", "-q:v", "3", "-c:a", "aac", "-y", video], deadline.Token);
                Assert.Equal(0, encoded.ExitCode);
                // These checks exercise real pieces, pause/resume and ownership,
                // not long-media throughput. Keep a multi-megabyte file without
                // encoding and rehashing a 90-second sample for each small boundary.
                fixture.VideoBytes = new FileInfo(video).Length;
                Assert.True(fixture.VideoBytes > 5 * 1024 * 1024,
                    $"The real-media fixture must exceed 5 MiB; {seconds}s produced {fixture.VideoBytes} bytes.");
                Console.WriteLine($"Native boundary fixture: {seconds}s, {fixture.VideoBytes} real video bytes, seed rate {rate} bytes/s.");
                await File.WriteAllTextAsync(Path.Combine(source, Title + " - S01E01.en.srt"), "1\n00:00:00,000 --> 00:00:30,000\nGenerated cue\n", deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, "permanent.mkv"), "permanent retained sentinel", deadline.Token);
                int httpPort = FreePort(), peerPort = FreePort();
                fixture.Origin = $"http://127.0.0.1:{httpPort}/";
                string tracker = $"http://127.0.0.1:{httpPort}/announce";
                fixture._listener.Prefixes.Add($"http://127.0.0.1:{httpPort}/");
                fixture._listener.Start();
                fixture._server = fixture.ServeAsync(peerPort);
                var creator = new TorrentCreator { Private = true, PieceLength = 16 * 1024 };
                creator.Announces.Add([tracker]);
                string torrentPath = Path.Combine(fixture.Root, "fixture.torrent");
                await creator.CreateAsync(new TorrentFileSource(source), torrentPath);
                var torrent = await Torrent.LoadAsync(torrentPath);
                fixture._hash = torrent.InfoHashes.V1!.ToHex();
                fixture._magnet = $"magnet:?xt=urn:btih:{fixture._hash}&tr={Uri.EscapeDataString(tracker)}";
                fixture._engine = new ClientEngine(new EngineSettingsBuilder
                {
                    AllowPortForwarding = false, AllowLocalPeerDiscovery = false,
                    AutoSaveLoadDhtCache = false, AutoSaveLoadFastResume = false, AutoSaveLoadMagnetLinkMetadata = false,
                    DhtEndPoint = null, CacheDirectory = Path.Combine(fixture.Root, "seed-cache"), MaximumUploadRate = rate,
                    ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, peerPort) }
                }.ToSettings());
                fixture._manager = await fixture._engine.AddAsync(torrent, fixture.Root,
                    new TorrentSettingsBuilder { MaximumUploadRate = rate }.ToSettings());
                await fixture._manager.StartAsync();
                await Eventually(() => fixture._manager.State == TorrentState.Seeding);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public List<TorrentResult> Candidates() =>
        [
            new() { Title = Title + " - S01E01", MagnetLink = _magnet, InfoHash = _hash, Seeders = 10 },
            new() { Title = Title + " - S01E01 alternative", MagnetLink = "magnet:?xt=urn:btih:not-a-hash", InfoHash = "different-fixture", Seeders = 0 }
        ];

        public TemporaryEpisodeWatchService Service(Func<string, long> capacity,
            Func<string, Action<string>?, CancellationToken, Task<List<TorrentResult>>>? search = null,
            Func<EngineSettings, ClientEngine>? createEngine = null) =>
            new(search ?? ((_, _, _) => Task.FromResult(Candidates())), Temporary, capacity, createEngine);

        public DomainHotSwapper SeasonConfig(string downloads)
        {
            var config = new DomainHotSwapper(Path.Combine(Root, "config.json"));
            config.SetSetting("DownloadDirectory", downloads);
            config.SetSetting("NyaaUrl", Origin + "rss?q=");
            config.SetSetting("AnimeToshoUrl", Origin + "rss?q=");
            config.SetSetting("QBitHost", "127.0.0.1");
            config.SetSetting("QBitPort", new Uri(Origin).Port.ToString());
            return config;
        }

        public void AssertRetainedAndClean()
        {
            Assert.Equal("permanent retained sentinel", File.ReadAllText(Path.Combine(Root, "permanent.mkv")));
            Assert.Empty(Directory.GetDirectories(Temporary));
        }

        private async Task ServeAsync(int peerPort)
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token);
                    byte[] peer = [127, 0, 0, 1, (byte)(peerPort >> 8), (byte)(peerPort & 255)];
                    byte[] body;
                    if (context.Request.Url!.AbsolutePath == "/rss")
                    {
                        body = Encoding.UTF8.GetBytes($"<rss version=\"2.0\" xmlns:nyaa=\"https://nyaa.si/xmlns/nyaa\"><channel><item><title>[Fixture] {Title} Season 1 [Batch]</title><link>{SecurityElement.Escape(_magnet)}</link><nyaa:seeders>1</nyaa:seeders><nyaa:infoHash>{_hash}</nyaa:infoHash></item></channel></rss>");
                        context.Response.ContentType = "application/xml";
                    }
                    else if (context.Request.Url.AbsolutePath.StartsWith("/api/v2/auth/"))
                    {
                        context.Response.StatusCode = 503;
                        body = [];
                    }
                    else body = new BEncodedDictionary { ["interval"] = new BEncodedNumber(2), ["min interval"] = new BEncodedNumber(1),
                        ["complete"] = new BEncodedNumber(1), ["incomplete"] = new BEncodedNumber(0), ["peers"] = new BEncodedString(peer) }.Encode();
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body, _lifetime.Token);
                    context.Response.Close();
                }
            }
            catch (Exception) when (_lifetime.IsCancellationRequested || !_listener.IsListening) { }
        }

        private static int FreePort()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Close();
            if (_engine is { IsRunning: true, Disposed: false } && _manager != null)
            {
                // MonoTorrent 3.0.2 has one process-wide pending send queue.
                // Leave this fixture's queued sends unlimited before its ticks stop,
                // so a disposed rate limiter cannot block the next fixture.
                var limitsReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int ticks = 0;
                EventHandler<StatsUpdateEventArgs> observe = (_, _) =>
                {
                    if (Interlocked.Increment(ref ticks) >= 2) limitsReleased.TrySetResult();
                };
                _engine.StatsUpdate += observe;
                try
                {
                    await _engine.UpdateSettingsAsync(new EngineSettingsBuilder(_engine.Settings) { MaximumUploadRate = 0 }.ToSettings());
                    await _manager.UpdateSettingsAsync(new TorrentSettingsBuilder(_manager.Settings) { MaximumUploadRate = 0 }.ToSettings());
                    await limitsReleased.Task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                finally { _engine.StatsUpdate -= observe; }
            }
            if (_manager != null) await _manager.StopAsync(TimeSpan.FromSeconds(2));
            _engine?.Dispose();
            if (_server != null) await _server;
            _lifetime.Dispose();
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.NativeTempBoundary")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its root.");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
