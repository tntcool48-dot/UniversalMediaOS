using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    public void AcceptedUnitChangesStayBoundedAndRetainLatestRequestBehindALockedWriter(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            const string nextWork = "av:fixture:show:accepted-units";
            string targetWork = catalog ? nextWork : "42";
            string Unit(int episode) => catalog ? $"season:1:episode:{episode}" : episode.ToString();
            using (var seed = new DatabaseContext())
            {
                seed.SaveResumeState(targetWork, Unit(200), 371);
                seed.SaveResumeState(work, "adjacent-unit", 57);
            }
            var accepted = new Dictionary<int, double>();
            int maximumPending = 0;
            player.PlaybackTime = 120_000;
            var elapsed = Stopwatch.StartNew();
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                elapsed.Restart();
                for (int episode = 1; episode <= 200; episode++)
                {
                    player.LoadEmbed("https://fixture.invalid/accepted/" + episode, "Accepted unit fixture",
                        episode.ToString(), malId: 42, audiovisualContext: catalog ? new(nextWork,
                            new() { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series },
                            new() { SeasonNumber = 1, EpisodeNumber = episode }, "Accepted unit fixture", "", new()) : null);
                    // A source which has not been admitted owns no playback yet.
                    // Its latest request is retained while the current unit stays paused.
                    if (episode < 200 && !player.IsPersistenceWaiting)
                    {
                        accepted[episode] = 120 + episode;
                        player.PlaybackTime = (120 + episode) * 1000;
                        player.SetTabActive(false);
                        player.SetTabActive(true);
                    }
                    maximumPending = Math.Max(maximumPending,
                        player.PendingResumeLoadCount + player.PendingResumeSaveCount);
                }
                elapsed.Stop();
                Assert.True(player.IsPersistenceWaiting);
                double heldPosition = player.PlaybackTime;
                player.BeginUserSeek();
                player.CommitUserSeek(999_000);
                player.SkipForwardCommand.Execute(null);
                player.TogglePlayPauseCommand.Execute(null);
                Assert.Equal(heldPosition, player.PlaybackTime);
                Assert.False(player.IsPersistenceControlsAvailable);
            }
            WaitForResumeLoad(player);
            Assert.True(player.TryConsumePendingWebResumePosition(out double restored));
            Assert.Equal(371, restored);
            using var verify = new DatabaseContext();
            foreach (var (episode, position) in accepted)
                Assert.Equal(position, verify.GetResumeState(targetWork, Unit(episode)));
            Assert.Equal(120, verify.GetResumeState(work, unit));
            Assert.Equal(57, verify.GetResumeState(work, "adjacent-unit"));
            output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} 200 requested units: {accepted.Count} admitted units, {maximumPending} maximum pending operations, {elapsed.Elapsed.TotalMilliseconds:0.000} ms.");
            Assert.InRange(maximumPending, 1, 32);
            Assert.InRange(accepted.Count, 1, 31);
            Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 0, 500);
        });
    }

    [Fact]
    public void SharedAdmissionBoundsUnitBurstsAcrossIndependentPlayersAndKeepsEveryAcceptedPosition()
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            const int playerCount = 20;
            const string sharedWork = "av:fixture:show:shared-admission";
            var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
            var players = new List<PlaybackViewModel>();
            var accepted = new Dictionary<(string Work, string Unit), double>();
            bool Catalog(int index) => index % 2 == 0;
            string Work(int index) => Catalog(index) ? sharedWork : (4200 + index).ToString();
            AudiovisualPlaybackContext Episode(int episode) => new(sharedWork,
                new() { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series },
                new() { SeasonNumber = 1, EpisodeNumber = episode }, "Shared admission fixture", "", new());
            string Unit(int index, int episode) => Catalog(index) ? $"season:1:episode:{episode}" : episode.ToString();
            try
            {
                for (int index = 0; index < playerCount; index++)
                {
                    var database = new TrackingDatabaseContext();
                    profile.Track(progress, database);
                    var player = new PlaybackViewModel(database, null, null, playbackProgress: progress);
                    players.Add(player);
                    player.LoadEmbed("https://fixture.invalid/shared/" + index, "Shared initial unit",
                        (index + 1).ToString(), malId: 4200 + index,
                        audiovisualContext: Catalog(index) ? Episode(index + 1) : null);
                    WaitForResumeLoad(player);
                    player.ReportWebPlaybackProgress(90, 600, false, paused: true);
                }
                progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                using (var seed = new DatabaseContext())
                {
                    profile.ConnectionString = seed.Database.GetConnectionString()!;
                    for (int index = 0; index < playerCount; index++)
                        seed.SaveResumeState(Work(index), Unit(index, (index + 1) * 1000 + 200), 371 + index);
                    seed.SaveResumeState(sharedWork, "adjacent-unit", 57);
                }
                int maximumPending = 0;
                var elapsed = Stopwatch.StartNew();
                using (var writer = new WriterLock(profile.ConnectionString!, sharedWork, Unit(0, 1), 5000))
                {
                    elapsed.Restart();
                    for (int round = 1; round <= 200; round++)
                    {
                        for (int index = 0; index < playerCount; index++)
                        {
                            var player = players[index];
                            int episode = (index + 1) * 1000 + round;
                            player.LoadEmbed("https://fixture.invalid/shared/" + episode, "Shared requested unit",
                                episode.ToString(), malId: 4200 + index,
                                audiovisualContext: Catalog(index) ? Episode(episode) : null);
                            if (!player.IsPersistenceWaiting && round < 200)
                            {
                                double position = 120 + round + index;
                                accepted[(Work(index), Unit(index, episode))] = position;
                                player.PlaybackTime = position * 1000;
                                player.SetTabActive(false);
                                player.SetTabActive(true);
                            }
                            maximumPending = Math.Max(maximumPending, progress.PendingPersistenceCount);
                        }
                    }
                    elapsed.Stop();
                    Assert.Contains(players, player => player.IsPersistenceWaiting);
                }
                foreach (var player in players) WaitForResumeLoad(player);
                using var verify = new DatabaseContext();
                foreach (var (key, position) in accepted)
                    Assert.Equal(position, verify.GetResumeState(key.Work, key.Unit));
                for (int index = 0; index < playerCount; index++)
                {
                    Assert.True(players[index].TryConsumePendingWebResumePosition(out double restored));
                    Assert.Equal(371 + index, restored);
                    Assert.False(players[index].IsPersistenceWaiting);
                }
                Assert.Equal(57, verify.GetResumeState(sharedWork, "adjacent-unit"));
                output.WriteLine($"20 mixed catalog/legacy players / 4,000 requested units: {accepted.Count} admitted units, {maximumPending} maximum shared pending operations, {elapsed.Elapsed.TotalMilliseconds:0.000} ms.");
                // Every live producer retains two slots for final observations;
                // admission backpressure must not drop those accepted saves.
                Assert.InRange(maximumPending, 1, PlaybackProgressService.PersistenceAdmissionThreshold + 2 * playerCount);
                Assert.InRange(accepted.Count, 1, 100);
                Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 0, 1000);
            }
            finally { foreach (var player in players) player.Dispose(); }
        });
    }

    [Fact]
    public void RepeatedExplicitOwnershipChangesUseBackpressureAndRetainTheFinalAcceptedPosition()
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
            var database = new TrackingDatabaseContext();
            profile.Track(progress, database);
            using var player = new PlaybackViewModel(database, null, null, playbackProgress: progress);
            player.LoadEmbed("https://fixture.invalid/ownership", "Ownership fixture", audiovisualContext: Context());
            var (work, unit) = SaveInitial(player, profile);
            var barrier = progress.Open(Context()).Session;
            int maximumPending = 0;
            double acceptedPosition;
            int playCommands = 0;
            player.WebPlaybackCommandRequested += (_, request) =>
            {
                if (request.Action == "play") playCommands++;
            };
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                // Reserve the ordering gate with real SQLite IO before the burst.
                // Otherwise superseded observations may all skip before any
                // write starts, legitimately never reaching the admission limit.
                Task blocked = progress.SaveAsync(progress.Capture(barrier, 90, 600, false)!);
                progress.TrackPendingPersistence(blocked);
                for (int index = 0; index < 200; index++)
                {
                    player.ReportWebPlaybackAction("play", 120 + index, 600);
                    player.ReportWebPlaybackAction("pause", 120 + index, 600);
                    maximumPending = Math.Max(maximumPending,
                        player.PendingResumeLoadCount + player.PendingResumeSaveCount);
                }
                acceptedPosition = player.PlaybackTime / 1000;
                Assert.True(player.IsPersistenceWaiting);
            }
            WaitForResumeLoad(player);
            AssertPersisted(work, unit, acceptedPosition);
            Assert.True(playCommands > 0);
            Assert.False(player.IsPersistenceWaiting);
            output.WriteLine($"200 explicit ownership changes: {maximumPending} maximum pending operations, final accepted position {acceptedPosition} seconds.");
            Assert.InRange(maximumPending, 1, 32);
        });
    }

    [Fact]
    public void ClosingBeforeAdmissionCancelsOnlyTheUnacceptedSourceAndKeepsAcceptedProgress()
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, true);
            var (work, unit) = SaveInitial(player, profile);
            var accepted = new Dictionary<int, double>();
            const string nextWork = "av:fixture:show:close-admission";
            using (var seed = new DatabaseContext()) seed.SaveResumeState(nextWork, "season:1:episode:200", 371);
            player.PlaybackTime = 120_000;
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                for (int episode = 1; episode <= 200; episode++)
                {
                    player.LoadEmbed("https://fixture.invalid/close-admission/" + episode, "Close admission fixture",
                        episode.ToString(), audiovisualContext: new(nextWork,
                            new() { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series },
                            new() { SeasonNumber = 1, EpisodeNumber = episode }, "Close admission fixture", "", new()));
                    if (!player.IsPersistenceWaiting)
                    {
                        accepted[episode] = 120 + episode;
                        player.PlaybackTime = (120 + episode) * 1000;
                        player.SetTabActive(false);
                        player.SetTabActive(true);
                    }
                }
                Assert.True(player.IsPersistenceWaiting);
                Task admission = player.ResumeLoadCompleted;
                player.Dispose();
                Assert.True(admission.IsCanceled);
                Assert.True(player.IsDisposed);
            }
            foreach (var (episode, position) in accepted)
                AssertPersisted(nextWork, $"season:1:episode:{episode}", position);
            AssertPersisted(work, unit, 120);
            using var verify = new DatabaseContext();
            Assert.Equal(371, verify.GetResumeState(nextWork, "season:1:episode:200"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupersededUnsavedUnitReadsDoNotAccumulateBehindALockedWriter(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            const string nextWork = "av:fixture:show:rapid-units";
            string latestWork = catalog ? nextWork : "42";
            string latestUnit = catalog ? "season:1:episode:200" : "200";
            using (var seed = new DatabaseContext()) seed.SaveResumeState(latestWork, latestUnit, 371);
            player.PlaybackTime = 120_000;
            int pending;
            var elapsed = Stopwatch.StartNew();
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                for (int episode = 1; episode <= 200; episode++)
                {
                    player.LoadEmbed("https://fixture.invalid/" + episode, "Rapid unit fixture", episode.ToString(), malId: 42,
                        audiovisualContext: catalog ? new(nextWork,
                            new() { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series },
                            new() { SeasonNumber = 1, EpisodeNumber = episode }, "Rapid unit fixture", "", new()) : null);
                }
                elapsed.Stop();
                var settle = Stopwatch.StartNew();
                PumpUntil(() => settle.Elapsed >= TimeSpan.FromMilliseconds(250));
                pending = player.PendingResumeLoadCount;
                Assert.False(player.ResumeLoadCompleted.IsCompleted);
                output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} 200 unsaved source changes: {pending} pending reads, {elapsed.Elapsed.TotalMilliseconds:0.000} ms.");
            }
            // Drain actual operations before asserting the bound, so the failing
            // baseline still safely releases its owned profile and database.
            WaitForResumeLoad(player);
            Assert.True(player.TryConsumePendingWebResumePosition(out double restored));
            Assert.Equal(371, restored);
            AssertPersisted(work, unit, 120);
            Assert.InRange(pending, 1, 2);
            Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 0, 500);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedPausesKeepAtMostTwoAcceptedWritesForTheCurrentUnitWhileLocked(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            using (var seed = new DatabaseContext()) seed.SaveResumeState(work, "adjacent-unit", 57);
            int maximumPending = 0;
            var elapsed = Stopwatch.StartNew();
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                for (int index = 0; index < 200; index++)
                {
                    player.PlaybackTime = (120 + index) * 1000;
                    player.SetTabActive(false);
                    player.SetTabActive(true);
                    maximumPending = Math.Max(maximumPending, player.PendingResumeSaveCount);
                }
                elapsed.Stop();
                output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} 200 pauses: {maximumPending} pending saves, {elapsed.Elapsed.TotalMilliseconds:0.000} ms.");
                Assert.InRange(maximumPending, 1, 2);
                Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 0, 500);
            }
            AssertPersisted(work, unit, 319);
            using var verify = new DatabaseContext();
            Assert.Equal(57, verify.GetResumeState(work, "adjacent-unit"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoalescedPausesDoNotMoveAcrossAnotherUnitsRead(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            var next = EpisodeContext(1);
            string nextWork = catalog ? next.WorkKey : "42";
            string nextUnit = catalog ? next.UnitKey! : "2";
            using (var seed = new DatabaseContext()) seed.SaveResumeState(nextWork, nextUnit, 180);
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                for (int index = 0; index < 200; index++)
                {
                    player.PlaybackTime = (120 + index) * 1000;
                    player.SetTabActive(false);
                    player.SetTabActive(true);
                }
                player.LoadEmbed("https://next.invalid/2", "Next unit", "2", malId: 42,
                    audiovisualContext: catalog ? next : null);
                player.BeginUserSeek();
                player.CommitUserSeek(123_000);
                player.ReportWebPlaybackAction("seek", 123, 600);
                player.SetTabActive(false);
                player.LoadEmbed(catalog ? "https://returned.invalid/embed" : "https://fixture.invalid/embed", "Contention fixture",
                    audiovisualContext: catalog ? Context() : null);
                Assert.False(player.ResumeLoadCompleted.IsCompleted);
            }
            WaitForResumeLoad(player);
            Assert.True(player.TryConsumePendingWebResumePosition(out double restored));
            Assert.Equal(319, restored);
            AssertPersisted(work, unit, 319);
            AssertPersisted(nextWork, nextUnit, 123);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionReplacesQueuedPausesAndSurvivesLateProgressAndClose(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            using (var seed = new DatabaseContext()) seed.SaveResumeState(work, "adjacent-unit", 57);
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                for (int index = 0; index < 200; index++)
                {
                    player.PlaybackTime = (120 + index) * 1000;
                    player.SetTabActive(false);
                    player.SetTabActive(true);
                }
                player.ReportWebPlaybackProgress(600, 600, true, paused: true);
                player.ReportWebPlaybackProgress(200, 600, false, paused: true);
                Assert.InRange(player.PendingResumeSaveCount, 0, 2);
                player.Dispose();
            }
            AssertPersisted(work, unit, 0);
            using var verify = new DatabaseContext();
            Assert.Equal(57, verify.GetResumeState(work, "adjacent-unit"));
        });
    }

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

    internal static void WaitForResumeLoad(PlaybackViewModel player, Func<string>? databaseDetails = null)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            PumpUntil(() => player.ResumeLoadCompleted.IsCompleted, () => ResumeLoadDetails(player) +
                (databaseDetails == null ? string.Empty : "\n" + databaseDetails()));
        player.ResumeLoadCompleted.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    private static string ResumeLoadDetails(PlaybackViewModel player)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var read = typeof(PlaybackViewModel).GetField("_resumeLoadRead", flags)?.GetValue(player) as Task;
        var gate = typeof(PlaybackViewModel).GetField("_resumeDatabaseLock", flags)?.GetValue(player) as SemaphoreSlim;
        var admission = typeof(PlaybackViewModel).GetField("_resumeAdmissionCompleted", flags)?.GetValue(player) as TaskCompletionSource;
        var admissionWorker = typeof(PlaybackViewModel).GetField("_resumeAdmissionWorker", flags)?.GetValue(player) as Task;
        var session = typeof(PlaybackViewModel).GetField("_progressSessionReady", flags)?.GetValue(player) as Task;
        var deferred = typeof(PlaybackViewModel).GetField("_deferredResumeRequest", flags)?.GetValue(player);
        var progress = typeof(PlaybackViewModel).GetField("_playbackProgress", flags)?.GetValue(player) as PlaybackProgressService;
        var sharedGate = progress == null ? null :
            typeof(PlaybackProgressService).GetField("_gate", flags)?.GetValue(progress) as SemaphoreSlim;
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        ThreadPool.GetAvailableThreads(out int workers, out int completionPorts);
        return $"read={read?.Status}; completion={player.ResumeLoadCompleted.Status}; " +
            $"completion is admission={ReferenceEquals(player.ResumeLoadCompleted, admission?.Task)}; " +
            $"waiting admission={player.IsPersistenceWaiting}; deferred source={deferred != null}; " +
            $"admission worker={admissionWorker?.Status}; session={session?.Status}; " +
            $"pending reads={player.PendingResumeLoadCount}; pending saves={player.PendingResumeSaveCount}; " +
            $"shared pending={progress?.PendingPersistenceCount}; shared database gate={sharedGate?.CurrentCount}; " +
            $"legacy database gate={gate?.CurrentCount}; disposed={player.IsDisposed}; " +
            $"dispatcher thread={dispatcher.Thread.ManagedThreadId}, alive={dispatcher.Thread.IsAlive}, shutdown={dispatcher.HasShutdownStarted}; " +
            $"pumping thread={Thread.CurrentThread.ManagedThreadId}; available workers={workers}, completion ports={completionPorts}; " +
            $"worker threads={ThreadPool.ThreadCount}, queued work={ThreadPool.PendingWorkItemCount}.";
    }

    private static void PumpUntil(Func<bool> condition, Func<string>? failureDetails = null)
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
        Assert.True(success, "The current unit must receive its delayed resume within the deadline." +
            (success || failureDetails == null ? string.Empty : "\n" + failureDetails()));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionEndingCapturesAndDrainsExactProgressWithoutPumpingTheDispatcher(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
            var database = new TrackingDatabaseContext();
            profile.Track(progress, database);
            using var player = new PlaybackViewModel(database, null, null, playbackProgress: progress);
            player.LoadEmbed("https://fixture.invalid/session-end", "Session-end fixture", "1", malId: 42,
                audiovisualContext: catalog ? Context() : null);
            var (work, unit) = SaveInitial(player, profile);
            using (var seed = new DatabaseContext()) seed.SaveResumeState(work, "adjacent-unit", 57);
            player.PlaybackTime = 120_000;
            int dispatcherThread = Environment.CurrentManagedThreadId;
            var elapsed = Stopwatch.StartNew();
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 300))
            {
                elapsed.Restart();
                Assert.True(UniversalMediaOS.WPF.App.DrainPlaybackProgressForSessionEnding(() =>
                {
                    Assert.Equal(dispatcherThread, Environment.CurrentManagedThreadId);
                    return player.FlushResumeForSessionEndingAsync();
                }, progress, TimeSpan.FromSeconds(3)));
                elapsed.Stop();
                Assert.False(player.IsPlaying);
                Assert.False(database.Disposed);
            }
            output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} session-end drain: {elapsed.Elapsed.TotalMilliseconds:0.000} ms, no dispatcher pump.");
            using var verify = new DatabaseContext();
            Assert.Equal(120, verify.GetResumeState(work, unit));
            Assert.Equal(57, verify.GetResumeState(work, "adjacent-unit"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionEndingDeadlineDoesNotAddPerPlayerCloseWaitOrCancelAcceptedProgress(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
            var database = new TrackingDatabaseContext();
            profile.Track(progress, database);
            using var player = new PlaybackViewModel(database, null, null, playbackProgress: progress);
            player.LoadEmbed("https://fixture.invalid/session-end", "Session-end fixture", "1", malId: 42,
                audiovisualContext: catalog ? Context() : null);
            var (work, unit) = SaveInitial(player, profile);
            using (var seed = new DatabaseContext()) seed.SaveResumeState(work, "adjacent-unit", 57);
            player.PlaybackTime = 120_000;
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                var elapsed = Stopwatch.StartNew();
                Assert.False(UniversalMediaOS.WPF.App.DrainPlaybackProgressForSessionEnding(
                    player.FlushResumeForSessionEndingAsync, progress, TimeSpan.FromMilliseconds(100)));
                elapsed.Stop();
                Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 75, 1000);
                Assert.False(progress.FlushAsync().IsCompleted);
                elapsed.Restart();
                player.Dispose();
                elapsed.Stop();
                Assert.False(database.Disposed);
                Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 0, 500);
                output.WriteLine($"{(catalog ? "Catalog" : "Legacy")} post-deadline player teardown: {elapsed.Elapsed.TotalMilliseconds:0.000} ms.");
            }
            progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            using var verify = new DatabaseContext();
            Assert.Equal(120, verify.GetResumeState(work, unit));
            Assert.Equal(57, verify.GetResumeState(work, "adjacent-unit"));
        });
    }

    [Fact]
    public void SessionEndingDrainsAcceptedProgressFromAnAlreadyRemovedPlayer()
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
            var database = new TrackingDatabaseContext();
            profile.Track(progress, database);
            using var player = new PlaybackViewModel(database, null, null, playbackProgress: progress);
            player.LoadEmbed("https://fixture.invalid/removed", "Removed player", "1", malId: 42,
                audiovisualContext: Context());
            var (work, unit) = SaveInitial(player, profile);
            player.PlaybackTime = 120_000;
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 2500))
            {
                player.Dispose(); // The normal tab-close two-second wait expires.
                Assert.True(player.IsDisposed);
                Assert.False(progress.FlushAsync().IsCompleted);
                Assert.True(UniversalMediaOS.WPF.App.DrainPlaybackProgressForSessionEnding(
                    () => Task.CompletedTask, progress, TimeSpan.FromSeconds(3)));
            }
            using var verify = new DatabaseContext();
            Assert.Equal(120, verify.GetResumeState(work, unit));
        });
    }

    [Fact]
    public void SessionEndingWithoutPlaybackReturnsImmediately()
    {
        var elapsed = Stopwatch.StartNew();
        Assert.True(UniversalMediaOS.WPF.App.DrainPlaybackProgressForSessionEnding(
            () => Task.CompletedTask, null, TimeSpan.FromSeconds(3)));
        Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 0, 500);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionEndingDeadlineReportsJournaledProgressBehindALockedWriter(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
            var database = new TrackingDatabaseContext();
            profile.Track(progress, database);
            using var player = new PlaybackViewModel(database, null, null, playbackProgress: progress);
            player.LoadEmbed("https://fixture.invalid/session-end", "Session-end fixture", "1", malId: 42,
                audiovisualContext: catalog ? Context() : null);
            var (work, unit) = SaveInitial(player, profile);
            player.PlaybackTime = 120_000;
            string journal = JournalPath(profile);
            using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
            {
                Assert.False(UniversalMediaOS.WPF.App.DrainPlaybackProgressForSessionEnding(
                    player.FlushResumeForSessionEndingAsync, progress, TimeSpan.FromMilliseconds(100), out bool journaled));
                Assert.True(journaled);
                // The report matches the files: the journal holds the accepted position; SQLite does not yet.
                Assert.Equal(120, PendingPosition(journal, work, unit));
                Assert.Equal(90, ReadCommitted(profile.ConnectionString!, work, unit));
            }
            progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            AssertPersisted(work, unit, 120);
            Assert.Null(PendingPosition(journal, work, unit));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionEndingDeadlineDoesNotReportAnUnwrittenJournalAsJournaled(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
            var database = new TrackingDatabaseContext();
            profile.Track(progress, database);
            using var player = new PlaybackViewModel(database, null, null, playbackProgress: progress);
            player.LoadEmbed("https://fixture.invalid/session-end", "Session-end fixture", "1", malId: 42,
                audiovisualContext: catalog ? Context() : null);
            var (work, unit) = SaveInitial(player, profile);
            progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            player.PlaybackTime = 120_000;
            string journal = JournalPath(profile);
            // Another holder of the journal's lock delays the file write while SQLite stays free.
            using (new FileStream(journal + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(UniversalMediaOS.WPF.App.DrainPlaybackProgressForSessionEnding(
                    player.FlushResumeForSessionEndingAsync, progress, TimeSpan.FromMilliseconds(100), out bool journaled));
                Assert.False(journaled);
                Assert.Null(PendingPosition(journal, work, unit));
            }
            progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            AssertPersisted(work, unit, 120);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedPositionIsSavedDirectlyWhenTheJournalCannotBeWritten(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
            var database = new TrackingDatabaseContext();
            profile.Track(progress, database);
            using var player = new PlaybackViewModel(database, null, null, playbackProgress: progress);
            player.LoadEmbed("https://fixture.invalid/journal-unavailable", "Journal-unavailable fixture", "1", malId: 42,
                audiovisualContext: catalog ? Context() : null);
            var (work, unit) = SaveInitial(player, profile);
            progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            string journal = JournalPath(profile);
            // Held past the journal's three-second lock wait, as by another process: this position cannot be journaled.
            using (new FileStream(journal + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                player.PlaybackTime = 120_000;
                player.SetTabActive(false);
                progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Assert.Equal(120, ReadCommitted(profile.ConnectionString!, work, unit));
            }
            Assert.Null(PendingPosition(journal, work, unit));
        });
    }

    [Fact]
    public void SessionEndingDeadlineDoesNotReportADirectSaveAsJournaled()
    {
        using var profile = new IsolatedProfile();
        string name = "resume-direct-" + Guid.NewGuid().ToString("N");
        using var volatileStore = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
        volatileStore.Open(); // Keeps the shared in-memory database alive between the player's operations.
        RecoveryLayoutTests.RunSta(() =>
        {
            var progress = new PlaybackProgressService(new(Path.Combine(profile.Root, "library.json")));
            using var player = new PlaybackViewModel(new VolatileDatabaseContext(name), null, null, playbackProgress: progress);
            player.LoadEmbed("https://fixture.invalid/direct", "Direct-save fixture", "1", malId: 42);
            WaitForResumeLoad(player);
            // A volatile database has no journal, so this save is direct; hold the shared SQLite order.
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task holder = progress.RunSerializedPersistenceAsync(() => held.Task);
            player.PlaybackTime = 120_000;
            Assert.False(UniversalMediaOS.WPF.App.DrainPlaybackProgressForSessionEnding(
                player.FlushResumeForSessionEndingAsync, progress, TimeSpan.FromMilliseconds(100), out bool journaled));
            Assert.False(journaled);
            held.SetResult();
            holder.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        });
        using var command = volatileStore.CreateCommand();
        command.CommandText = "SELECT PositionSeconds FROM ResumeStates WHERE MediaId = '42' AND EpisodeId = '1'";
        Assert.Equal(120, Convert.ToDouble(command.ExecuteScalar()));
    }

    [Fact]
    public void ClosingAPlayerTabScopeKeepsItsDatabaseUntilAcceptedSavesFinish()
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var services = UniversalMediaOS.WPF.App.ConfigureServices();
            var progress = services.GetRequiredService<PlaybackProgressService>();
            // MainViewModel gives each player tab its own scope and disposes that scope when the tab closes.
            var tab = services.CreateScope();
            var player = tab.ServiceProvider.GetRequiredService<PlaybackViewModel>();
            var database = (DatabaseContext)typeof(PlaybackViewModel).GetField("_databaseContext",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(player)!;
            try
            {
                player.LoadEmbed("https://fixture.invalid/tab-scope", "Tab scope fixture", "1", malId: 42);
                var (work, unit) = SaveInitial(player, profile);
                player.PlaybackTime = 120_000;
                using (var writer = new WriterLock(profile.ConnectionString!, work, unit, 5000))
                {
                    tab.Dispose(); // The player's own two-second final-save wait expires behind the writer.
                    Assert.True(player.IsDisposed);
                    Assert.False(IsDisposed(database), "Closing the tab must not dispose the player's database while an accepted save waits.");
                }
                progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                AssertPersisted(work, unit, 120);
                Assert.True(SpinWait.SpinUntil(() => IsDisposed(database), TimeSpan.FromSeconds(5)),
                    "The player must dispose its database after its accepted saves finish.");
            }
            finally
            {
                // A failed assertion must not leave a commit holding the profile's database open.
                tab.Dispose();
                progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedJournaledCommitDoesNotHideTheNextUnitsSavedPosition(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            const string nextWork = "av:fixture:show:failed-commit";
            string targetWork = catalog ? nextWork : "42";
            string targetUnit = catalog ? "season:1:episode:2" : "2";
            using (var seed = new DatabaseContext()) seed.SaveResumeState(targetWork, targetUnit, 371);
            using (new FailingUnitTrigger(profile.ConnectionString!, work, unit))
            {
                // The accepted position reaches the journal; SQLite rejects its commit.
                player.PlaybackTime = 120_000;
                player.SetTabActive(false);
                player.SetTabActive(true);
                player.LoadEmbed("https://fixture.invalid/failed-commit/2", "Failed commit fixture", "2", malId: 42,
                    audiovisualContext: catalog ? FailureEpisode(nextWork, 2) : null);
                WaitForResumeLoad(player);
                Assert.True(player.TryConsumePendingWebResumePosition(out double restored),
                    "The next unit's read must not inherit the failed commit and restore zero.");
                Assert.Equal(371, restored);
                Assert.Equal(120, PendingPosition(JournalPath(profile), work, unit));
                Assert.Equal(90, ReadCommitted(profile.ConnectionString!, work, unit));
            }
            // Once SQLite accepts the write, the journaled position replays before the unit's next read.
            player.LoadEmbed("https://fixture.invalid/embed", "Contention fixture", audiovisualContext: catalog ? Context() : null);
            WaitForResumeLoad(player);
            Assert.True(player.TryConsumePendingWebResumePosition(out double replayed));
            Assert.Equal(120, replayed);
            AssertPersisted(work, unit, 120);
            Assert.Equal(371, ReadCommitted(profile.ConnectionString!, targetWork, targetUnit));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeferredSourceIsAdmittedAfterAnAcceptedCommitFails(bool catalog)
    {
        using var profile = new IsolatedProfile();
        RecoveryLayoutTests.RunSta(() =>
        {
            using var player = CreatePlayer(profile, catalog);
            var (work, unit) = SaveInitial(player, profile);
            const string nextWork = "av:fixture:show:failed-admission";
            string targetWork = catalog ? nextWork : "42";
            string Unit(int episode) => catalog ? $"season:1:episode:{episode}" : episode.ToString();
            using (var seed = new DatabaseContext())
            {
                seed.SaveResumeState(targetWork, Unit(200), 371);
                seed.SaveResumeState(work, "adjacent-unit", 57);
            }
            using var trigger = new FailingUnitTrigger(profile.ConnectionString!, work, unit);
            player.PlaybackTime = 120_000;
            using (var writer = new WriterLock(profile.ConnectionString!, work, "adjacent-unit", 5000))
            {
                for (int episode = 1; episode <= 200; episode++)
                {
                    player.LoadEmbed("https://fixture.invalid/failed-admission/" + episode, "Failed admission fixture",
                        episode.ToString(), malId: 42, audiovisualContext: catalog ? FailureEpisode(nextWork, episode) : null);
                    if (episode < 200 && !player.IsPersistenceWaiting)
                    {
                        player.PlaybackTime = (120 + episode) * 1000;
                        player.SetTabActive(false);
                        player.SetTabActive(true);
                    }
                }
                Assert.True(player.IsPersistenceWaiting);
            }
            // The first unit's commit fails after the writer releases. The latest source must still be admitted.
            WaitForResumeLoad(player);
            Assert.False(player.IsPersistenceWaiting);
            Assert.True(player.TryConsumePendingWebResumePosition(out double restored));
            Assert.Equal(371, restored);
            Assert.Equal(120, PendingPosition(JournalPath(profile), work, unit));
            Assert.Equal(90, ReadCommitted(profile.ConnectionString!, work, unit));
            Assert.Equal(57, ReadCommitted(profile.ConnectionString!, work, "adjacent-unit"));
        });
    }

    private static AudiovisualPlaybackContext FailureEpisode(string work, int episode) => new(work,
        new() { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series },
        new() { SeasonNumber = 1, EpisodeNumber = episode }, "Failed commit fixture", "", new());

    /// <summary>Real SQLite rejects every update of one unit, as a failing commit would.</summary>
    private sealed class FailingUnitTrigger : IDisposable
    {
        private readonly string _connectionString;

        public FailingUnitTrigger(string connectionString, string work, string unit)
        {
            _connectionString = new SqliteConnectionStringBuilder(connectionString)
                { Cache = SqliteCacheMode.Private, Pooling = false }.ToString();
            static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
            Execute("CREATE TRIGGER fixture_failing_unit BEFORE UPDATE ON ResumeStates " +
                $"WHEN OLD.MediaId = {Literal(work)} AND OLD.EpisodeId = {Literal(unit)} " +
                "BEGIN SELECT RAISE(ABORT, 'fixture commit failure'); END;");
        }

        public void Dispose() => Execute("DROP TRIGGER IF EXISTS fixture_failing_unit;");

        private void Execute(string sql)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static string JournalPath(IsolatedProfile profile) => DurableResumeJournal.JournalPath(
        Path.GetFullPath(new SqliteConnectionStringBuilder(profile.ConnectionString!).DataSource));

    /// <summary>The journaled position of one unit, or null. A live app replaces the file atomically, so a
    /// read racing that replacement also returns null; poll when another process is writing.</summary>
    internal static double? PendingPosition(string journal, string work, string unit)
    {
        try
        {
            if (!File.Exists(journal)) return null;
            using var stream = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            foreach (var pending in document.RootElement.GetProperty("Pending").EnumerateArray())
            {
                if (pending.GetProperty("WorkKey").GetString() == work && pending.GetProperty("UnitKey").GetString() == unit)
                    return pending.GetProperty("Position").GetDouble();
            }
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException) { }
        return null;
    }

    /// <summary>A private-cache WAL reader sees the last committed row beside a held writer.</summary>
    internal static double ReadCommitted(string connectionString, string work, string unit)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString)
            { Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PositionSeconds FROM ResumeStates WHERE MediaId = $work AND EpisodeId = $unit";
        command.Parameters.AddWithValue("$work", work);
        command.Parameters.AddWithValue("$unit", unit);
        return Convert.ToDouble(command.ExecuteScalar());
    }

    private static bool IsDisposed(DatabaseContext database)
    {
        try { _ = database.Database; return false; }
        catch (ObjectDisposedException) { return true; }
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
        profile.Track(progress, database);
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
        private readonly List<(PlaybackProgressService Progress, TrackingDatabaseContext Database)> _players = new();
        public string Root { get; }
        public string? ConnectionString { get; set; }

        public IsolatedProfile()
        {
            Root = Path.Combine(_tempRoot, "resume-contention-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, Root);
        }

        public void Track(PlaybackProgressService progress, TrackingDatabaseContext database)
            => _players.Add((progress, database));

        public void Dispose()
        {
            try
            {
                foreach (var (progress, database) in _players)
                {
                    progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    Assert.True(SpinWait.SpinUntil(() => database.Disposed, TimeSpan.FromSeconds(5)),
                        "Player-owned storage must finish disposal before its profile is removed.");
                }
            }
            finally { Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, _previousRoot); }
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

    private sealed class VolatileDatabaseContext(string name) : DatabaseContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlite($"Data Source={name};Mode=Memory;Cache=Shared");
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
