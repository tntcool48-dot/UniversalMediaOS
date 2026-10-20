using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class PackagedResumeDurabilityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NormalCloseBehindAWriterPastTheDrainDeadlineRestoresTheAcceptedPositionAfterRestart()
    {
        using var fixture = new AppFixture();
        string directory = Path.Combine(fixture.SandboxPath, "durable-resume-media");
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "durable-resume.mkv");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", [
                "-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10",
                "-t", "600", "-c:v", "mpeg4", "-y", video], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
        }

        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        Button? Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(name))?.AsButton();
        double Position() => Window().FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!
            .Patterns.RangeValue.Pattern.Value.Value / 1000.0;
        bool IsPaused() => Button("Play or pause")?.FindFirstDescendant(cf => cf.ByText("Play")) != null;
        string logPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
        void OpenVideo()
        {
            Button("Player")!.Invoke();
            Assert.True(SpinWait.SpinUntil(() => Button("Play or pause") != null, TimeSpan.FromSeconds(5)));
            Window().FindFirstDescendant(cf => cf.ByName("Playback options"))!.Patterns.ExpandCollapse.Pattern.Expand();
            Window().FindFirstDescendant(cf => cf.ByName("Playback volume"))!.Patterns.RangeValue.Pattern.SetValue(0);
            Window().FindFirstDescendant(cf => cf.ByName("Media URL or local file path"))!.AsTextBox().Text = video;
            Button("Open media URL or path")!.Invoke();
        }
        double SkipThenPause()
        {
            if (IsPaused()) Button("Play or pause")!.Invoke();
            Assert.True(SpinWait.SpinUntil(() => !IsPaused(), TimeSpan.FromSeconds(5)), ReadShared(logPath));
            double before = Position();
            Button("Skip forward 30 seconds")!.Invoke();
            Assert.True(SpinWait.SpinUntil(() => Position() >= before + 25, TimeSpan.FromSeconds(5)), ReadShared(logPath));
            Button("Play or pause")!.Invoke();
            Assert.True(SpinWait.SpinUntil(IsPaused, TimeSpan.FromSeconds(5)), ReadShared(logPath));
            Thread.Sleep(500); // The paused decoder clock settles before its final observation.
            return Position();
        }

        OpenVideo();
        Assert.True(SpinWait.SpinUntil(() => Position() >= 1.5, TimeSpan.FromSeconds(10)), ReadShared(logPath));
        double shownCommitted = SkipThenPause();
        ResumeState row = WaitForRow(candidate => Math.Abs(candidate.PositionSeconds - shownCommitted) <= 1);
        double committed = row.PositionSeconds;
        string connectionString;
        using (var database = new DatabaseContext()) connectionString = database.Database.GetConnectionString()!;
        string journal = Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource) + ".resume-pending.json";

        double accepted = double.NaN;
        var closing = new Stopwatch();
        using (var writer = new ResumeDispatcherContentionTests.WriterLock(connectionString, row.MediaId, row.EpisodeId, 60_000))
        {
            double shownAccepted = SkipThenPause();
            // The paused observation becomes durable beside the database while SQLite stays locked.
            Assert.True(SpinWait.SpinUntil(() => PendingPosition(journal, row) is double pending &&
                Math.Abs(pending - shownAccepted) <= 1 && (accepted = pending) > committed + 25,
                TimeSpan.FromSeconds(5)), ReadShared(journal));

            closing.Start();
            fixture.App.Close(killIfCloseFails: false);
            Assert.True(SpinWait.SpinUntil(() => fixture.App.HasExited, TimeSpan.FromSeconds(40)), ReadShared(logPath));
            closing.Stop();
            Assert.Contains("[Resume] Application close deadline elapsed with playback progress still pending.", ReadShared(logPath));
            Assert.Equal(committed, ReadCommitted(connectionString, row));
        }
        output.WriteLine($"Committed {committed:0.000}s; accepted behind the writer {accepted:0.000}s; close took {closing.Elapsed.TotalSeconds:0.000}s.");
        Assert.InRange(closing.Elapsed.TotalSeconds, 14, 40);
        // The exited process could not commit; only the journal holds the accepted position.
        Assert.Equal(committed, ReadCommitted(connectionString, row));
        Assert.Equal(accepted, PendingPosition(journal, row));

        fixture.Restart();
        OpenVideo();
        WaitForRow(candidate => candidate.MediaId == row.MediaId && candidate.EpisodeId == row.EpisodeId &&
            candidate.PositionSeconds == accepted);
        Assert.True(SpinWait.SpinUntil(() => Position() >= accepted - 1, TimeSpan.FromSeconds(15)),
            $"Restarted position {Position():0.000}s did not reach the journaled {accepted:0.000}s.\n{ReadShared(logPath)}");
        // Replay acknowledges the record; later observations commit promptly once the writer is gone.
        Assert.True(SpinWait.SpinUntil(() => PendingPosition(journal, row) == null, TimeSpan.FromSeconds(5)), ReadShared(journal));
        using var verify = new DatabaseContext();
        Assert.Equal(120.5, verify.GetResumeState("mock-anime", "mock-anime-episode-1"));
    }

    private static ResumeState WaitForRow(Func<ResumeState, bool> matches)
    {
        ResumeState? found = null;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var database = new DatabaseContext();
            found = database.ResumeStates.AsNoTracking().AsEnumerable()
                .FirstOrDefault(row => row.MediaId != "mock-anime" && matches(row));
            return found != null;
        }, TimeSpan.FromSeconds(10)), "The expected resume row was not saved.");
        return found!;
    }

    /// <summary>A private-cache WAL reader sees the last committed row beside a held writer.</summary>
    private static double ReadCommitted(string connectionString, ResumeState row)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString)
            { Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PositionSeconds FROM ResumeStates WHERE MediaId = $work AND EpisodeId = $unit";
        command.Parameters.AddWithValue("$work", row.MediaId);
        command.Parameters.AddWithValue("$unit", row.EpisodeId);
        return Convert.ToDouble(command.ExecuteScalar());
    }

    private static double? PendingPosition(string journal, ResumeState row)
    {
        try
        {
            using var document = JsonDocument.Parse(ReadShared(journal));
            foreach (var pending in document.RootElement.GetProperty("Pending").EnumerateArray())
            {
                if (pending.GetProperty("WorkKey").GetString() == row.MediaId &&
                    pending.GetProperty("UnitKey").GetString() == row.EpisodeId)
                    return pending.GetProperty("Position").GetDouble();
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException) { }
        return null;
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
