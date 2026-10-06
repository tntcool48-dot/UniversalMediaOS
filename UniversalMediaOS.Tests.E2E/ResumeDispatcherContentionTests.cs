using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E;

public sealed class ResumeDispatcherContentionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceChangesReturnPromptlyAndOnlyTheLatestUnitReceivesItsResume(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            var first = EpisodeContext(1);
            var latest = EpisodeContext(2);
            using (var seed = new DatabaseContext())
            {
                seed.SaveResumeState(catalog ? first.WorkKey : "42", catalog ? first.UnitKey! : "2", 180);
                seed.SaveResumeState(catalog ? latest.WorkKey : "42", catalog ? latest.UnitKey! : "3", 371);
            }
            player.PlaybackTime = 120_000;
            var elapsed = Stopwatch.StartNew();
            double maximumHeartbeatGap = 0;
            int heartbeatCount = 0;
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                var heartbeatClock = Stopwatch.StartNew();
                double previousTick = 0;
                var heartbeat = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(50) };
                heartbeat.Tick += (_, _) =>
                {
                    double now = heartbeatClock.Elapsed.TotalMilliseconds;
                    maximumHeartbeatGap = Math.Max(maximumHeartbeatGap, now - previousTick);
                    previousTick = now;
                    heartbeatCount++;
                };
                heartbeat.Start();
                elapsed.Restart();
                player.LoadEmbed("https://next.invalid/2", "Next unit", "2", malId: 42,
                    audiovisualContext: catalog ? first : null);
                player.LoadEmbed("https://next.invalid/3", "Latest unit", "3", malId: 42,
                    audiovisualContext: catalog ? latest : null);
                elapsed.Stop();
                try
                {
                    Assert.False(player.ResumeLoadCompleted.IsCompleted);
                    PumpUntil(() => heartbeatClock.Elapsed >= TimeSpan.FromSeconds(3));
                    Assert.False(player.TryConsumePendingWebResumePosition(out _));
                }
                finally { heartbeat.Stop(); }
            }
            output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} two source changes returned in {elapsed.Elapsed.TotalMilliseconds:0.000} ms.");
            double restored = 0;
            PumpUntil(() => player.TryConsumePendingWebResumePosition(out restored));
            output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} two source changes returned in {elapsed.Elapsed.TotalMilliseconds:0.000} ms; latest unit restored {restored} seconds.");
            Assert.Equal(371, restored);
            AssertPersisted(work, unit, 120);
            using var verify = new DatabaseContext();
            Assert.Equal(180, verify.GetResumeState(catalog ? first.WorkKey : "42", catalog ? first.UnitKey! : "2"));
            Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 0, 500);
            output.WriteLine($"Three-second writer heartbeat: {heartbeatCount} ticks, maximum gap {maximumHeartbeatGap:0.000} ms.");
            Assert.True(heartbeatCount >= 30);
            Assert.InRange(maximumHeartbeatGap, 0, 100);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitSeekWinsOverAPendingUnitResume(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            var next = EpisodeContext(1);
            using (var seed = new DatabaseContext())
                seed.SaveResumeState(catalog ? next.WorkKey : "42", catalog ? next.UnitKey! : "2", 180);
            player.PlaybackTime = 120_000;
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                player.LoadEmbed("https://next.invalid/2", "Next unit", "2", malId: 42,
                    audiovisualContext: catalog ? next : null);
                player.ReportWebPlaybackProgress(0, 600, false, paused: true);
                player.BeginUserSeek();
                player.CommitUserSeek(123_000);
                player.ReportWebPlaybackAction("seek", 123, 600);
                player.SetTabActive(false); // Pause captures the explicitly chosen position.
                Assert.False(player.ResumeLoadCompleted.IsCompleted);
            }
            WaitForResumeLoad(player);
            Assert.False(player.TryConsumePendingWebResumePosition(out _));
            Assert.Equal(123_000, player.PlaybackTime);
            AssertPersisted(work, unit, 120);
            AssertPersisted(catalog ? next.WorkKey : "42", catalog ? next.UnitKey! : "2", 123);
        });
    }

    [Fact]
    public async Task QueuedCatalogLoadKeepsItsCapturedProfileAndFollowsAcceptedProgress()
    {
        using var profile = new IsolatedProfile();
        var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
        var session = progress.Open(Context()).Session;
        using (var seed = new DatabaseContext())
        {
            profile.ConnectionString = seed.Database.GetConnectionString()!;
            seed.SaveResumeState(session.Context.WorkKey, session.Context.UnitKey!, 90);
        }
        Task save;
        Task<(PlaybackProgressSession Session, double Position)> read;
        using (var writer = new WriterLock(profile.ConnectionString!, session.Context.WorkKey, session.Context.UnitKey!, 5000))
        {
            save = progress.SaveAsync(progress.Capture(session, 120, 600, false)!);
            read = progress.OpenAsync(session.Context);
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, Path.Combine(profile.Root, "other-profile"));
        }
        await save;
        Assert.Equal(120, (await read).Position);
        Assert.False(Directory.Exists(Path.Combine(profile.Root, "other-profile")));
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, profile.Root);
        AssertPersisted(session.Context.WorkKey, session.Context.UnitKey!, 120);
    }

    private static AudiovisualPlaybackContext EpisodeContext(int season) => new("av:fixture:show:load-contention",
        new() { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series,
            Title = "Load contention fixture" }, new() { SeasonNumber = season, EpisodeNumber = 1 },
        "Load contention fixture", "", new());

    internal static void WaitForResumeLoad(PlaybackViewModel player)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            PumpUntil(() => player.ResumeLoadCompleted.IsCompleted);
        player.ResumeLoadCompleted.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var deadline = Stopwatch.StartNew();
        bool success;
        while (!(success = condition()) && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Assert.True(success, "The current unit must receive its delayed resume within the deadline.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PausingAnInactiveTabDoesNotWaitForASqliteWriter(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            player.PlaybackTime = 120_000;
            var elapsed = Stopwatch.StartNew();
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 1500))
            {
                elapsed.Restart();
                player.SetTabActive(false);
                elapsed.Stop();
            }
            output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} inactive pause returned in {elapsed.Elapsed.TotalMilliseconds:0.000} ms.");
            Assert.False(player.IsPlaying);
            AssertPersisted(work, unit, 120);
            Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 0, 500);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplicationFlushIncludesAnAlreadyClosedPlayersAcceptedProgress(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
            using var player = new PlaybackViewModel(new DatabaseContext(), null, null, playbackProgress: progress);
            player.LoadEmbed("https://fixture.invalid/close", "Closed player", "1", malId: 42,
                audiovisualContext: catalog ? Context() : null);
            var (work, unit) = SaveInitial(player, profile);
            using (var seed = new DatabaseContext()) seed.SaveResumeState(work, "adjacent-unit", 57);
            player.PlaybackTime = 120_000;
            Task flush;
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                player.Dispose(); // Its own two-second wait expires; the tab has gone.
                var elapsed = Stopwatch.StartNew();
                flush = progress.FlushAsync();
                elapsed.Stop();
                Assert.False(flush.IsCompleted);
                Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 0, 500);
            }
            PumpUntil(() => flush.IsCompleted);
            flush.GetAwaiter().GetResult();
            AssertPersisted(work, unit, 120);
            using var verify = new DatabaseContext();
            Assert.Equal(57, verify.GetResumeState(work, "adjacent-unit"));
        });
    }

    [Fact]
    public async Task ACloseWaitDeadlineDoesNotCancelAcceptedPersistence()
    {
        using var profile = new IsolatedProfile();
        var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        progress.TrackPendingPersistence(accepted.Task);
        Task flush = progress.FlushAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => flush.WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.False(accepted.Task.IsCompleted);
        accepted.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CloseIsBoundedAndCapturedProgressSurvivesDeferredDatabaseDisposal(bool catalog, bool completed)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog, out var ownedDatabase);
            var (work, unit) = SaveInitial(player, profile);
            using (var seed = new DatabaseContext()) seed.SaveResumeState(work, "adjacent-unit", 57);
            player.PlaybackTime = 120_000;
            var elapsed = Stopwatch.StartNew();
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                // Hold the writer beyond the two-second final flush deadline.
                elapsed.Restart();
                if (completed) player.ReportWebPlaybackProgress(600, 600, true, paused: true);
                player.Dispose();
                elapsed.Stop();
                Assert.True(player.IsDisposed);
                Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 1500, 3000);
                Assert.False(ownedDatabase.Disposed, "Accepted saves must retain the database while the writer remains locked.");
            }
            output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} {(completed ? "completion/close" : "close")} returned in {elapsed.Elapsed.TotalMilliseconds:0.000} ms.");
            AssertPersisted(work, unit, completed ? 0 : 120);
            Assert.True(SpinWait.SpinUntil(() => ownedDatabase.Disposed, TimeSpan.FromSeconds(5)),
                "The retained database must be disposed after every accepted save ends.");
            using var verify = new DatabaseContext();
            Assert.Equal(57, verify.GetResumeState(work, "adjacent-unit"));
        });
    }

    [Fact]
    public async Task QueuedCatalogSaveKeepsTheProfileCapturedBeforeTheDataRootChanges()
    {
        using var profile = new IsolatedProfile();
        var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
        var session = progress.Open(Context()).Session;
        using (var database = new DatabaseContext())
        {
            profile.ConnectionString = database.Database.GetConnectionString()!;
            database.SaveResumeState(session.Context.WorkKey, session.Context.UnitKey!, 90);
        }
        Task first;
        Task queued;
        using (var writer = new WriterLock(profile.ConnectionString!, session.Context.WorkKey, session.Context.UnitKey!, 5000))
        {
            first = progress.SaveAsync(progress.Capture(session, 120, 600, false)!);
            queued = progress.SaveAsync(progress.Capture(session, 180, 600, false)!);
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, Path.Combine(profile.Root, "other-profile"));
        }
        await Task.WhenAll(first, queued);
        Assert.False(Directory.Exists(Path.Combine(profile.Root, "other-profile")), "Workers must use the captured connection, not create another profile.");
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, profile.Root);
        AssertPersisted(session.Context.WorkKey, session.Context.UnitKey!, 180);
    }

    private static PlaybackViewModel CreatePlayer(IsolatedProfile profile, bool catalog)
        => CreatePlayer(profile, catalog, out _);

    private static PlaybackViewModel CreatePlayer(IsolatedProfile profile, bool catalog, out TrackingDatabaseContext database)
    {
        var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
        database = new TrackingDatabaseContext();
        var player = new PlaybackViewModel(database, null, null, playbackProgress: progress);
        player.LoadEmbed("https://fixture.invalid/embed", "Contention fixture", audiovisualContext: catalog ? Context() : null);
        return player;
    }

    private static AudiovisualPlaybackContext Context() => new("av:fixture:film:contention",
        new AudiovisualIdentity { PrimaryId = new("fixture", "film", "contention"),
            Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature,
            Title = "Contention fixture" }, new(), "Contention fixture", "", new());

    private static (string Work, string Unit) SaveInitial(PlaybackViewModel player, IsolatedProfile profile)
    {
        PumpUntil(() => player.ResumeLoadCompleted.IsCompleted);
        player.ResumeLoadCompleted.GetAwaiter().GetResult();
        player.ReportWebPlaybackProgress(90, 600, false, paused: true);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var database = new DatabaseContext();
            return database.ResumeStates.AsNoTracking().Any(row => row.PositionSeconds == 90);
        }, TimeSpan.FromSeconds(5)), "Arrange waits for the asynchronous initial save.");
        using var initialDatabase = new DatabaseContext();
        profile.ConnectionString = initialDatabase.Database.GetConnectionString()!;
        var initial = Assert.Single(initialDatabase.ResumeStates.AsNoTracking().ToArray());
        Assert.Equal(90, initial.PositionSeconds);
        return (initial.MediaId, initial.EpisodeId);
    }

    private static void AssertPersisted(string work, string unit, double expected) =>
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var database = new DatabaseContext();
            return database.GetResumeState(work, unit) == expected;
        }, TimeSpan.FromSeconds(5)), $"Queued exact-unit position {expected} must persist after the writer releases.");

    internal sealed class WriterLock : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Task _writer;

        public WriterLock(string connectionString, string work, string unit, int maximumHoldMilliseconds)
        {
            using var locked = new ManualResetEventSlim();
            _writer = Task.Run(() =>
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE ResumeStates SET PositionSeconds = PositionSeconds WHERE MediaId = $work AND EpisodeId = $unit";
                command.Parameters.AddWithValue("$work", work);
                command.Parameters.AddWithValue("$unit", unit);
                Assert.Equal(1, command.ExecuteNonQuery());
                locked.Set();
                _release.Wait(TimeSpan.FromMilliseconds(maximumHoldMilliseconds));
                transaction.Commit();
            });
            Assert.True(locked.Wait(TimeSpan.FromSeconds(3)), "Fixture writer must acquire the SQLite lock before the timed UI call.");
        }

        public void Dispose()
        {
            _release.Set();
            try { _writer.GetAwaiter().GetResult(); }
            finally { _release.Dispose(); }
        }
    }

    private sealed class IsolatedProfile : IDisposable
    {
        private readonly string? _previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        private readonly string _tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests"));
        public string Root { get; }
        public string? ConnectionString { get; set; }

        public IsolatedProfile()
        {
            Root = Path.Combine(_tempRoot, "resume-contention-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, Root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _previousRoot);
            if (ConnectionString != null)
            {
                using var ownPool = new SqliteConnection(ConnectionString);
                SqliteConnection.ClearPool(ownPool);
            }
            var ownedDirectory = new DirectoryInfo(Root);
            Assert.StartsWith(_tempRoot + Path.DirectorySeparatorChar, ownedDirectory.FullName, StringComparison.OrdinalIgnoreCase);
            Assert.Null(ownedDirectory.LinkTarget);
            if (ownedDirectory.Exists) ownedDirectory.Delete(recursive: true);
        }
    }

    private sealed class TrackingDatabaseContext : DatabaseContext
    {
        private int _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public override void Dispose()
        {
            base.Dispose();
            Volatile.Write(ref _disposed, 1);
        }
    }
}
