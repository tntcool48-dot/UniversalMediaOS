using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.UIA3;
using UniversalMediaOS.Core.Data;

namespace UniversalMediaOS.Tests.E2E.Infrastructure
{
    public class AppFixture : IDisposable
    {
        public const string ExpectedMainWindowTitle = "Universal Media OS";

        public Application App { get; private set; }
        public UIA3Automation Automation { get; private set; }
        public FlaUI.Core.AutomationElements.Window MainWindow { get; private set; }
        private readonly MockHttpServer _server;
        private readonly string? _previousDataRoot;
        public string SandboxPath { get; }

        public AppFixture() : this(mangaPagePng: null) { }

        internal AppFixture(byte[]? mangaPagePng,
            Func<System.Net.HttpListenerRequest, (int Status, string Body)>? mangaChapterFeed = null)
        {
            // 1. Start Mock HTTP Server
            _server = new MockHttpServer(mangaPagePng: mangaPagePng, mangaChapterFeed: mangaChapterFeed);
            _server.Start();

            // 2. Setup APPDATA redirection and test environment sandbox
            SandboxPath = Path.Combine(
                Path.GetTempPath(),
                "UniversalMediaOS.Tests",
                "AppFixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SandboxPath);
            string appDataDir = Path.Combine(SandboxPath, "Roaming", "UniversalMediaOS");
            Directory.CreateDirectory(appDataDir);

            // Write a custom config.json to the sandbox pointing to the Mock HTTP Server and custom DB path
            string configPath = Path.Combine(appDataDir, "config.json");
            string baseUrl = _server.BaseUrl.TrimEnd('/');
            var defaultSettings = new Dictionary<string, string>
            {
                ["AutoManageServices"] = "false",
                ["EnableDebugLogging"] = "true",
                ["StartupMonitor"] = "Secondary",
                ["ConsumetApiBase"] = baseUrl,
                ["ConsumetProvider"] = "gogoanime",
                ["AniListUrl"] = baseUrl + "/graphql",
                ["AniSkipUrl"] = baseUrl,
                ["MalApiUrl"] = baseUrl,
                ["NyaaUrl"] = baseUrl + "/nyaa?q=",
                ["AnimeToshoUrl"] = baseUrl + "/animetosho?q=",
                ["MangaDexUrl"] = baseUrl,
                ["MangaDexCoversUrl"] = baseUrl,
                ["DubAvailabilityProviders"] = "[]",
                ["DatabasePath"] = Path.Combine(SandboxPath, "media_os.db")
            };
            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultSettings));

            // Set environment variable for the test runner process
            _previousDataRoot = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", SandboxPath);

            try
            {

            // 3. Seed SQLite Database
            using (var db = new DatabaseContext())
            {
                db.Database.EnsureCreated();

                // Seed Dub Cast cache
                if (!db.DubHashes.Any())
                {
                    db.DubHashes.Add(new DubCastHash
                    {
                        MediaId = 1,
                        ShowTitle = "Mock Anime",
                        CharacterName = "Frieren",
                        VoiceActorName = "Atsumi Tanezaki",
                        VoiceActorImageUrl = baseUrl + "/va.png",
                        CharacterImageUrl = baseUrl + "/cover.png"
                    });
                }

                // Seed Resume states
                if (!db.ResumeStates.Any())
                {
                    db.ResumeStates.Add(new ResumeState
                    {
                        MediaId = "mock-anime",
                        EpisodeId = "mock-anime-episode-1",
                        PositionSeconds = 120.5
                    });
                }

                db.SaveChanges();
            }

            // 4. Locate the WPF executable relative to the test binary directory
            string testDir = AppDomain.CurrentDomain.BaseDirectory;
            string preferredConfiguration = testDir.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
            string fallbackConfiguration = preferredConfiguration == "Release" ? "Debug" : "Release";
            string? exePath = FindWpfExecutable(testDir, preferredConfiguration, fallbackConfiguration);

            if (exePath == null)
            {
                throw new FileNotFoundException(
                    $"Could not find UniversalMediaOS.WPF.exe from test output '{testDir}'. " +
                    "Build the solution first, or set UNIVERSAL_MEDIA_OS_EXE to the exact executable under test.");
            }

            string wpfProjectDir = Path.GetDirectoryName(exePath)!;

            // 5. Initialize FlaUI Automation and Launch Application with APPDATA redirected
            Automation = new UIA3Automation();

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = wpfProjectDir,
                UseShellExecute = false
            };
            psi.Environment["UNIVERSAL_MEDIA_OS_DATA_ROOT"] = SandboxPath;

