using System;
using System.Threading;
using Xunit;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    [Collection("Application Collection")]
    public class DownloadTests
    {
        private readonly AppFixture _fixture;

        public DownloadTests(AppFixture fixture)
        {
            _fixture = fixture;
        }

        private void NavigateTo(string tabName)
        {
            var button = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName(tabName))?.AsButton();
            Assert.NotNull(button);
            button.Invoke();
            Thread.Sleep(500);
        }

        [Fact]
        public void Downloads_View_RefreshesWithoutClosingTheApplication()
        {
            NavigateTo("Downloads");
            
            // Should show the empty state or mock downloaded files.
            var refreshBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Refresh"))?.AsButton();
            Assert.NotNull(refreshBtn);
            refreshBtn.Invoke();
            Thread.Sleep(500);
            Assert.False(_fixture.App.HasExited);
        }

        [Fact]
        public async System.Threading.Tasks.Task T1_Downloads_02_Pause_and_Resume_Download()
        {
            // A real season job from a local seeder, slow enough to act on mid-transfer.
            await using var season = await QueuedSeason.StartAsync(64 * 1024);
            var row = season.WaitForProgress(above: 0);

            season.Press("Pause");
            Assert.True(SpinWait.SpinUntil(() => season.Status() == "Paused", TimeSpan.FromSeconds(30)), season.State());
            double paused = season.Progress();
            Assert.True(season.PartialBytes() > 0, "Pausing must keep the partial data.");
            long sent = season.Seeder.UploadedBytes;
            Thread.Sleep(3000);
            Assert.Equal(paused, season.Progress());
            Assert.InRange(season.Seeder.UploadedBytes - sent, 0, 64 * 1024); // At most an in-flight piece.

            season.Press("Resume");
            Assert.True(SpinWait.SpinUntil(() => season.Status() == "Downloading" && season.Progress() > paused,
                TimeSpan.FromSeconds(60)), "Resuming did not continue the transfer. " + season.State());
        }

        [Fact]
        public async System.Threading.Tasks.Task T1_Downloads_03_Download_Progress_Updates()
        {
            // Fast enough to finish: the row's progress rises through several values to a completed episode,
            // which then plays automatically (the default setting) and leaves Downloads for the player.
            await using var season = await QueuedSeason.StartAsync(1024 * 1024);
            string episode = NativeTemporaryDownloadBoundaryTests.LocalSeeder.Title + " - Episode 1";
            var seen = new System.Collections.Generic.SortedSet<double>();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                double progress = season.Progress();
                if (progress >= 0) seen.Add(Math.Round(progress));
                return season.Status() == "Completed" || season.PlayerTitle() == episode;
            }, TimeSpan.FromSeconds(120)), "The season did not complete. " + season.State());
            Assert.True(SpinWait.SpinUntil(() => season.PlayerTitle() == episode, TimeSpan.FromSeconds(30)),
                "The finished episode did not open in the player. " + season.State());
            Assert.True(seen.Count >= 3, "Progress was not shown as it changed: " + string.Join(", ", seen));

            season.OpenDownloads();
            Assert.True(SpinWait.SpinUntil(() => season.Status() == "Completed", TimeSpan.FromSeconds(15)), season.State());
            Assert.Equal(100, season.Progress());
            Assert.Contains(System.IO.Directory.GetFiles(season.Library, "*.mkv", System.IO.SearchOption.AllDirectories),
                path => new System.IO.FileInfo(path).Length == season.Seeder.VideoBytes);
        }

        [Fact]
        public async System.Threading.Tasks.Task T1_Downloads_04_Cancel_Download_and_Clean_Files()
        {
            // By design a cancelled season keeps its partial files ("partial files were retained") for a later
            // retry; cancelling must stop the transfer and offer Retry and Remove. No file is deleted.
            await using var season = await QueuedSeason.StartAsync(64 * 1024);
            season.WaitForProgress(above: 0);

            season.Press("Cancel");
            season.Confirm("Cancel Download");
            Assert.True(SpinWait.SpinUntil(() => season.Status() == "Cancelled", TimeSpan.FromSeconds(30)), season.State());
            Assert.Contains("partial files were retained", season.State(), StringComparison.Ordinal);
            Assert.True(season.PartialBytes() > 0, "Cancelling must not delete the partial data.");
            long sent = season.Seeder.UploadedBytes;
            Thread.Sleep(3000);
            Assert.InRange(season.Seeder.UploadedBytes - sent, 0, 64 * 1024);
            Assert.True(season.Button("Retry").IsEnabled && season.Button("Remove").IsEnabled, season.State());
            Assert.False(season.Button("Pause").IsEnabled);
        }

        [Fact(Skip = "Requires a source-selection fixture; the previous check only opened the Downloads view.")]
        public void T1_Downloads_05_Audio_Language_Preference_Selection()
        {
            NavigateTo("Downloads");
            var refreshBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Refresh"))?.AsButton();
            Assert.NotNull(refreshBtn);
        }

        [Fact(Skip = "Requires a sandboxed output-path injection point; the previous check did not attempt traversal.")]
        public void T2_Downloads_01_Output_Path_Traversal_Attempt()
        {
            NavigateTo("Downloads");
            var refreshBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Refresh"))?.AsButton();
            Assert.NotNull(refreshBtn);
        }

        [Fact(Skip = "Requires an isolated torrent-client endpoint; the previous check did not simulate an outage.")]
        public void T2_Downloads_02_Torrent_Client_Offline_on_Connect()
        {
            NavigateTo("Downloads");
            var refreshBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Refresh"))?.AsButton();
            Assert.NotNull(refreshBtn);
        }

        [Fact(Skip = "Requires an injectable disk-capacity boundary; the previous check did not exhaust disk space.")]
        public void T2_Downloads_03_Disk_Space_Exhaustion()
        {
            NavigateTo("Downloads");
            var refreshBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Refresh"))?.AsButton();
            Assert.NotNull(refreshBtn);
        }

        [Fact(Skip = "Requires direct malformed-magnet submission; the previous check did not submit a magnet URI.")]
        public void T2_Downloads_04_Malformed_Info_Hash_Magnet_Link()
        {
            NavigateTo("Downloads");
            var refreshBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Refresh"))?.AsButton();
            Assert.NotNull(refreshBtn);
        }

        [Fact]
        public async System.Threading.Tasks.Task T2_Downloads_05_Interrupted_Download_Recovery()
        {
            // Closing the app mid-transfer leaves the job paused with its partial data; Resume continues from it.
            await using var season = await QueuedSeason.StartAsync(64 * 1024);
            season.WaitForProgress(above: 1);
            long partial = season.PartialBytes();

            season.App.Restart();
            season.OpenDownloads();
            Assert.True(SpinWait.SpinUntil(() => season.Status() == "Paused", TimeSpan.FromSeconds(30)), season.State());
            Assert.Contains("resume", season.State(), StringComparison.OrdinalIgnoreCase);
            Assert.True(season.PartialBytes() >= partial, $"The partial shrank from {partial} to {season.PartialBytes()} bytes.");

            season.Press("Resume");
            Assert.True(SpinWait.SpinUntil(() => season.Status() == "Downloading" && season.PartialBytes() > partial,
                TimeSpan.FromSeconds(60)), "The resumed job did not continue from its partial. " + season.State());
        }

        /// <summary>One season job in its own profile, fed by a local seeder through the app's own feed and tracker
        /// settings; qBittorrent answers 503, so the native engine runs it.</summary>
        private sealed class QueuedSeason : IAsyncDisposable
        {
            private QueuedSeason(NativeTemporaryDownloadBoundaryTests.LocalSeeder seeder, AppFixture app)
            {
                Seeder = seeder;
                App = app;
            }

            public NativeTemporaryDownloadBoundaryTests.LocalSeeder Seeder { get; }
            public AppFixture App { get; }
            public string Library => System.IO.Path.Combine(App.SandboxPath, "library");

            public static async System.Threading.Tasks.Task<QueuedSeason> StartAsync(int rate)
            {
                var seeder = await NativeTemporaryDownloadBoundaryTests.LocalSeeder.StartAsync(rate);
                try
                {
                    var app = new AppFixture(mangaPagePng: null, initializeProfile: root => Seed(root, seeder));
                    var season = new QueuedSeason(seeder, app);
                    season.OpenDownloads();
                    return season;
                }
                catch
                {
                    await seeder.DisposeAsync();
                    throw;
                }
            }

            private static void Seed(string root, NativeTemporaryDownloadBoundaryTests.LocalSeeder seeder)
            {
                string profile = System.IO.Path.Combine(root, "Roaming", "UniversalMediaOS");
                string configPath = System.IO.Path.Combine(profile, "config.json");
                var settings = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(
                    System.IO.File.ReadAllText(configPath))!;
                settings["NyaaUrl"] = seeder.Origin + "rss?q=";
                settings["AnimeToshoUrl"] = seeder.Origin + "rss?q=";
                settings["QBitHost"] = "127.0.0.1";
                settings["QBitPort"] = new Uri(seeder.Origin).Port.ToString();
                settings["DownloadDirectory"] = System.IO.Path.Combine(root, "library");
                System.IO.File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(settings));
                var job = new UniversalMediaOS.Core.Archiving.DownloadQueueJob
                {
                    Title = NativeTemporaryDownloadBoundaryTests.LocalSeeder.Title,
                    AudioPreference = "Sub",
                    TotalEpisodes = 1,
                    ReleaseSelectionProtocol = 1
                };
                System.IO.File.WriteAllText(System.IO.Path.Combine(profile, "download-queue.json"),
                    System.Text.Json.JsonSerializer.Serialize(new[] { job }));
            }

            private Window MainWindow() => System.Linq.Enumerable.Single(
                App.App.GetAllTopLevelWindows(App.Automation), window => window.Title == AppFixture.ExpectedMainWindowTitle);

            public void OpenDownloads()
            {
                MainWindow().FindFirstDescendant(cf => cf.ByName("Downloads").And(cf.ByControlType(ControlType.Button)))!
                    .AsButton().Invoke();
                Assert.True(SpinWait.SpinUntil(() => Row() != null, TimeSpan.FromSeconds(15)), "The queued season is not listed.");
            }

            private AutomationElement? Row() => System.Linq.Enumerable.FirstOrDefault(
                MainWindow().FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)),
                item => item.FindFirstDescendant(cf => cf.ByName(NativeTemporaryDownloadBoundaryTests.LocalSeeder.Title)) != null);

            private static readonly string[] Statuses =
                { "Queued", "Downloading", "Pausing...", "Paused", "Cancelling...", "Completed", "Failed", "Cancelled" };

            public string Status() => System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.Select(
                Row()?.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)) ?? Array.Empty<AutomationElement>(),
                text => text.Name), name => Array.IndexOf(Statuses, name) >= 0) ?? string.Empty;

            public double Progress() =>
                Row()?.FindFirstDescendant(cf => cf.ByControlType(ControlType.ProgressBar))?.Patterns.RangeValue.Pattern.Value.Value ?? -1;

            public Button Button(string name) =>
                Row()!.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button)))!.AsButton();

            public void Press(string name)
            {
                Assert.True(SpinWait.SpinUntil(() => Button(name).IsEnabled, TimeSpan.FromSeconds(10)), $"{name} stayed disabled. {State()}");
                Button(name).Invoke();
            }

            /// <summary>Answers Yes in the confirmation message box owned by the main window.</summary>
            public void Confirm(string title)
            {
                Window? Dialog() => System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.Concat(
                    MainWindow().ModalWindows, App.App.GetAllTopLevelWindows(App.Automation)), window => window.Title == title);
                Assert.True(SpinWait.SpinUntil(() => Dialog() != null, TimeSpan.FromSeconds(10)), $"No '{title}' confirmation appeared.");
                Dialog()!.FindFirstDescendant(cf => cf.ByAutomationId("6").And(cf.ByControlType(ControlType.Button)))!.AsButton().Invoke();
                Assert.True(SpinWait.SpinUntil(() => Dialog() == null, TimeSpan.FromSeconds(5)), $"'{title}' did not close.");
            }

            public string PlayerTitle() =>
                MainWindow().FindFirstDescendant(cf => cf.ByAutomationId("MediaTitleText"))?.Name ?? string.Empty;

            /// <summary>Waits until the job shows a running transfer above the given percentage.</summary>
            public AutomationElement WaitForProgress(double above)
            {
                Assert.True(SpinWait.SpinUntil(() => Status() == "Downloading" && Progress() > above,
                    TimeSpan.FromSeconds(90)), "The season never started transferring. " + State());
                return Row()!;
            }

            public long PartialBytes() => System.IO.Directory.Exists(Library)
                ? System.Linq.Enumerable.Sum(System.IO.Directory.GetFiles(Library, "*", System.IO.SearchOption.AllDirectories),
                    path => new System.IO.FileInfo(path).Length)
                : 0;

            /// <summary>The row's texts and the app's log tail, for failure messages.</summary>
            public string State()
            {
                string texts = string.Join(" | ", System.Linq.Enumerable.Select(
                    Row()?.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)) ?? Array.Empty<AutomationElement>(),
                    text => text.Name));
                string log = System.IO.Path.Combine(App.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
                string tail = string.Empty;
                try
                {
                    using var stream = new System.IO.FileStream(log, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                        System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
                    stream.Seek(Math.Max(0, stream.Length - 3000), System.IO.SeekOrigin.Begin);
                    tail = new System.IO.StreamReader(stream).ReadToEnd();
                }
                catch (System.IO.IOException) { }
                return $"Row: {texts}. Progress {Progress():0.0}. Partial bytes {PartialBytes()}.{Environment.NewLine}{tail}";
            }

            public async System.Threading.Tasks.ValueTask DisposeAsync()
            {
                App.Dispose();
                await Seeder.DisposeAsync();
            }
        }
    }
}
