using System.Reflection;
using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;

namespace UniversalMediaOS.Tests.E2E;

/// <summary>A headless child process, killed only after its accepted observation is acknowledged.</summary>
internal static class ResumeCrashWorker
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 5 || args[0] != "--resume-crash-worker") return 2;
        try
        {
            string root = Path.GetFullPath(args[1]);
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests"));
            if (!root.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                new DirectoryInfo(root).LinkTarget != null ||
                File.ReadAllText(Path.Combine(root, ".resume-crash-owned")) != args[4])
                throw new InvalidOperationException("The crash worker requires its own isolated fixture profile.");
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, root);
            bool catalog = bool.Parse(args[2]);
            bool completed = bool.Parse(args[3]);
            var progress = new PlaybackProgressService(new(Path.Combine(root, "library.json")));
            using var player = new PlaybackViewModel(new DatabaseContext(), null, null, playbackProgress: progress);
            player.LoadEmbed("https://fixture.invalid/crash-worker", "Crash recovery fixture", "3", 12, 42,
                audiovisualContext: catalog ? Context() : null);
            await player.ResumeLoadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
            player.ReportWebPlaybackProgress(90, 600, false, paused: true);
            await progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            string work;
            string unit;
            using (var database = new DatabaseContext())
            {
                var row = database.ResumeStates.AsNoTracking().Single();
                work = row.MediaId;
                unit = row.EpisodeId;
                database.SaveResumeState(work, "adjacent-unit", 57);
                database.SaveResumeState("av:fixture:film:neighbor", "feature", 77);
                Write(root, "ready.json", new { work, unit, connectionString = database.Database.GetConnectionString() });
            }
            await WaitForFile(Path.Combine(root, "observe.flag"));
            if (completed) player.ReportWebPlaybackProgress(600, 600, true, paused: true);
            else player.PlaybackTime = 120_000;
            player.SetTabActive(false); // Accept the exact current observation before teardown.

            // The baseline has no journal. The repaired worker waits for its durable acknowledgement,
            // independently of the SQLite writer still held by the parent.
            MethodInfo? durableFlush = typeof(PlaybackProgressService).GetMethod("FlushDurableObservationsAsync");
            if (durableFlush?.Invoke(progress, null) is Task durable)
                await durable.WaitAsync(TimeSpan.FromSeconds(5));
            Write(root, "accepted.json", new { work, unit, position = completed ? 0 : 120, durableApiPresent = durableFlush != null });
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString());
            return 1;
        }
    }

    internal static AudiovisualPlaybackContext Context() => new("av:fixture:show:crash",
        new AudiovisualIdentity { PrimaryId = new("fixture", "show", "crash"),
            Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series,
            Title = "Crash recovery fixture" }, new() { SeasonNumber = 2, EpisodeNumber = 3 },
        "Crash recovery fixture", "", new());

    private static async Task WaitForFile(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(path)) await Task.Delay(10, timeout.Token);
    }

    private static void Write(string root, string name, object value)
    {
        string destination = Path.Combine(root, name);
        string temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, destination);
    }
}