            // Launch the application
            App = Application.Launch(psi);

            // Wait for the main window to load (up to 15 seconds)
            var startTime = DateTime.Now;
            string observedWindowTitles = "<none>";
            while ((DateTime.Now - startTime).TotalSeconds < 15)
            {
                var windows = App.GetAllTopLevelWindows(Automation);
                observedWindowTitles = string.Join(", ", windows.Select(window => $"'{window.Title}'"));
                var mainWin = System.Linq.Enumerable.FirstOrDefault(windows, w => w.Title == ExpectedMainWindowTitle);
                if (mainWin != null)
                {
                    MainWindow = mainWin;
                    break;
                }
                Thread.Sleep(200);
            }

            if (MainWindow == null)
            {
                // FlaUI 5 correctly annotates this fallback as nullable; the
                // immediately following guard turns a miss into a clear timeout.
                MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(15))!;
            }

            if (MainWindow == null)
            {
                throw new TimeoutException(
                    $"Main window '{ExpectedMainWindowTitle}' did not load within 30 seconds. Observed top-level windows: {observedWindowTitles}.");
            }

            Console.WriteLine("DUMPING MAINWINDOW DESCENDANTS FOR DIAGNOSTICS:");
            try
            {
                var descendants = MainWindow.FindAllDescendants();
                Console.WriteLine($"Total descendants found: {descendants.Length}");
                foreach (var d in descendants)
                {
                    Console.WriteLine($"- Name: '{d.Name}', ControlType: {d.ControlType}, AutomationId: '{d.AutomationId}'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to dump descendants: {ex.Message}");
            }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static string? FindWpfExecutable(
            string testDirectory,
            string preferredConfiguration,
            string fallbackConfiguration)
        {
            string? explicitPath = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_EXE");
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                string fullExplicitPath = Path.GetFullPath(explicitPath);
                if (File.Exists(fullExplicitPath))
                {
                    return fullExplicitPath;
                }

                throw new FileNotFoundException(
                    $"UNIVERSAL_MEDIA_OS_EXE points to a missing file: '{fullExplicitPath}'.");
            }

            const string executableName = "UniversalMediaOS.WPF.exe";
            string[] configurations = [preferredConfiguration, fallbackConfiguration];
            string[] targetFrameworks = ["net9.0-windows10.0.17763.0", "net9.0-windows"];

            DirectoryInfo? ancestor = new DirectoryInfo(testDirectory);
            for (int depth = 0; ancestor != null && depth < 10; depth++, ancestor = ancestor.Parent)
            {
                foreach (string configuration in configurations)
                {
                    foreach (string targetFramework in targetFrameworks)
                    {
                        string conventional = Path.Combine(
                            ancestor.FullName,
                            "UniversalMediaOS.WPF",
                            "bin",
                            configuration,
                            targetFramework,
                            executableName);
                        if (File.Exists(conventional))
                        {
                            return conventional;
                        }
                    }

                    // `dotnet --artifacts-path` uses
                    // <artifacts>/bin/<project>/<configuration>/ without the TFM.
                    string artifactsLayout = Path.Combine(
                        ancestor.FullName,
                        "UniversalMediaOS.WPF",
                        configuration.ToLowerInvariant(),
                        executableName);
                    if (File.Exists(artifactsLayout))
                    {
                        return artifactsLayout;
                    }

                    string artifactsRootLayout = Path.Combine(
                        ancestor.FullName,
                        "bin",
                        "UniversalMediaOS.WPF",
                        configuration.ToLowerInvariant(),
                        executableName);
                    if (File.Exists(artifactsRootLayout))
                    {
                        return artifactsRootLayout;
                    }
                }
            }

            return null;
        }

        public void Dispose()
        {
            try
            {
                if (App != null && !App.HasExited)
                {
                    // Attempt to close gracefully, fallback to Kill if needed
                    App.Close();
                }
            }
            catch
            {
                try
                {
                    App?.Kill();
                }
                catch { }
            }
            finally
            {
                Automation?.Dispose();
                _server?.Dispose();
                Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _previousDataRoot);
                try
                {
                    string target = Path.GetFullPath(SandboxPath);
                    string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests")) + Path.DirectorySeparatorChar;
                    if (target.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                        Directory.Delete(target, recursive: true);
                }
                catch { }
            }
        }
    }
}
