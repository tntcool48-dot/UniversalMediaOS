using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using LibVLCSharp.Shared;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E;

public sealed class NativeShortMediaCompletionTests(ITestOutputHelper output)
{
    [Fact]
    public Task TwoNativeWatchTogetherPlayersRetainPauseAcrossTwentyPeerReconnectsAndHostPromotion()
        => OnDispatcher(() => NativeWatchTogetherReconnectAsync(output));

    private static async Task NativeWatchTogetherReconnectAsync(ITestOutputHelper output)
    {
        string? previous = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests"));
        string root = Path.Combine(parent, "NativeWatchReconnect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, root);
        var progress = new PlaybackProgressService(new(Path.Combine(root, "library.json")));
        string? connectionString = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            string video = Path.Combine(root, "reconnect.mp4");
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error",
                "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25", "-t", "90", "-c:v", "mpeg4", "-y", video], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
            using (var database = new DatabaseContext())
            {
                database.Database.EnsureCreated();
                connectionString = database.Database.GetConnectionString()!;
            }
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            using var relay = new WatchRoomRelayService();
            using var hostClient = new WatchTogetherClientService(new DomainHotSwapper(Path.Combine(root, "host.json")));
            using var peerClient = new WatchTogetherClientService(new DomainHotSwapper(Path.Combine(root, "peer.json")));
            using var hostMemory = new MemoryVideo();
            using var peerMemory = new MemoryVideo();
            using var host = new PlaybackViewModel(new DatabaseContext(), hostClient, new(hostClient), playbackProgress: progress);
            using var peer = new PlaybackViewModel(new DatabaseContext(), peerClient, new(peerClient), playbackProgress: progress);
            hostMemory.Attach(host.MediaPlayer);
            peerMemory.Attach(peer.MediaPlayer);
            host.Volume = peer.Volume = 0;
            host.LoadMedia(video, "Reconnect host", episodeNumber: "1", malId: 42);
            peer.LoadMedia(video, "Reconnect peer", episodeNumber: "1", malId: 43);
            host.PlayPending();
            peer.PlayPending();
            await Wait(() => host.IsPlaying && hostMemory.Frames >= 2, host);
            await Wait(() => peer.IsPlaying && peerMemory.Frames >= 2, peer);
            host.TogglePlayPauseCommand.Execute(null);
            peer.TogglePlayPauseCommand.Execute(null);
            await Wait(() => host.MediaPlayer.State == VLCState.Paused && !host.IsPlaying, host);
            await Wait(() => peer.MediaPlayer.State == VLCState.Paused && !peer.IsPlaying, peer);
            host.ActivateWatchTogetherPlayback();
            peer.ActivateWatchTogetherPlayback();
            var initialSyncs = Channel.CreateUnbounded<double>();
            peerClient.MessageReceived += (_, message) =>
            {
                if (message.Payload.TryGetProperty("type", out var type) && type.GetString() == "initial_sync")
                    initialSyncs.Writer.TryWrite(message.Payload.GetProperty("timestamp").GetDouble());
            };
            int unexpectedStarts = 0;
            peer.MediaPlayer.Playing += (_, _) => Interlocked.Increment(ref unexpectedStarts);
            await relay.StartAsync(port, deadline.Token);
            string server = $"localhost:{port}";
            try
            {
                await hostClient.ConnectAsync(server, "native-reconnect", 0, deadline.Token);
                await Wait(() => hostClient.Role == WatchRoomRole.Host, host);
                using var process = Process.GetCurrentProcess();
                for (int cycle = 0; cycle < 20; cycle++)
                {
                    long hostPosition = 27_000 + cycle * 2_000;
                    host.MediaPlayer.Time = hostPosition;
                    await Wait(() => Math.Abs(host.MediaPlayer.Time - hostPosition) <= 1000, host);
                    await peerClient.ConnectAsync(server, "native-reconnect", 2.5, deadline.Token);
                    double synchronized = await initialSyncs.Reader.ReadAsync(deadline.Token);
                    Assert.InRange(synchronized, hostPosition / 1000.0 - 1, hostPosition / 1000.0 + 1);
                    await Wait(() => peer.MediaPlayer.State == VLCState.Paused && !peer.IsPlaying &&
                        Math.Abs(peer.MediaPlayer.Time - (synchronized + 2.5) * 1000) <= 1500, peer);
                    await Task.Delay(250, deadline.Token);
                    Assert.Equal(0, Volatile.Read(ref unexpectedStarts));
                    Assert.Equal(WatchRoomRole.Peer, peerClient.Role);
                    Assert.Equal(WatchRoomConnectionState.Connected, hostClient.State);
                    await peerClient.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
                    Assert.Equal(WatchRoomConnectionState.Disconnected, peerClient.State);
                    Assert.Equal(WatchRoomRole.None, peerClient.Role);
                    if ((cycle + 1) % 5 == 0)
                    {
                        long heap = GC.GetTotalMemory(forceFullCollection: true);
                        process.Refresh();
                        output.WriteLine($"Reconnect {cycle + 1}: managed heap {heap} bytes, private memory {process.PrivateMemorySize64} bytes, handles {process.HandleCount}.");
                    }
                }
                output.WriteLine($"Twenty native peer reconnects: {unexpectedStarts} unintended playing events; latest host/peer {host.MediaPlayer.Time}/{peer.MediaPlayer.Time} ms.");
                await peerClient.ConnectAsync(server, "native-reconnect", 2.5, deadline.Token);
                await initialSyncs.Reader.ReadAsync(deadline.Token);
                for (int repeat = 0; repeat < 2; repeat++)
                    await hostClient.SendAsync(new { type = "action", action = "buffer_pause", timestamp = host.MediaPlayer.Time / 1000.0 }, deadline.Token);
                await Task.Delay(250, deadline.Token);
                Assert.Equal(0, Volatile.Read(ref unexpectedStarts));
                Assert.Equal(VLCState.Paused, peer.MediaPlayer.State);
                await hostClient.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
                await Wait(() => peerClient.Role == WatchRoomRole.Host, peer);
                await hostClient.ConnectAsync(server, "native-reconnect", 0, deadline.Token);
                await Wait(() => hostClient.Role == WatchRoomRole.Peer, host);
                await Task.Delay(2200, deadline.Token); // Let accepted remote-command echo suppression expire.
                peer.BeginUserSeek();
                peer.CommitUserSeek(58_000);
                await Wait(() => Math.Abs(host.MediaPlayer.Time - 55_500) <= 1500, host);
                peer.TogglePlayPauseCommand.Execute(null);
                await Wait(() => host.MediaPlayer.State == VLCState.Playing && peer.MediaPlayer.State == VLCState.Playing, host);
                peer.TogglePlayPauseCommand.Execute(null);
                await Wait(() => host.MediaPlayer.State == VLCState.Paused && peer.MediaPlayer.State == VLCState.Paused, host);
                Assert.True(hostMemory.Frames > 2 && peerMemory.Frames > 2);
                Assert.False(host.HasPlaybackError || peer.HasPlaybackError);
                output.WriteLine("Host promotion/rejoin retained roles, offset seek, decoded play and explicit pause on both native players.");
            }
            finally
            {
                await peerClient.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
                await hostClient.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
                relay.Stop();
                Assert.False(relay.IsRunning);
            }
        }
        finally
        {
            await progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previous);
            if (connectionString != null)
            {
                using var pool = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
            }
            Assert.StartsWith(parent + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase);
            Assert.Null(new DirectoryInfo(root).LinkTarget);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public Task NativeStartWaitsForItsLockedResumeThenDecodesAtTheSavedPosition()
        => OnDispatcher(NativeLockedResumeAsync);

