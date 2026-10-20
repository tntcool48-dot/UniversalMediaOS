using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class ResumeCrashRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AcknowledgedProgressRecoversAfterTheProcessDiesBehindAWriter(bool catalog, bool completed)
    {
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests"));
        string root = Path.Combine(allowed, "resume-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string marker = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(root, ".resume-crash-owned"), marker);
        string? previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        string? connectionString = null;
        using var worker = new Process { StartInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardOutput = true, WorkingDirectory = AppContext.BaseDirectory
        }};
        foreach (string argument in new[] { typeof(ResumeCrashWorker).Assembly.Location,
            "--resume-crash-worker", root, catalog.ToString(), completed.ToString(), marker })
            worker.StartInfo.ArgumentList.Add(argument);
        Assert.True(worker.Start());
        Task<string> errors = worker.StandardError.ReadToEndAsync();
        Task<string> output = worker.StandardOutput.ReadToEndAsync();
        try
        {
            await Signal("ready.json");
            using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "ready.json")));
            string work = ready.RootElement.GetProperty("work").GetString()!;
            string unit = ready.RootElement.GetProperty("unit").GetString()!;
            connectionString = ready.RootElement.GetProperty("connectionString").GetString()!;
            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                using var writer = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = writer;
                command.CommandText = "UPDATE ResumeStates SET PositionSeconds=PositionSeconds WHERE MediaId=$work AND EpisodeId=$unit";
                command.Parameters.AddWithValue("$work", work);
                command.Parameters.AddWithValue("$unit", unit);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                await File.WriteAllTextAsync(Path.Combine(root, "observe.flag"), "accept observation");
                await Signal("accepted.json");
                worker.Kill();
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                writer.Rollback();
            }

            // Reconstruct production playback in this independent process; no old worker survives.
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, root);
            var progress = new PlaybackProgressService(new(Path.Combine(root, "library.json")));
            using (var player = new PlaybackViewModel(new DatabaseContext(), null, null, playbackProgress: progress))
            {
                player.LoadEmbed("https://fixture.invalid/crash-worker", "Crash recovery fixture", "3", 12, 42,
                    audiovisualContext: catalog ? ResumeCrashWorker.Context() : null);
                await player.ResumeLoadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
                using var database = new DatabaseContext();
                Assert.Equal(completed ? 0 : 120, database.GetResumeState(work, unit));
                Assert.Equal(57, database.GetResumeState(work, "adjacent-unit"));
                Assert.Equal(77, database.GetResumeState("av:fixture:film:neighbor", "feature"));
                Assert.Equal(3, await database.ResumeStates.CountAsync());
            }
            await progress.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill();
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            await Task.WhenAll(errors, output);
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previousRoot);
            // Replay opens the same file through its own frozen destination; tests run serially.
            if (connectionString != null) SqliteConnection.ClearAllPools();
            var owned = new DirectoryInfo(root);
            Assert.StartsWith(allowed + Path.DirectorySeparatorChar, owned.FullName, StringComparison.OrdinalIgnoreCase);
            Assert.Null(owned.LinkTarget);
            Assert.Equal(marker, File.ReadAllText(Path.Combine(root, ".resume-crash-owned")));
            owned.Delete(recursive: true);
        }

        async Task Signal(string name)
        {
            var elapsed = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(root, name)) && !worker.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(10);
            Assert.True(File.Exists(Path.Combine(root, name)), worker.HasExited
                ? $"Crash worker exited before {name}: {await errors} {await output}"
                : $"Crash worker did not acknowledge {name} while the writer was held.");
        }
    }
}
