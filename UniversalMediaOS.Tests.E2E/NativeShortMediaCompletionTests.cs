using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class NativeShortMediaCompletionTests
{
    [Fact]
    public Task NativeStartWaitsForItsLockedResumeThenDecodesAtTheSavedPosition()
        => OnDispatcher(NativeLockedResumeAsync);

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

    private static Task OnDispatcher(Func<Task> test)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completed.TrySetResult(); }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            });
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(40));
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

    private sealed class VideoServer : IDisposable
    {
        private readonly HttpListener _listener = new();
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
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { break; }
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
                            await response.OutputStream.WriteAsync(bytes.AsMemory(offset));
                    }
                    catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException) { }
                    finally { context.Response.Close(); }
                }
            });
        }
        public void Dispose()
        {
            _listener.Close();
            Assert.True(_worker.Wait(TimeSpan.FromSeconds(3)));
        }
    }
}