    [Fact]
    public Task PausedDisplayRedrawDecodesAnotherFrameWithoutResumeOrSourceReplacement()
        => OnDispatcher(async () =>
        {
            string? previous = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "NativeDisplayRedraw-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, root);
            string? connectionString = null;
            PlaybackProgressService? progress = null;
            try
            {
                string video = Path.Combine(root, "redraw.mp4"), caption = Path.Combine(root, "redraw.en.srt");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error",
                    "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25", "-t", "90", "-c:v", "mpeg4", "-y", video], deadline.Token);
                Assert.Equal(0, encoded.ExitCode);
                File.WriteAllText(caption, "1\n00:00:00,000 --> 00:01:30,000\nFirst line\nSecond line\n");
                byte[] original = await File.ReadAllBytesAsync(video);
                using (var seed = new DatabaseContext())
                {
                    seed.Database.EnsureCreated();
                    connectionString = seed.Database.GetConnectionString();
                }
                progress = new(new AudiovisualLibraryService());
                using var memory = new MemoryVideo();
                using var player = new PlaybackViewModel(new DatabaseContext(), null, null, playbackProgress: progress);
                memory.Attach(player.MediaPlayer);
                player.Volume = 0;
                player.LoadMedia(video, "Redraw fixture", localCaptionPaths: [caption]);
                player.PlayPending();
                await Wait(() => player.IsPlaying && memory.Frames >= 2 && player.MediaPlayer.IsSeekable &&
                    player.SelectedCaption?.Label == "English (downloaded)", player);
                Assert.False(player.RedrawPausedNativeFrame());
                player.MediaPlayer.Time = 10_000;
                await Wait(() => player.MediaPlayer.Time >= 9_900, player);
                player.TogglePlayPauseCommand.Execute(null);
                await Wait(() => !player.IsPlaying && player.MediaPlayer.State == VLCState.Paused, player);
                await Task.Delay(200);
                long position = player.MediaPlayer.Time;
                string selected = player.SelectedCaption!.Key;
                int frames = memory.Frames, unexpectedStarts = 0, remoteActions = 0;
                player.MediaPlayer.Playing += (_, _) => Interlocked.Increment(ref unexpectedStarts);
                player.WebPlaybackCommandRequested += (_, _) => remoteActions++;
                Assert.True(player.RedrawPausedNativeFrame());
                await Wait(() => memory.Frames > frames, player);
                await Task.Delay(200);
                Assert.Equal(VLCState.Paused, player.MediaPlayer.State);
                Assert.False(player.IsPlaying);
                Assert.InRange(player.MediaPlayer.Time, position - 100, position + 100);
                Assert.Equal(video, player.SourceInput);
                Assert.Equal(selected, player.SelectedCaption!.Key);
                Assert.Equal(0, unexpectedStarts);
                Assert.Equal(0, remoteActions);
                player.SetTabActive(false);
                Assert.False(player.RedrawPausedNativeFrame());
                player.SetTabActive(true);
                player.LoadEmbed("https://example.invalid/fixture", "Web guard fixture");
                Assert.False(player.RedrawPausedNativeFrame());
                player.Dispose();
                Assert.False(player.RedrawPausedNativeFrame());
                Assert.Equal(original, await File.ReadAllBytesAsync(video));
            }
            finally
            {
                if (progress != null) await progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previous);
                if (connectionString != null)
                {
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                    Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
                }
                string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests")) + Path.DirectorySeparatorChar;
                Assert.StartsWith(parent, root, StringComparison.OrdinalIgnoreCase);
                Assert.Null(new DirectoryInfo(root).LinkTarget);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });

    private static async Task NativeLockedResumeAsync()
    {
        string? previous = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "NativeLockedResume-" + Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, root);
        string? connectionString = null;
        PlaybackProgressService? progress = null;
        try
        {
            Directory.CreateDirectory(root);
            string video = Path.Combine(root, "resume.mp4");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error",
                "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25", "-t", "90", "-c:v", "mpeg4", "-y", video], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
            const string work = "av:fixture:film:locked-native";
            const string unit = "feature";
            using (var seed = new DatabaseContext())
            {
                seed.Database.EnsureCreated();
                seed.SaveResumeState(work, unit, 27);
                connectionString = seed.Database.GetConnectionString()!;
            }
            using var memory = new MemoryVideo();
            progress = new(new AudiovisualLibraryService());
            using var player = new PlaybackViewModel(new DatabaseContext(), null, null, playbackProgress: progress);
            memory.Attach(player.MediaPlayer);
            player.Volume = 0;
            using (var writer = new ResumeDispatcherContentionTests.WriterLock(connectionString, work, unit, 5000))
            {
                player.LoadMedia(video, "Locked native fixture", audiovisualContext: new(work,
                    new() { ContentForm = AudiovisualContentForm.Feature }, new(), "Locked native fixture", "", new()));
                player.PlayPending();
                await Task.Delay(300);
                Assert.False(player.ResumeLoadCompleted.IsCompleted);
                Assert.False(player.IsPlaying);
                Assert.Equal(0, memory.Frames);
                Assert.NotEmpty(player.PendingMediaPath);
            }
            await player.ResumeLoadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            await Wait(() => memory.Frames >= 2 && player.MediaPlayer.Time >= 26_000, player);
            Assert.InRange(player.MediaPlayer.Time, 26_000, 31_000);
            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<EventArgs> onPaused = (_, _) => paused.TrySetResult();
            player.MediaPlayer.Paused += onPaused;
            try { player.SetTabActive(false); await paused.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { player.MediaPlayer.Paused -= onPaused; }
            player.Dispose();
            using var verify = new DatabaseContext();
            Assert.InRange(verify.GetResumeState(work, unit), 26, 31);
        }
        finally
        {
            if (progress != null) await progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previous);
            if (connectionString != null)
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            }
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(parent, root, StringComparison.OrdinalIgnoreCase);
            Assert.Null(new DirectoryInfo(root).LinkTarget);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    internal static Task OnDispatcher(Func<Task> test, TimeSpan? timeout = null)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Exception? failure = null;
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(async () =>
                {
                    try { await test(); }
                    catch (Exception ex) { failure = ex; }
                    finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
                });
                System.Windows.Threading.Dispatcher.Run();
                // Native view/window cleanup can still be queued at the end of
                // the case. Finish dispatcher shutdown before releasing xUnit.
                if (failure == null) completed.TrySetResult();
                else completed.TrySetException(failure);
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task NativeDispatcherHelperWaitsForShutdownBeforeTheNextCase()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = OnDispatcher(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.ShutdownStarted += (_, _) =>
            {
                started.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            };
            dispatcher.ShutdownFinished += (_, _) => finished.TrySetResult();
            return Task.CompletedTask;
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(run.IsCompleted, "The next native case must not start while the previous dispatcher is shutting down.");
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        await run;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RealShortVideoFinishesLocallyButRetainsTheProviderCompletionGuard(bool network)
        => OnDispatcher(() => RealShortVideoAsync(network));

    private static async Task RealShortVideoAsync(bool network)
    {
        string? previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", "NativeCompletion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, root);
        PlaybackProgressService? progress = null;
        try
        {
            string video = Path.Combine(root, "short-video.mp4");
            using var encodingDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error",
                "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25", "-t", "90", "-c:v", "mpeg4",
                "-movflags", "+faststart", "-y", video], encodingDeadline.Token);
            Assert.Equal(0, encoded.ExitCode);
            long bytes = new FileInfo(video).Length;
            DateTime written = File.GetLastWriteTimeUtc(video);
            using var server = new VideoServer(await File.ReadAllBytesAsync(video));
            const string work = "av:fixture:show:short-completion";
            const string unit = "season:1:episode:1";
            using (var seed = new DatabaseContext())
            {
                seed.Database.EnsureCreated();
                seed.SaveResumeState(work, "season:1:episode:2", 57);
            }
            var context = new AudiovisualPlaybackContext(work,
                new() { ContentForm = AudiovisualContentForm.Series, Title = "Short completion fixture" },
                new() { SeasonNumber = 1, EpisodeNumber = 1 }, "Short completion fixture", "", new());
            using var memory = new MemoryVideo();
            progress = new(new AudiovisualLibraryService());
            using var player = new PlaybackViewModel(new DatabaseContext(), null, null, playbackProgress: progress);
            memory.Attach(player.MediaPlayer);
            player.Volume = 0;
            player.LoadMedia(network ? server.Url : video, context.Title, "", "1", audiovisualContext: context);
            player.PlayPending();
            await Wait(() => player.IsPlaying && player.MediaPlayer.Length >= 89_000 && memory.Frames >= 2, player);
            player.MediaPlayer.Time = 87_000;
            await Wait(() => player.MediaPlayer.Time >= 86_000 || player.HasPlaybackError, player);
            await Wait(() => player.PlaybackStatusText == "Finished" || player.HasPlaybackError, player);
            Assert.True(memory.Frames > 2);
            if (network)
            {
                Assert.True(player.HasPlaybackError);
                Assert.NotEqual("Finished", player.PlaybackStatusText);
            }
            else
            {
                Assert.False(player.HasPlaybackError, player.PlaybackErrorText);
                Assert.Equal("Finished", player.PlaybackStatusText);
                Assert.False(player.IsPlaying);
                Assert.False(player.IsPlaybackBusy);
                await Wait(() =>
                {
                    using var completed = new DatabaseContext();
                    return completed.GetResumeState(work, unit) == 0;
                }, player);
                int framesBeforeReplay = memory.Frames;
                player.TogglePlayPauseCommand.Execute(null);
                await Wait(() => player.IsPlaying && player.MediaPlayer.Time is >= 500 and < 5_000 &&
                    memory.Frames > framesBeforeReplay + 2, player);
                player.MediaPlayer.Time = 12_000;
                await Wait(() => player.MediaPlayer.Time >= 11_000, player);
                // Wait for the real decoder callback, including the player's track
                // refresh, before Stop. IsPlaying changes optimistically on click.
                var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<EventArgs> onPaused = (_, _) => paused.TrySetResult();
                player.MediaPlayer.Paused += onPaused;
                try
                {
                    player.TogglePlayPauseCommand.Execute(null);
                    await paused.Task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                finally { player.MediaPlayer.Paused -= onPaused; }
                await Wait(() => !player.IsPlaying, player);
                // Switching this formerly local player to a website must not
                // grant a short browser clip local-file completion semantics.
                player.LoadEmbed("https://example.invalid/ad", "Browser guard fixture", audiovisualContext:
                    new(work, context.Identity, new() { SeasonNumber = 1, EpisodeNumber = 2 },
                        "Browser guard fixture", "", new()));
                player.ReportWebPlaybackProgress(90, 90, ended: true, paused: true);
                player.ReportWebPlaybackAction("ended", 90, 90);
                Assert.NotEqual("Finished", player.PlaybackStatusText);
            }
            player.Dispose();
            using var verify = new DatabaseContext();
            Assert.Equal(57, verify.GetResumeState(work, "season:1:episode:2"));
            if (network) Assert.True(verify.GetResumeState(work, unit) > 0);
            else Assert.InRange(verify.GetResumeState(work, unit), 11, 15);
            Assert.Equal(bytes, new FileInfo(video).Length);
            Assert.Equal(written, File.GetLastWriteTimeUtc(video));
        }
        finally
        {
            if (progress != null) await progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={Path.Combine(root, "Roaming", "UniversalMediaOS", "media_os.db")};Cache=Shared;"))
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previousRoot);
            string target = Path.GetFullPath(root);
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests")) + Path.DirectorySeparatorChar;
            if (target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(target, true);
        }
    }

    [Theory]
    [InlineData(89.8, 90, true)]
    [InlineData(30, 90, false)]
    [InlineData(0, 10, false)]
    [InlineData(9.8, 10, true)]
    [InlineData(93, 90, false)]
    [InlineData(double.NaN, 90, false)]
    [InlineData(90, -1, false)]
    [InlineData(90, 0, false)]
    public void LocalFileCompletionRejectsPrematureMissingAndInvalidPositions(double position, double duration, bool expected) =>
        Assert.Equal(expected, PlaybackViewModel.IsCredibleCompletion(position, duration, isLocalFile: true));

    private static async Task Wait(Func<bool> condition, PlaybackViewModel player)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(25);
        Assert.True(condition(), $"status={player.PlaybackStatusText}, position={player.PlaybackTime}, " +
            $"duration={player.PlaybackDuration}, error={player.PlaybackErrorText}");
    }

    private sealed class MemoryVideo : IDisposable
    {
        private readonly IntPtr _buffer = Marshal.AllocHGlobal(160 * 90 * 4);
        private int _frames;
        public int Frames => Volatile.Read(ref _frames);
        public void Attach(MediaPlayer player)
        {
            player.SetVideoFormat("RV32", 160, 90, 640);
            player.SetVideoCallbacks((_, planes) => { Marshal.WriteIntPtr(planes, _buffer); return IntPtr.Zero; },
                null, (_, _) => Interlocked.Increment(ref _frames));
        }
        public void Dispose() => Marshal.FreeHGlobal(_buffer);
    }

    [Fact]
    public async Task NativeVideoRangeRequestIsNotBlockedByAnEarlierUnreadBody()
    {
        const int length = 16 * 1024 * 1024;
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)73);
        using var server = new VideoServer(bytes);
        using var unread = new TcpClient { ReceiveBufferSize = 4096 };
        var uri = new Uri(server.Url);
        await unread.ConnectAsync(uri.Host, uri.Port);
        var stream = unread.GetStream();
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
            $"GET {uri.AbsolutePath} HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(stream, leaveOpen: true);
        try
        {
            Assert.Contains("200", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))) { }
            // A demuxer can stop reading one body while requesting the MP4 index
            // or seeking over a second connection. The first body stays unread.
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, server.Url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(length - 1024, length - 1);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(length - 1024, response.Content.Headers.ContentRange!.From);
            Assert.Equal(length - 1, response.Content.Headers.ContentRange.To);
            byte[] tail = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(1024, tail.Length);
            Assert.All(tail, value => Assert.Equal(73, value));
        }
        finally { unread.Dispose(); }
    }

    internal sealed class VideoServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _responses = new(4);
        private readonly Task _worker;
        public string Url { get; }
        public VideoServer(byte[] bytes)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Url = $"http://127.0.0.1:{port}/short-video.mp4";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _worker = Task.Run(async () =>
            {
                var pending = new List<Task>();
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        await _responses.WaitAsync(_stop.Token);
                        HttpListenerContext context;
                        try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
                        catch { _responses.Release(); throw; }
                        pending.RemoveAll(task => task.IsCompletedSuccessfully);
                        pending.Add(RespondAsync(context));
                    }
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
                finally { await Task.WhenAll(pending); }
            });

            async Task RespondAsync(HttpListenerContext context)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var abort = deadline.Token.Register(() =>
                {
                    try { context.Response.Abort(); }
                    catch (ObjectDisposedException) { }
                });
                try
                {
                    var response = context.Response;
                    response.ContentType = "video/mp4";
                    response.Headers["Accept-Ranges"] = "bytes";
                    int offset = 0;
                    string? range = context.Request.Headers["Range"];
                    if (range?.StartsWith("bytes=", StringComparison.Ordinal) == true &&
                        int.TryParse(range[6..].Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int start))
                    {
                        offset = Math.Clamp(start, 0, bytes.Length - 1);
                        response.StatusCode = 206;
                        response.Headers["Content-Range"] = $"bytes {offset}-{bytes.Length - 1}/{bytes.Length}";
                    }
                    response.ContentLength64 = bytes.Length - offset;
                    if (context.Request.HttpMethod != "HEAD")
                        await response.OutputStream.WriteAsync(bytes.AsMemory(offset), deadline.Token);
                }
                catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException) { }
                finally
                {
                    context.Response.Close();
                    _responses.Release();
                }
            }
        }
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            Assert.True(_worker.Wait(TimeSpan.FromSeconds(3)));
            _responses.Dispose();
            _stop.Dispose();
        }
    }
}
