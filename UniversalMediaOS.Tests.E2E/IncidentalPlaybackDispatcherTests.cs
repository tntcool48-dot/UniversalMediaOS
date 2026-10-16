using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E;

public sealed class IncidentalPlaybackDispatcherTests(ITestOutputHelper output)
{
    [Fact]
    public void CanonicalResumeDoesNotDependOnAnIncidentalUnpumpedMtaDispatcher()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckResumeOnWorker(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The owned MTA resume worker must finish and release its private profile.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void CheckResumeOnWorker()
    {
        string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests"));
        string root = Path.Combine(tempRoot, "incidental-dispatcher-" + Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        string? connectionString = null;
        PlaybackViewModel? player = null;
        PlaybackProgressService? progress = null;
        Dispatcher? dispatcher = null;
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, root);
        try
        {
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            Assert.Null(SynchronizationContext.Current);
            Assert.Null(System.Windows.Application.Current);
            // Image decoding can establish a dispatcher on a reused worker.
            // Its existence does not make that worker a pumped WPF UI host.
            dispatcher = Dispatcher.CurrentDispatcher;
            Assert.Same(dispatcher, Dispatcher.FromThread(Thread.CurrentThread));
            using (var seed = new DatabaseContext())
            {
                seed.Database.EnsureCreated();
                connectionString = seed.Database.GetConnectionString();
                seed.SaveResumeState("anime:anilist:100", "episode:1", 137.25);
            }
            progress = new(new(Path.Combine(root, "library.json")));
            player = new(new DatabaseContext(), null, null, playbackProgress: progress);
            var context = new EpisodePlaybackContext(1, 12,
                (_, _) => Task.FromResult<ResolvedEpisodePlayback?>(null), "sub", new(100));
            player.LoadEmbed("https://fixture.invalid/incidental-dispatcher", "Anime - Ep 1", "1", episodeContext: context);

            var read = Assert.IsAssignableFrom<Task>(typeof(PlaybackViewModel)
                .GetField("_resumeLoadRead", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player));
            read.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.True(read.IsCompletedSuccessfully, "The canonical database read must finish independently of the incidental dispatcher.");
            bool applied = SpinWait.SpinUntil(() => player.ResumeLoadCompleted.IsCompleted, TimeSpan.FromSeconds(5));
            string details = $"read={read.Status}; completion={player.ResumeLoadCompleted.Status}; " +
                $"worker apartment={Thread.CurrentThread.GetApartmentState()}; dispatcher thread={dispatcher.Thread.ManagedThreadId}; " +
                $"worker thread={Thread.CurrentThread.ManagedThreadId}; dispatcher shutdown={dispatcher.HasShutdownStarted}; " +
                $"synchronization context={SynchronizationContext.Current?.GetType().Name ?? "none"}; pending reads={player.PendingResumeLoadCount}.";
            output.WriteLine(details);
            Assert.True(applied, "A completed canonical read must apply without pumping an incidental MTA dispatcher. " + details);
            player.ResumeLoadCompleted.GetAwaiter().GetResult();
            Assert.True(player.TryConsumePendingWebResumePosition(out double position));
            Assert.Equal(137.25, position);
            using var verify = new DatabaseContext();
            Assert.Equal(137.25, verify.GetResumeState("anime:anilist:100", "episode:1"));
        }
        finally
        {
            try
            {
                player?.Dispose();
                // Drain callbacks only during cleanup, after the assertion.
                // The failing baseline must not leave its queued Apply behind.
                if (dispatcher is { HasShutdownStarted: false })
                {
                    dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    dispatcher.InvokeShutdown();
                }
                if (player != null) player.ResumeLoadCompleted.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                progress?.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (connectionString != null)
                {
                    using var pool = new SqliteConnection(connectionString);
                    SqliteConnection.ClearPool(pool);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previousRoot);
                var ownedDirectory = new DirectoryInfo(root);
                Assert.StartsWith(tempRoot + Path.DirectorySeparatorChar, ownedDirectory.FullName, StringComparison.OrdinalIgnoreCase);
                Assert.Null(ownedDirectory.LinkTarget);
                if (ownedDirectory.Exists) ownedDirectory.Delete(recursive: true);
            }
        }
    }
}
