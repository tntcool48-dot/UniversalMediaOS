using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class PackagedSourceAdmissionTests(ITestOutputHelper output)
{
    private const string FilmWork = "av:fixture:film:pressure";
    private const string FilmUnit = "feature";
    private const string ShowWork = "av:fixture:show:pressure";
    private const string EpisodeUnit = "season:1:episode:7";
    private const double SeededEpisodePosition = 421.5;

    [Fact]
    public async Task PressuredSourceChangeWaitsForSavedProgressThenOpensTheEpisodeOnItsOwnRow()
    {
        using var fixture = new AppFixture();
        string media = Path.Combine(fixture.SandboxPath, "source-pressure-media");
        string film = await CreateLibraryItemAsync(Path.Combine(media, "film"), "testsrc", new AudiovisualIdentity
        {
            PrimaryId = new("fixture", "film", "pressure"), Kind = AudiovisualMediaKind.Movie,
            ContentForm = AudiovisualContentForm.Feature, Title = "Pressure film fixture", Year = 2010
        }, new AudiovisualUnit(), FilmWork, "Pressure film fixture");
        string episode = await CreateLibraryItemAsync(Path.Combine(media, "episode"), "smptebars", new AudiovisualIdentity
        {
            PrimaryId = new("fixture", "show", "pressure"), Kind = AudiovisualMediaKind.Television,
            ContentForm = AudiovisualContentForm.Series, Title = "Pressure show fixture", Year = 2008
        }, new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 7 }, ShowWork, "Pressure show fixture");
        using (var seed = new DatabaseContext()) seed.SaveResumeState(ShowWork, EpisodeUnit, SeededEpisodePosition);

        string logPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
        const string WaitingText = "Saving progress before continuing...";
        Window window = fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(candidate => candidate.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        AutomationElement Named(string name) => window.FindFirstDescendant(cf => cf.ByName(name)) ??
            throw new InvalidOperationException($"'{name}' is not in the window.\n{ReadShared(logPath)}");
        window.FindFirstDescendant(cf => cf.ByName("Player"))!.AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => window.FindFirstDescendant(cf => cf.ByName("Play or pause")) != null,
            TimeSpan.FromSeconds(5)));
        // Whole-window searches take about a second here, so the player's own elements are found once.
        Button playPause = Named("Play or pause").AsButton();
        // Expanded options keep the overlay controls from auto-hiding while media plays.
        Named("Playback options").Patterns.ExpandCollapse.Pattern.Expand();
        Assert.True(SpinWait.SpinUntil(() => window.FindFirstDescendant(cf => cf.ByName("Open media URL or path")) != null,
            TimeSpan.FromSeconds(5)));
        Button open = Named("Open media URL or path").AsButton();
        TextBox source = Named("Media URL or local file path").AsTextBox();
        AutomationElement slider = window.FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!;
        AutomationElement? title = null;
        AutomationElement? status = null;
        double Position() => slider.Patterns.RangeValue.Pattern.Value.Value / 1000.0;
        bool IsPaused() => playPause.FindFirstDescendant(cf => cf.ByText("Play")) != null;
        string Title() => (title ??= window.FindFirstDescendant(cf => cf.ByAutomationId("MediaTitleText")))?.Name ?? string.Empty;
        string Status() => status?.Name ?? string.Empty;
        bool Waiting() => Status() == WaitingText;
        void Toggle(bool toPlaying)
        {
            playPause.Invoke();
            // Wait for the decoder's own event: the paused one queues the save, the playing one follows ownership.
            Assert.True(SpinWait.SpinUntil(() => Waiting() || (toPlaying
                ? !IsPaused() && Status() == "Playing"
                : IsPaused() && Status() == "Paused"), TimeSpan.FromSeconds(5)),
                $"Status '{Status()}', paused {IsPaused()}.\n{ReadShared(logPath)}");
            Assert.False(Waiting(), $"A transport request was deferred before the source change.\n{ReadShared(logPath)}");
        }

        Named("Playback volume").Patterns.RangeValue.Pattern.SetValue(0);
        source.Text = film;
        open.Invoke();
        Assert.True(SpinWait.SpinUntil(() => Title() == "Pressure film fixture" && Position() >= 1.5,
            TimeSpan.FromSeconds(10)), ReadShared(logPath));
        // The title row's status text follows the player; it is the same element for the whole session.
        status = window.FindFirstDescendant(cf => cf.ByName("Playing"));
        Assert.NotNull(status);
        double beforeSkip = Position();
        Named("Skip forward 30 seconds").AsButton().Invoke();
        Assert.True(SpinWait.SpinUntil(() => Position() >= beforeSkip + 25, TimeSpan.FromSeconds(5)), ReadShared(logPath));
        Toggle(toPlaying: false);
        Thread.Sleep(500); // The paused decoder clock settles before its final observation.
        double shownCommitted = Position();
        double committed = WaitForPosition(FilmWork, FilmUnit, value => Math.Abs(value - shownCommitted) <= 1);
        string connectionString;
        using (var database = new DatabaseContext()) connectionString = database.Database.GetConnectionString()!;
        string journal = DurableResumeJournal.JournalPath(
            Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource));
        // Start the pressure from a fully drained player: no read, ownership or save is outstanding.
        Assert.True(SpinWait.SpinUntil(() => ResumeDispatcherContentionTests.PendingPosition(journal, FilmWork, FilmUnit) == null &&
            File.Exists(journal), TimeSpan.FromSeconds(5)), ReadShared(journal));

        int threshold = (int)typeof(PlaybackViewModel).GetField("ResumeAdmissionThreshold",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        // Behind a held writer, the first pause's commit holds the shared persistence gate. Each later Play
        // then queues one ownership operation and each Pause one save: after k cycles, 2k - 1 wait. The last
        // Play is admitted below the threshold, so the source change is the first deferred request.
        int cycles = threshold / 2 + 1;
        double shownWaiting, journaledFilm, waitingSeconds;
        var pressure = new Stopwatch();
        using (var writer = new ResumeDispatcherContentionTests.WriterLock(connectionString, FilmWork, FilmUnit, 60_000))
        {
            pressure.Start();
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                Toggle(toPlaying: true);
                if (cycle == 1) Thread.Sleep(2000); // Moves the film clearly past its committed position.
                Toggle(toPlaying: false);
            }
            pressure.Stop();
            // The first blocked commit fails at SQLite's 30-second command timeout, releasing two operations.
            Assert.InRange(pressure.Elapsed.TotalSeconds, 0, 25);
            Thread.Sleep(500);
            shownWaiting = Position();

            source.Text = episode;
            open.Invoke();
            Assert.True(SpinWait.SpinUntil(Waiting, TimeSpan.FromSeconds(5)),
                $"The pressured source change did not wait.\n{ReadShared(logPath)}");
            var waiting = Stopwatch.StartNew();
            // The deferred episode owns nothing yet: the film stays paused on its clock, with input disabled.
            Assert.Equal("Pressure film fixture", Title());
            Assert.False(playPause.IsEnabled);
            Assert.False(open.IsEnabled);
            Assert.False(source.IsEnabled);
            // Besides the title row's status, the overlay above the video shows the notice.
            Assert.True(window.FindAllDescendants(cf => cf.ByName(WaitingText)).Length > 1);
            Assert.InRange(Position(), shownWaiting - 0.5, shownWaiting + 0.5);
            // The film's latest accepted position is beside the database while SQLite still holds the earlier row.
            double? pending = null;
            Assert.True(SpinWait.SpinUntil(() => (pending = ResumeDispatcherContentionTests.PendingPosition(journal, FilmWork, FilmUnit))
                is double value && Math.Abs(value - shownWaiting) <= 1, TimeSpan.FromSeconds(5)), ReadShared(journal));
            journaledFilm = pending!.Value;
            Assert.Equal(committed, ResumeDispatcherContentionTests.ReadCommitted(connectionString, FilmWork, FilmUnit));
            Assert.Equal(SeededEpisodePosition, ResumeDispatcherContentionTests.ReadCommitted(connectionString, ShowWork, EpisodeUnit));
            Thread.Sleep(1000);
            Assert.True(Waiting(), "The waiting state must hold while the writer does.");
            Assert.Equal("Pressure film fixture", Title());
            waitingSeconds = waiting.Elapsed.TotalSeconds;
        }

        // Admission opens the requested episode from its own row and plays it.
        string episodeTitle = "Pressure show fixture \u00B7 S01E07";
        var admission = Stopwatch.StartNew();
        Assert.True(SpinWait.SpinUntil(() => !Waiting() && Title() == episodeTitle && !IsPaused() &&
            Position() >= SeededEpisodePosition - 1, TimeSpan.FromSeconds(20)), ReadShared(logPath));
        admission.Stop();
        Assert.True(playPause.IsEnabled);
        Assert.True(source.IsEnabled);
        // Play past the seeded row, so the episode's own save cannot be mistaken for it.
        Assert.True(SpinWait.SpinUntil(() => Position() >= SeededEpisodePosition + 2, TimeSpan.FromSeconds(10)), ReadShared(logPath));
        Toggle(toPlaying: false);
        Thread.Sleep(500);
        double shownEpisode = Position();
        Assert.InRange(shownEpisode, SeededEpisodePosition + 2, SeededEpisodePosition + 30);
        double episodeSaved = WaitForPosition(ShowWork, EpisodeUnit, value => Math.Abs(value - shownEpisode) <= 1);
        double filmSaved = WaitForPosition(FilmWork, FilmUnit, value => Math.Abs(value - journaledFilm) <= 1);
        Assert.True(filmSaved >= committed + 1.5, $"Film row {filmSaved:0.000}s lost the pressured progress after {committed:0.000}s.");
        Assert.True(SpinWait.SpinUntil(() => ResumeDispatcherContentionTests.PendingPosition(journal, FilmWork, FilmUnit) == null &&
            ResumeDispatcherContentionTests.PendingPosition(journal, ShowWork, EpisodeUnit) == null, TimeSpan.FromSeconds(5)), ReadShared(journal));
        output.WriteLine($"Film committed {committed:0.000}s; {cycles} pressure cycles took {pressure.Elapsed.TotalSeconds:0.000}s; " +
            $"waiting held {waitingSeconds:0.000}s at film {shownWaiting:0.000}s (journaled {journaledFilm:0.000}s); " +
            $"admitted the episode {admission.Elapsed.TotalSeconds:0.000}s after release; film row {filmSaved:0.000}s, episode row {episodeSaved:0.000}s.");

        string log = ReadShared(logPath);
        int Count(string text) => log.Split(text).Length - 1;
        // Each pressure cycle reached the player as one admitted Play and one Pause; the other two pauses
        // are the film's before the writer and the episode's after admission.
        Assert.Equal(cycles, Count("Starting/resuming media player."));
        Assert.Equal(cycles + 2, Count("Pausing media player."));
        Assert.Contains($"[Resume] Loaded saved position {SeededEpisodePosition:0.0}s for media '{ShowWork}', episode '{EpisodeUnit}'.", log);
        Assert.DoesNotContain("Deferred playback request failed", log);
        Assert.DoesNotContain("could not be journaled or saved", log);

        window.Patterns.Window.Pattern.Close();
        Assert.True(SpinWait.SpinUntil(() => fixture.App.HasExited, TimeSpan.FromSeconds(20)), ReadShared(logPath));
        Assert.Contains("[Resume] Accepted playback progress drained before application close.", ReadShared(logPath));
        using var verify = new DatabaseContext();
        var rows = verify.ResumeStates.AsNoTracking().ToArray();
        // Exactly the seeded fixture row, one film row and one episode row; neither unit took the other's position.
        Assert.Equal(3, rows.Length);
        Assert.Equal(120.5, Assert.Single(rows, row => row.MediaId == "mock-anime").PositionSeconds);
        Assert.Equal(filmSaved, Assert.Single(rows, row => row.MediaId == FilmWork && row.EpisodeId == FilmUnit).PositionSeconds);
        Assert.InRange(Assert.Single(rows, row => row.MediaId == ShowWork && row.EpisodeId == EpisodeUnit).PositionSeconds,
            episodeSaved - 1, episodeSaved + 1);
    }

    private static async Task<string> CreateLibraryItemAsync(string directory, string pattern, AudiovisualIdentity identity,
        AudiovisualUnit unit, string workKey, string title)
    {
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "media.mkv");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", [
                "-nostdin", "-hide_banner", "-v", "error", "-f", "lavfi", "-i", $"{pattern}=size=160x90:rate=10",
                "-t", "600", "-c:v", "mpeg4", "-y", video], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
        }
        File.WriteAllText(Path.Combine(directory, "library-item.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, WorkKey = workKey, UnitKey = AudiovisualIdentityKeys.CreateUnitKey(identity.ContentForm, unit),
            Identity = identity, Unit = unit, Title = title, MediaFile = "media.mkv", CaptionFiles = Array.Empty<string>(),
            SourceProvider = "generated-fixture", AudioNotice = "Generated pattern; no spoken audio."
        }));
        return video;
    }

    private static double WaitForPosition(string work, string unit, Func<double, bool> matches)
    {
        double found = double.NaN;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var database = new DatabaseContext();
            var row = database.ResumeStates.AsNoTracking().FirstOrDefault(state => state.MediaId == work && state.EpisodeId == unit);
            return row != null && matches(found = row.PositionSeconds);
        }, TimeSpan.FromSeconds(10)), $"The expected '{work}/{unit}' position was not saved; last seen {found:0.000}s.");
        return found;
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
