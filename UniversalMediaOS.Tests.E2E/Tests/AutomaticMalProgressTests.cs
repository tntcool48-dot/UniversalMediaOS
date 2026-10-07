using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Views;
using LibVLCSharp.Shared;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class AutomaticMalProgressTests
{
    [Fact]
    public Task NativeDecoderAndProductionViewRetryThenRetainSameUnitAcrossProviderReloadAndChangeIdentity()
        => NativeShortMediaCompletionTests.OnDispatcher(NativeProgressAsync, TimeSpan.FromSeconds(150));

    private static async Task NativeProgressAsync()
    {
        using var profile = new Profile();
        int attempts = 0;
        var remote = new ConcurrentDictionary<int, int>();
        using var server = new MockHttpServer(malProgressFeed: request =>
        {
            int id = int.Parse(request.Url!.Segments[3].Trim('/'));
            if (id is not (101 or 202)) return (404, "{}");
            if (request.HttpMethod == "GET") return (200, JsonSerializer.Serialize(new { id, num_episodes = 12,
                my_list_status = new { status = "watching", num_episodes_watched = remote.GetValueOrDefault(id), is_rewatching = false } }));
            var write = profile.Capture(request);
            if (Interlocked.Increment(ref attempts) == 1) return (503, "{}");
            remote[id] = write.Episode;
            return (200, "{}");
        });
        server.Start();
        string video = Path.Combine(profile.Root, "controlled-native.mp4");
        using (var encodingDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error",
                "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25", "-t", "90", "-c:v", "mpeg4", "-y", video], encodingDeadline.Token);
            Assert.Equal(0, encoded.ExitCode);
        }
        string caption = Path.Combine(profile.Root, "controlled.en.srt");
        using var replacementSource = new NativeShortMediaCompletionTests.VideoServer(File.ReadAllBytes(video));
        File.WriteAllText(caption, "1\n00:00:00,000 --> 00:01:30,000\nControlled MAL profile\nGenerated video without audio\n");
        string configPath = Path.Combine(profile.Root, "Roaming", "UniversalMediaOS", "config.json");
        using var player = profile.Player(server);
        string config = File.ReadAllText(configPath);
        var resources = ControlThemeRenderTests.LoadResources(true);
        resources["BoolToVisibility"] = new System.Windows.Controls.BooleanToVisibilityConverter();
        var view = new PlaybackView(resources) { DataContext = player };
        var window = new System.Windows.Window { Title = "UniversalMediaOS - automatic MAL native QA", Content = view,
            Width = 1280, Height = 720, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
        PositionOnSecondary(window);
        async Task Wait(Func<bool> condition, string message)
        {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            Assert.True(condition(), message + $" state={player.MediaPlayer.State}, time={player.PlaybackTime}, " +
                $"duration={player.PlaybackDuration}, error={player.PlaybackErrorText}");
        }
        async Task Pause()
        {
            if (player.MediaPlayer.State == VLCState.Paused) return;
            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<EventArgs> handler = (_, _) => paused.TrySetResult();
            player.MediaPlayer.Paused += handler;
            try { player.TogglePlayPauseCommand.Execute(null); await paused.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { player.MediaPlayer.Paused -= handler; }
            await Wait(() => !player.IsPlaying, "Native pause must settle before the next source or observation.");
        }
        async Task Hold(string phase)
        {
            string? directory = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_AUTO_MAL_NATIVE_QA_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            string? onlyPhase = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_AUTO_MAL_NATIVE_OBSERVE_PHASE");
            if (!string.IsNullOrWhiteSpace(onlyPhase) && onlyPhase != phase) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "automatic-mal-native-ready.json"), JsonSerializer.Serialize(new
            {
                Phase = phase, Pid = Environment.ProcessId, Window = new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64(),
                Profile = profile.Root, Writes = profile.Writes.ToArray(), Position = player.PlaybackTime / 1000,
                Paused = player.MediaPlayer.State == VLCState.Paused, Utc = DateTime.UtcNow
            }));
            if (int.TryParse(Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_AUTO_MAL_NATIVE_HOLD_MS"), out int hold))
                await Task.Delay(Math.Clamp(hold, 0, 60_000));
        }
        try
        {
            window.Show();
            player.LoadMedia(video, "Controlled anime - Ep 3", malId: 101, episodeNumber: "3", totalEpisodes: 12,
                localCaptionPaths: [caption]);
            player.PlayPending();
            await Wait(() => player.IsPlaying && player.MediaPlayer.VoutCount > 0 && player.PlaybackDuration > 80_000,
                "The production view must attach a real native video output and decoder clock.");
            player.BeginUserSeek();
            player.CommitUserSeek(80_000);
            await Wait(() => profile.Writes.Count >= 2 && Synced(player), "Real decoder progress must retry the first failed update.");
            Assert.All(profile.Writes, write => { Assert.Equal(101, write.MalId); Assert.Equal(3, write.Episode); });
            await Pause();
            await player.FlushResumeAsync();
            await Hold("native-progress-retried-correct-anime-episode");
            double position = player.PlaybackTime / 1000;
            player.LoadMedia(replacementSource.Url, "Controlled source URL change - Ep 3", malId: 101, episodeNumber: "3", totalEpisodes: 12,
                localCaptionPaths: [caption], reloadPositionSeconds: position, pauseAfterReload: true);
            player.PlayPending();
            await Wait(() => player.MediaPlayer.State == VLCState.Paused && player.MediaPlayer.VoutCount > 0 &&
                Math.Abs(player.PlaybackTime / 1000 - position) < 2, "Same-unit provider reload must retain the paused native position.");
            Assert.Equal(2, profile.Writes.Count);
            Assert.True(Synced(player));
            await Hold("same-unit-reload-retained-sync-and-paused-native-position");
            player.LoadMedia(video, "Different controlled anime - Ep 4", malId: 202, episodeNumber: "4", totalEpisodes: 12,
                localCaptionPaths: [caption]);
            player.PlayPending();
            await Wait(() => player.IsPlaying && player.MediaPlayer.VoutCount > 0 && player.PlaybackDuration > 80_000,
                "The new work/unit must receive a real native output.");
            player.BeginUserSeek();
            player.CommitUserSeek(80_000);
            await Wait(() => profile.Writes.Count == 3 && Synced(player), "The new work/unit must sync its own MAL ID and episode.");
            await Pause();
            Assert.Equal(3, remote[101]);
            Assert.Equal(4, remote[202]);
            var last = profile.Writes.Last();
            Assert.Equal(202, last.MalId);
            Assert.Equal(4, last.Episode);
            Assert.Equal(config, File.ReadAllText(configPath));
            await player.FlushResumeAsync();
            await Hold("new-work-unit-synced-with-old-progress-retained");
        }
        finally
        {
            if (player.MediaPlayer.State == VLCState.Playing) await Pause();
            await player.FlushResumeAsync();
            window.Close();
            player.Dispose();
            await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static void PositionOnSecondary(System.Windows.Window window)
    {
        var monitors = new List<MonitorInfo>();
        MonitorCallback callback = (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(handle, ref info)) monitors.Add(info);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        var target = monitors.FirstOrDefault(monitor => (monitor.Flags & 1) == 0);
        if (target.Size == 0) target = monitors.First();
        window.Left = target.Work.Left;
        window.Top = target.Work.Top;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        Assert.True(SetWindowPos(hwnd, IntPtr.Zero, target.Work.Left, target.Work.Top,
            target.Work.Right - target.Work.Left, target.Work.Bottom - target.Work.Top, 0x0014));
        GC.KeepAlive(callback);
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [Fact]
    public async Task FailedAutomaticUpdateRetriesTheSameAnimeAndEpisodeEvenAfterPlaybackEnded()
    {
        using var profile = new Profile();
        int attempts = 0;
        using var server = profile.Server(write => Interlocked.Increment(ref attempts) == 1 ? 503 : 200);
        using var player = profile.Player(server);
        Configure(player, 101, 3);
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(2, attempts);
        Assert.All(profile.Writes, write => Assert.Equal(new Write(101, 3, "completed", ""), write));
        Assert.True(Synced(player));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledOrUnconnectedProgressCanSyncAfterTheUserEnablesOrConnects(bool disabled)
    {
        using var profile = new Profile();
        using var server = profile.Server(_ => 200);
        using var player = profile.Player(server);
        profile.Config.SetSetting(disabled ? "AutoSyncMal" : "MalOAuthToken", disabled ? "false" : "");
        Configure(player, 101, 3);
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(profile.Writes);
        profile.Config.SetSetting(disabled ? "AutoSyncMal" : "MalOAuthToken", disabled ? "true" : "controlled-fixture-token");
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new Write(101, 3, "completed", ""), Assert.Single(profile.Writes));
    }

    [Fact]
    public async Task SourceChangeDuringTokenRefreshCannotWriteOrMarkTheNewAnimeFromTheOldOperation()
    {
        using var profile = new Profile();
        using var server = profile.Server(_ => 200);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new TokenHandler(async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(
                "{\"access_token\":\"controlled-refreshed-token\",\"expires_in\":3600}", Encoding.UTF8, "application/json") };
        }));
        profile.Config.SetSetting("MalOAuthRefreshToken", "controlled-refresh-token");
        profile.Config.SetSetting("MalOAuthExpiresAtUtc", DateTime.UtcNow.AddHours(-1).ToString("O"));
        using var oauth = new MalOAuthService(profile.Config, client);
        using var player = profile.Player(server, oauth);
        Configure(player, 101, 3);
        TryProgress(player);
        Task original = player.PendingMalProgressSync;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Configure(player, 202, 4);
            release.TrySetResult();
            await original.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(profile.Writes, write => write.MalId == 202);
            Assert.False(Synced(player));
            TryProgress(player);
            await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(profile.Writes, write => write.MalId == 202 && write.Episode == 4);
            Assert.True(Synced(player));
        }
        finally { release.TrySetResult(); await original.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task SameUnitProviderReloadKeepsSuccessfulSyncWithoutASecondWrite()
    {
        using var profile = new Profile();
        using var server = profile.Server(_ => 200);
        using var player = profile.Player(server);
        Configure(player, 101, 3);
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(3));
        Configure(player, 101, 3);
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(profile.Writes);
    }

    [Theory]
    [InlineData("watching")]
    [InlineData("completed")]
    public async Task AutomaticEarlierEpisodeCannotReduceAlreadyWatchedProgress(string status)
    {
        using var profile = new Profile();
        using var server = profile.Server(_ => 200, watched: 9, status: status);
        using var player = profile.Player(server);
        Configure(player, 101, 3);
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(profile.Writes);
        Assert.True(Synced(player));
    }

    [Fact]
    public async Task ExplicitRewatchRetainsItsRemoteStatusAndRewatchFlag()
    {
        using var profile = new Profile();
        using var server = profile.Server(_ => 200, watched: 9, status: "completed", rewatching: true);
        using var player = profile.Player(server);
        Configure(player, 101, 3);
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(3));
        var write = Assert.Single(profile.Writes);
        Assert.Equal(101, write.MalId);
        Assert.Equal(3, write.Episode);
        Assert.NotEqual("watching", write.Status);
        Assert.Equal("true", write.Rewatching);
    }

    [Fact]
    public async Task PermanentFailureStopsAtThreeAttemptsAndCanRetryWithoutClaimingSuccess()
    {
        using var profile = new Profile();
        bool recovering = false;
        using var server = profile.Server(_ => recovering ? 200 : 503);
        using var player = profile.Player(server);
        Configure(player, 101, 3);
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(3, profile.Writes.Count);
        Assert.False(Synced(player));
        recovering = true;
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4, profile.Writes.Count);
        Assert.True(Synced(player));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosingTheOwnerOrWholeOperationDeadlineCancelsTokenWaitWithoutAnUpdate(bool close)
    {
        using var profile = new Profile();
        using var server = profile.Server(_ => 200);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new TokenHandler(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The owned cancellation must end this token request.");
        }));
        profile.Config.SetSetting("MalOAuthRefreshToken", "controlled-refresh-token");
        profile.Config.SetSetting("MalOAuthExpiresAtUtc", DateTime.UtcNow.AddHours(-1).ToString("O"));
        using var oauth = new MalOAuthService(profile.Config, client);
        using var player = profile.Player(server, oauth, close ? TimeSpan.FromSeconds(45) : TimeSpan.FromMilliseconds(500));
        Configure(player, 101, 3);
        TryProgress(player);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (close) player.Dispose();
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(profile.Writes);
        Assert.False(Synced(player));
    }

    [Fact]
    public async Task ConcurrentPlayersReadAfterTheNewerAcceptedWriteAndDoNotReduceIt()
    {
        using var profile = new Profile();
        int watched = 1;
        int reads = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var server = new MockHttpServer(malProgressFeed: request =>
        {
            if (request.HttpMethod == "GET")
            {
                Interlocked.Increment(ref reads);
                return (200, JsonSerializer.Serialize(new { num_episodes = 12, my_list_status = new
                    { status = "watching", num_episodes_watched = Volatile.Read(ref watched) } }));
            }
            var write = profile.Capture(request);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            Volatile.Write(ref watched, write.Episode);
            return (200, "{}");
        });
        server.Start();
        using var newer = profile.Player(server);
        using var older = profile.Player(server);
        Configure(newer, 101, 7);
        Configure(older, 101, 3);
        try
        {
            TryProgress(newer);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            TryProgress(older);
            Assert.Equal(1, Volatile.Read(ref reads));
            Assert.False(older.PendingMalProgressSync.IsCompleted);
            release.Set();
            await Task.WhenAll(newer.PendingMalProgressSync, older.PendingMalProgressSync).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(7, watched);
            Assert.Equal(7, Assert.Single(profile.Writes).Episode);
            Assert.Equal(2, reads);
            Assert.True(Synced(newer));
            Assert.True(Synced(older));
        }
        finally { release.Set(); await Task.WhenAll(newer.PendingMalProgressSync, older.PendingMalProgressSync).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task DisablingAutoSyncDuringBackoffStopsFurtherWrites()
    {
        using var profile = new Profile();
        using var server = profile.Server(_ => { profile.Config.SetSetting("AutoSyncMal", "false"); return 503; });
        using var player = profile.Player(server);
        Configure(player, 101, 3);
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(profile.Writes);
        Assert.False(Synced(player));
    }

    [Theory]
    [InlineData(503, "{}")]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"id\":202,\"num_episodes\":12,\"my_list_status\":{\"status\":\"watching\",\"num_episodes_watched\":1}}")]
    public async Task UnavailableIncompleteOrConflictingRemoteStatusCannotAuthorizeAnAutomaticWrite(int status, string body)
    {
        using var profile = new Profile();
        using var server = new MockHttpServer(malProgressFeed: request =>
        {
            if (request.HttpMethod == "GET") return (status, body);
            profile.Capture(request);
            return (200, "{}");
        });
        server.Start();
        using var player = profile.Player(server);
        Configure(player, 101, 3);
        TryProgress(player);
        await player.PendingMalProgressSync.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Empty(profile.Writes);
        Assert.False(Synced(player));
    }

    private static void Configure(PlaybackViewModel player, int malId, int episode) => typeof(PlaybackViewModel)
        .GetMethod("ConfigureTracking", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(player, [malId, episode.ToString(), "Controlled title"]);
    private static void TryProgress(PlaybackViewModel player) => typeof(PlaybackViewModel)
        .GetMethod("TrySyncMalFromProgress", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(player, [9_000d, 10_000d, true]);
    private static bool Synced(PlaybackViewModel player) => (bool)typeof(PlaybackViewModel)
        .GetField("_malProgressSynced", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(player)!;
    private sealed class TokenHandler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => response(token);
    }
    private sealed record Write(int MalId, int Episode, string Status, string Rewatching);
    private sealed class Profile : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UMOS-Automatic-Mal-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previous = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
        public DomainHotSwapper Config { get; }
        public string Root => _root;
        public ConcurrentQueue<Write> Writes { get; } = new();
        public Profile()
        {
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _root);
            Config = new DomainHotSwapper(Path.Combine(_root, "Roaming", "UniversalMediaOS", "config.json"));
            Assert.True(Config.SetSettings(new Dictionary<string, string> { ["AutoSyncMal"] = "true",
                ["MalOAuthToken"] = "controlled-fixture-token", ["DatabasePath"] = Path.Combine(_root, "media_os.db") }));
            using var db = new DatabaseContext();
            db.Database.EnsureCreated();
        }
        public MockHttpServer Server(Func<Write, int> result, int watched = 1, string status = "completed", bool rewatching = false)
        {
            var server = new MockHttpServer(malProgressFeed: request =>
            {
                if (request.HttpMethod == "GET") return (200, JsonSerializer.Serialize(new { num_episodes = 12,
                    my_list_status = new { status, num_episodes_watched = watched, is_rewatching = rewatching } }));
                var write = Capture(request);
                return (result(write), "{}");
            });
            server.Start();
            return server;
        }
        public Write Capture(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            var form = reader.ReadToEnd().Split('&').Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
            var write = new Write(int.Parse(request.Url!.Segments[3].Trim('/')),
                int.Parse(form["num_watched_episodes"]), form.GetValueOrDefault("status", ""), form.GetValueOrDefault("is_rewatching", ""));
            Writes.Enqueue(write);
            return write;
        }
        public PlaybackViewModel Player(MockHttpServer server, MalOAuthService? oauth = null, TimeSpan? deadline = null)
        {
            Config.SetSetting("MalApiUrl", server.BaseUrl.TrimEnd('/'));
            return new(new DatabaseContext(), null, null, oauth, malProgressConfig: Config)
                { MalProgressSyncTimeout = deadline ?? TimeSpan.FromSeconds(45) };
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _previous);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
