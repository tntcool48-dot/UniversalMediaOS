using System.IO;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.Messaging;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class NativeTemporaryDownloadBoundaryTests
{
    private const long Reserve = 1024L * 1024 * 1024;

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

    private static async Task Eventually(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(50, deadline.Token);
    }

    private sealed class LocalSeeder : IAsyncDisposable
    {
        public const string Title = "Queue Space Fixture";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.NativeTempBoundary", Guid.NewGuid().ToString("N"));
        public string Temporary => Path.Combine(Root, "temporary");
        public long VideoBytes { get; private set; }
        public long UploadedBytes => _manager!.Monitor.DataBytesSent;
        public List<string> Logs { get; } = [];
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HttpListener _listener = new();
        private ClientEngine? _engine;
        private TorrentManager? _manager;
        private Task? _server;
        private string _magnet = "";
        private string _hash = "";

        public static async Task<LocalSeeder> StartAsync(int rate)
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
                     "-f", "lavfi", "-i", "sine=frequency=440", "-t", "30", "-c:v", "mpeg4", "-q:v", "3", "-c:a", "aac", "-y", video], deadline.Token);
                Assert.Equal(0, encoded.ExitCode);
                fixture.VideoBytes = new FileInfo(video).Length;
                Assert.True(fixture.VideoBytes > 5 * 1024 * 1024);
                await File.WriteAllTextAsync(Path.Combine(source, Title + " - S01E01.en.srt"), "1\n00:00:00,000 --> 00:00:30,000\nGenerated cue\n", deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, "permanent.mkv"), "permanent retained sentinel", deadline.Token);
                int httpPort = FreePort(), peerPort = FreePort();
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
                fixture._manager = await fixture._engine.AddAsync(torrent, fixture.Root);
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
            Func<string, Action<string>?, CancellationToken, Task<List<TorrentResult>>>? search = null) =>
            new(search ?? ((_, _, _) => Task.FromResult(Candidates())), Temporary, capacity);

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
                    byte[] body = new BEncodedDictionary { ["interval"] = new BEncodedNumber(2), ["min interval"] = new BEncodedNumber(1),
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
