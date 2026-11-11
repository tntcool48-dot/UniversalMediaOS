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
    public class SettingsTests
    {
        private readonly AppFixture _fixture;

        public SettingsTests(AppFixture fixture)
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
        public void Settings_EverySectionNamesItsInteractiveControls()
        {
            NavigateTo("Settings");
            var settings = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SettingsView"));
            Assert.NotNull(settings);
            var unnamed = new System.Collections.Generic.List<string>();
            foreach (string section in new[] { "Appearance", "Services", "qBittorrent", "MAL", "Providers & Scrapers", "Notifications", "Privacy", "Language" })
            {
                var open = settings.FindFirstDescendant(cf => cf.ByName(section).And(cf.ByControlType(ControlType.Button)))?.AsButton();
                Assert.True(open != null, $"Settings section button '{section}' is missing.");
                open.Invoke();
                Thread.Sleep(300);
                // A screen reader announces a control by its UI Automation name; an unnamed field is announced only by its type.
                foreach (var control in settings.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)
                             .Or(cf.ByControlType(ControlType.Edit)).Or(cf.ByControlType(ControlType.ComboBox))
                             .Or(cf.ByControlType(ControlType.CheckBox)).Or(cf.ByControlType(ControlType.Slider))
                             .Or(cf.ByControlType(ControlType.RadioButton)).Or(cf.ByControlType(ControlType.Hyperlink))))
                {
                    // Track parts inside sliders and scroll bars take no keyboard focus and are not announced.
                    if (control.IsOffscreen || !control.Properties.IsKeyboardFocusable.ValueOrDefault ||
                        !string.IsNullOrWhiteSpace(control.Name)) continue;
                    unnamed.Add($"{section}: {control.ControlType} id='{control.AutomationId}' help='{control.HelpText}'");
                }
            }
            Assert.True(unnamed.Count == 0, "Unnamed Settings controls:" + Environment.NewLine +
                string.Join(Environment.NewLine, System.Linq.Enumerable.Distinct(unnamed)));
        }

        [Fact]
        public void Settings_TabReachesEverySectionControlInOrderWithoutUnnamedStops()
        {
            // From each section's button, Tab must visit every visible control of that section once, in the order
            // they are laid out, and never stop on something a screen reader can only call "pane" or "group".
            using var fixture = new AppFixture();
            var problems = new System.Collections.Generic.List<string>();
            foreach (string section in new[] { "Appearance", "Services", "qBittorrent", "MAL", "Providers & Scrapers", "Notifications", "Privacy", "Language" })
            {
                OpenSection(fixture, section);
                var settings = SettingsRoot(fixture);
                var navigation = settings.FindFirstDescendant(cf => cf.ByName(section).And(cf.ByControlType(ControlType.Button)))!;
                var expected = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(settings.FindAllDescendants(),
                    control => control.Properties.IsKeyboardFocusable.ValueOrDefault && control.IsEnabled &&
                               !control.Properties.IsOffscreen.ValueOrDefault && !IsNavigation(settings, control) &&
                               control.ControlType is not (ControlType.Thumb or ControlType.ScrollBar or ControlType.Pane or ControlType.Group or ControlType.Custom)));
                FocusWithKeyboard(fixture, navigation);

                var visited = new System.Collections.Generic.List<AutomationElement>();
                for (int press = 0; press < 80; press++)
                {
                    FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
                    Thread.Sleep(60);
                    var focused = fixture.Automation.FocusedElement();
                    if (focused == null) { problems.Add($"{section}: focus went nowhere after {visited.Count} stops."); break; }
                    if (IsNavigation(settings, focused)) continue; // the remaining section buttons come first
                    if (!IsWithin(settings, focused)) break;       // left Settings: the section is done
                    if (System.Linq.Enumerable.Any(visited, seen => fixture.Automation.Compare(seen, focused)))
                    { problems.Add($"{section}: Tab returned to {Describe(focused)} before leaving Settings."); break; }
                    if (string.IsNullOrWhiteSpace(focused.Name))
                        problems.Add($"{section}: unnamed Tab stop {Describe(focused)}.");
                    visited.Add(focused);
                }

                foreach (var control in expected)
                    if (!System.Linq.Enumerable.Any(visited, seen => fixture.Automation.Compare(seen, control)))
                        problems.Add($"{section}: Tab never reached {Describe(control)}.");
                // Layout order: the reached controls appear in the same order as in the view's own tree.
                var order = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(visited,
                    seen => expected.FindIndex(control => fixture.Automation.Compare(control, seen))));
                for (int i = 1; i < order.Count; i++)
                    if (order[i] >= 0 && order[i - 1] >= 0 && order[i] < order[i - 1])
                        problems.Add($"{section}: Tab moved back from {Describe(visited[i - 1])} to {Describe(visited[i])}.");
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        /// <summary>The section buttons in the Settings sidebar (188 px wide at 100% scale).</summary>
        private static bool IsNavigation(AutomationElement settings, AutomationElement element) =>
            element.ControlType == ControlType.Button && Array.IndexOf(SettingsSections, element.Name) >= 0 &&
            IsWithin(settings, element) && element.Properties.BoundingRectangle.ValueOrDefault.Left < settings.BoundingRectangle.Left + 200;

        private static readonly string[] SettingsSections =
            ["Appearance", "Services", "qBittorrent", "MAL", "Providers & Scrapers", "Notifications", "Privacy", "Language"];

        private static bool IsWithin(AutomationElement root, AutomationElement element)
        {
            var walker = root.Automation.TreeWalkerFactory.GetControlViewWalker();
            for (var current = element; current != null; current = walker.GetParent(current))
                if (root.Automation.Compare(current, root)) return true;
            return false;
        }

        [Fact]
        public void Settings_AutoManageCheckbox_CanBeToggledAndRestored()
        {
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(_fixture.SandboxPath, "title.txt"), _fixture.MainWindow.Title);
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(_fixture.SandboxPath, "title_error.txt"), ex.ToString());
            }

            NavigateTo("Settings");
            var mainView = _fixture.MainWindow;
            Assert.NotNull(mainView);

            var servicesBtn = mainView.FindFirstDescendant(cf => cf.ByName("Services"))?.AsButton();
            Assert.NotNull(servicesBtn);
            servicesBtn.Invoke();
            Thread.Sleep(200);
            
            // Check for AutoManage checkbox/text
            var checkbox = mainView.FindFirstDescendant(cf => cf.ByAutomationId("AutoManageServices"))?.AsCheckBox();
            Assert.NotNull(checkbox);
            
            bool originalState = checkbox.IsChecked ?? false;
            checkbox.IsChecked = !originalState;
            Thread.Sleep(200);
            checkbox.IsChecked = originalState; // revert
        }

        [Fact]
        public void T1_Settings_02_Set_Domain_Hot_Swap_URL()
        {
            // Own profile: the shared application's settings never change.
            using var fixture = new AppFixture();
            const string seed = "https://seed.example.test/";
            OpenSection(fixture, "Providers & Scrapers");
            Field(fixture, "Scraper Seed URL").AsTextBox().Text = seed;
            SaveAndConfirm(fixture);
            Assert.Equal(seed, StoredSetting(fixture, "OtherMediaScraperUrl"));

            fixture.Restart();
            OpenSection(fixture, "Providers & Scrapers");
            Assert.Equal(seed, Field(fixture, "Scraper Seed URL").AsTextBox().Text);
        }

        [Fact]
        public async System.Threading.Tasks.Task T1_Settings_03_System_Resource_Check_Status_View()
        {
            // Services lists every local service. The HLS proxy line is the deterministic one here: the endpoint it
            // names must really answer. FFmpeg, qBittorrent and uBlock depend on the machine and are checked present.
            using var fixture = new AppFixture();
            OpenSection(fixture, "Services");
            string health = HealthText(fixture);
            foreach (string service in new[] { "Python scraper: ", "HLS proxy: ", "uBlock Origin: ", "FFmpeg: ", "qBittorrent: " })
                Assert.Contains(service, health, StringComparison.Ordinal);
            var listening = System.Text.RegularExpressions.Regex.Match(health, @"HLS proxy: Listening on (?:http://)?(?<endpoint>127\.0\.0\.1:\d+)");
            Assert.True(listening.Success, health);
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync($"http://{listening.Groups["endpoint"].Value}/health-probe");
            Assert.True((int)response.StatusCode is >= 200 and < 600);

            // Refresh re-reads the same running proxy.
            SettingsRoot(fixture).FindFirstDescendant(cf => cf.ByName("Refresh").And(cf.ByControlType(ControlType.Button)))!
                .AsButton().Invoke();
            Assert.True(SpinWait.SpinUntil(() => HealthText(fixture).Contains(listening.Value, StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)), HealthText(fixture));
        }

        [Fact]
        public void Settings_RepairServicesKeepsKeyboardFocusThroughItsBusyState()
        {
            // Repair services is unavailable while it runs. A keyboard user who pressed it must still be on it
            // afterwards, so Tab continues to the next control instead of starting over from the window.
            using var fixture = new AppFixture(mangaPagePng: null, initializeProfile: SeedPinnedUBlock);
            OpenSection(fixture, "Services");
            var repair = SettingsButton(fixture, "Repair services");
            FocusWithKeyboard(fixture, repair);

            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
            Assert.True(SpinWait.SpinUntil(() => !repair.IsEnabled, TimeSpan.FromSeconds(10)), "Repair services showed no busy state.");
            Assert.True(SpinWait.SpinUntil(() => repair.IsEnabled, TimeSpan.FromSeconds(150)), "Repair services did not finish.");
            Assert.True(SpinWait.SpinUntil(() => repair.Properties.HasKeyboardFocus.ValueOrDefault, TimeSpan.FromSeconds(5)),
                "Keyboard focus after Repair services: " + Describe(fixture.Automation.FocusedElement()));

            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            var focused = fixture.Automation.FocusedElement();
            Assert.True(focused?.Name == "Save Settings", "Tab after Repair services reached " + Describe(focused));
        }

        [Fact]
        public void Settings_SaveKeepsKeyboardFocusAfterItsConfirmation()
        {
            // Save is unavailable while it runs, and its confirmation opens meanwhile. Closing the confirmation
            // with the keyboard must leave focus on Save, not on the section's scroll pane.
            using var fixture = new AppFixture();
            OpenSection(fixture, "Services");
            var save = System.Linq.Enumerable.First(SettingsRoot(fixture).FindAllDescendants(
                cf => cf.ByName("Save Settings").And(cf.ByControlType(ControlType.Button))), button => !button.IsOffscreen).AsButton();
            FocusWithKeyboard(fixture, save);

            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
            Window? dialog = null;
            Assert.True(SpinWait.SpinUntil(() => (dialog = System.Linq.Enumerable.FirstOrDefault(MainWindowOf(fixture).ModalWindows,
                window => window.Title == "Settings Saved")) != null, TimeSpan.FromSeconds(10)), "No 'Settings Saved' confirmation appeared.");
            dialog!.SetForeground();
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
            Assert.True(SpinWait.SpinUntil(() => MainWindowOf(fixture).ModalWindows.Length == 0, TimeSpan.FromSeconds(5)),
                "The confirmation did not close from the keyboard.");

            Assert.True(SpinWait.SpinUntil(() => save.IsEnabled && save.Properties.HasKeyboardFocus.ValueOrDefault, TimeSpan.FromSeconds(5)),
                "Keyboard focus after Save: " + Describe(fixture.Automation.FocusedElement()));
        }

        [Fact]
        public void Settings_AddedDubProviderRowsAreNamedAndDeleteKeepsKeyboardPlace()
        {
            // Add Dub Provider reveals a row per provider. Each row's controls must say which provider they act on,
            // and deleting one from the keyboard must leave focus on the next row, or the add form when none is left.
            using var fixture = new AppFixture();
            OpenSection(fixture, "Providers & Scrapers");
            foreach (string provider in new[] { "Alpha Dubs", "Beta Dubs" })
            {
                Field(fixture, "Dub provider name").Patterns.Value.Pattern.SetValue(provider);
                Field(fixture, "Dub provider URL").Patterns.Value.Pattern.SetValue($"https://{provider[..4].ToLowerInvariant()}.example.test/search?q={{query}}");
                FocusWithKeyboard(fixture, SettingsButton(fixture, "Add Dub Provider"));
                FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
                Assert.True(SpinWait.SpinUntil(() => RowControl(fixture, ControlType.Slider, provider + " timeout") != null,
                    TimeSpan.FromSeconds(5)), $"No row appeared for {provider}. " + AddDiagnostics(fixture));
                Assert.True(RowControl(fixture, ControlType.CheckBox, provider + " enabled") != null,
                    $"{provider}'s Enabled check box does not name its provider: " + RowNames(fixture));
                Assert.True(RowControl(fixture, ControlType.Button, "Delete " + provider) != null,
                    $"{provider}'s Delete button does not name its provider: " + RowNames(fixture));
            }

            // Moving through the list, a screen reader announces each row by its list item's name.
            var walker = fixture.Automation.TreeWalkerFactory.GetControlViewWalker();
            foreach (string provider in new[] { "Alpha Dubs", "Beta Dubs" })
            {
                var item = walker.GetParent(RowControl(fixture, ControlType.Slider, provider + " timeout")!);
                Assert.True(item?.ControlType == ControlType.DataItem && item.Name == provider,
                    $"{provider}'s row is announced as {Describe(item)}.");
            }

            FocusWithKeyboard(fixture, RowControl(fixture, ControlType.Button, "Delete Alpha Dubs")!);
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
            Assert.True(SpinWait.SpinUntil(() => RowControl(fixture, ControlType.Button, "Delete Alpha Dubs") == null, TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => fixture.Automation.FocusedElement()?.Name == "Beta Dubs enabled", TimeSpan.FromSeconds(5)),
                "Focus after deleting Alpha Dubs: " + Describe(fixture.Automation.FocusedElement()));

            FocusWithKeyboard(fixture, RowControl(fixture, ControlType.Button, "Delete Beta Dubs")!);
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
            Assert.True(SpinWait.SpinUntil(() => fixture.Automation.FocusedElement()?.Name == "Dub provider name", TimeSpan.FromSeconds(5)),
                "Focus after deleting the last provider: " + Describe(fixture.Automation.FocusedElement()));
        }

        [Fact]
        public void ServicesOn_MissingToolsAndAStalledExtensionDownloadAreReportedWithoutBlockingTheApp()
        {
            // Services start with the window. PATH holds only Windows' own folders, so FFmpeg and ffprobe are missing,
            // and a loopback proxy accepts the uBlock download's connection but never answers. Each service must
            // settle on its own honest state, the app must stay usable meanwhile, and Repair must not claim success.
            using var proxy = new SilentProxy();
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            using var fixture = new AppFixture(mangaPagePng: null, initializeProfile: EnableServices,
                environment: new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["PATH"] = system + ";" + windows,
                    ["HTTPS_PROXY"] = proxy.Address, ["HTTP_PROXY"] = proxy.Address, ["ALL_PROXY"] = null,
                    ["NO_PROXY"] = "127.0.0.1,localhost"
                });
            var started = System.Diagnostics.Stopwatch.StartNew();
            OpenSection(fixture, "Services");

            // While the download hangs, FFmpeg's result is already published and Settings still navigates.
            Assert.True(SpinWait.SpinUntil(() => HealthText(fixture).Contains("FFmpeg: ffmpeg missing; ffprobe missing.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15)), HealthText(fixture));
            Assert.True(proxy.Connections > 0 || SpinWait.SpinUntil(() => proxy.Connections > 0, TimeSpan.FromSeconds(10)),
                "The uBlock download never reached the stalled proxy.");
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(18), $"The stall was already over after {started.Elapsed}.");
            OpenSection(fixture, "Appearance");
            OpenSection(fixture, "Services");
            Assert.Equal("Preparing services...", ServiceSummary(fixture));

            const string timedOut = "uBlock Origin: Timed out after 20 seconds during uBlock Origin download.";
            Assert.True(SpinWait.SpinUntil(() => HealthText(fixture).Contains(timedOut, StringComparison.Ordinal),
                TimeSpan.FromSeconds(40)), HealthText(fixture));
            Assert.True(SpinWait.SpinUntil(() => ServiceSummary(fixture) == "Service attention needed", TimeSpan.FromSeconds(130)),
                ServiceSummary(fixture) + Environment.NewLine + HealthText(fixture));
            // Python: without a launcher in Windows' own folders there is no interpreter to find.
            bool launcherInWindows = System.IO.File.Exists(System.IO.Path.Combine(windows, "py.exe")) ||
                                     System.IO.File.Exists(System.IO.Path.Combine(system, "py.exe"));
            if (!launcherInWindows)
                Assert.Contains("Python scraper: Failed (python_missing)", HealthText(fixture), StringComparison.Ordinal);
            Assert.DoesNotContain("Python scraper: Ready", HealthText(fixture), StringComparison.Ordinal);

            // Repair retries every service; with nothing changed it ends in the same honest states.
            int before = proxy.Connections;
            var repair = SettingsButton(fixture, "Repair services");
            repair.Invoke();
            Assert.True(SpinWait.SpinUntil(() => !repair.IsEnabled, TimeSpan.FromSeconds(5)), "Repair services showed no busy state.");
            Assert.True(SpinWait.SpinUntil(() => repair.IsEnabled, TimeSpan.FromSeconds(150)), "Repair services did not finish.");
            Assert.True(proxy.Connections > before, "Repair did not retry the uBlock download.");
            Assert.Contains(timedOut, HealthText(fixture), StringComparison.Ordinal);
            Assert.Contains("FFmpeg: ffmpeg missing; ffprobe missing.", HealthText(fixture), StringComparison.Ordinal);
            Assert.Equal("Service attention needed", ServiceSummary(fixture));
            Assert.False(System.IO.Directory.Exists(System.IO.Path.Combine(fixture.SandboxPath, "Local", "UniversalMediaOS", "Extensions", "ublock-origin")),
                "A failed download left an extension folder behind.");
        }

        [Fact]
        public void ServicesOn_PythonRemovedAfterReadinessIsReportedAndRepairRecoversIt()
        {
            // A venv interpreter is the only Python on PATH. Once Settings shows it Ready, its folder is moved away
            // while the app runs, as an uninstall or a moved environment does. Refresh must say Python is missing,
            // and once it is back, Repair must make it Ready again.
            string venv = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UniversalMediaOS.Tests", "python-" + Guid.NewGuid().ToString("N"));
            string away = venv + "-away";
            CreateVenv(venv);
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            try
            {
                using var fixture = new AppFixture(mangaPagePng: null, initializeProfile: root => { EnableServices(root); SeedPinnedUBlock(root); },
                    environment: new System.Collections.Generic.Dictionary<string, string?>
                    {
                        ["PATH"] = System.IO.Path.Combine(venv, "Scripts") + ";" + system + ";" + windows,
                        ["PYTHONHOME"] = null, ["PYTHONPATH"] = null, ["VIRTUAL_ENV"] = null
                    });
                OpenSection(fixture, "Services");
                // Where the pins are missing (as on the CI runner) the app installs them first.
                Assert.True(SpinWait.SpinUntil(() => HealthText(fixture).Contains("Python scraper: Ready (ready)", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(150)), HealthText(fixture));

                MoveWithRetry(venv, away);
                SettingsButton(fixture, "Refresh").Invoke();
                Assert.True(SpinWait.SpinUntil(() => HealthText(fixture).Contains("Python scraper: Failed (python_missing)", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5)), HealthText(fixture));
                Assert.Equal("Service attention needed", ServiceSummary(fixture));

                MoveWithRetry(away, venv);
                var repair = SettingsButton(fixture, "Repair services");
                repair.Invoke();
                Assert.True(SpinWait.SpinUntil(() => HealthText(fixture).Contains("Python scraper: Ready (ready)", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(150)), HealthText(fixture));
                Assert.True(SpinWait.SpinUntil(() => repair.IsEnabled, TimeSpan.FromSeconds(60)), "Repair services did not finish.");
            }
            finally
            {
                foreach (string folder in new[] { venv, away })
                    try { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, recursive: true); } catch (Exception) { }
            }
        }

        /// <summary>Makes a venv from the Python on this machine's PATH that also sees its installed packages.</summary>
        private static void CreateVenv(string folder)
        {
            foreach (string python in new[] { "python", "py" })
            {
                try
                {
                    var start = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                    foreach (string argument in new[] { "-m", "venv", "--without-pip", "--system-site-packages", folder }) start.ArgumentList.Add(argument);
                    using var process = System.Diagnostics.Process.Start(start)!;
                    string error = process.StandardError.ReadToEnd();
                    if (process.WaitForExit(60000) && process.ExitCode == 0 &&
                        System.IO.File.Exists(System.IO.Path.Combine(folder, "Scripts", "python.exe"))) return;
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
            throw new InvalidOperationException("This case needs Python 3.10 or newer on PATH to make a venv.");
        }

        private static void MoveWithRetry(string from, string to)
        {
            // Antivirus scanners can hold a just-written interpreter for a moment.
            for (int attempt = 0; ; attempt++)
            {
                try { System.IO.Directory.Move(from, to); return; }
                catch (Exception ex) when (attempt < 20 && ex is System.IO.IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
            }
        }

        private static void EnableServices(string root)
        {
            string path = System.IO.Path.Combine(root, "Roaming", "UniversalMediaOS", "config.json");
            var settings = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(
                System.IO.File.ReadAllText(path))!;
            settings["AutoManageServices"] = "true";
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(settings));
        }

        private static string ServiceSummary(AppFixture fixture) => System.Linq.Enumerable.FirstOrDefault(
            MainWindowOf(fixture).FindAllDescendants(cf => cf.ByControlType(ControlType.Text)),
            text => text.Name is "Service checks passed" or "Service attention needed" or "Preparing services..." or "Service checks pending")?.Name ?? string.Empty;

        /// <summary>A loopback HTTP proxy that accepts connections and never answers, like a stalled network.</summary>
        private sealed class SilentProxy : IDisposable
        {
            private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
            private readonly System.Collections.Concurrent.ConcurrentBag<System.Net.Sockets.TcpClient> _held = new();
            private int _connections;
            public SilentProxy()
            {
                _listener.Start();
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { while (true) { _held.Add(await _listener.AcceptTcpClientAsync()); Interlocked.Increment(ref _connections); } }
                    catch (Exception) { /* Stopped. */ }
                });
            }
            public string Address => "http://" + _listener.LocalEndpoint;
            public int Connections => Volatile.Read(ref _connections);
            public void Dispose()
            {
                _listener.Stop();
                foreach (var client in _held) client.Dispose();
            }
        }

        private static AutomationElement? RowControl(AutomationElement list, ControlType type, string name) =>
            list.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(type)));

        private static AutomationElement? RowControl(AppFixture fixture, ControlType type, string name) =>
            RowControl(SettingsRoot(fixture), type, name);

        private static string AddDiagnostics(AppFixture fixture) =>
            "Rows: " + RowNames(fixture) + "; dialogs: " + string.Join(", ", System.Linq.Enumerable.Select(MainWindowOf(fixture).ModalWindows, w => w.Title)) +
            "; log: " + string.Join(" | ", System.Linq.Enumerable.Where(ReadShared(System.IO.Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log"))
                .Split('\n'), line => line.Contains("DubAvailabilityProvider", StringComparison.Ordinal)));

        private static string RowNames(AppFixture fixture) => string.Join(", ", System.Linq.Enumerable.Select(
            System.Linq.Enumerable.Where(SettingsRoot(fixture).FindAllDescendants(), e => e.Properties.IsKeyboardFocusable.ValueOrDefault &&
                (e.Name.Contains("Dubs", StringComparison.Ordinal) || e.Name is "Enabled" or "Delete")),
            e => $"{e.ControlType} '{e.Name}'"));

        /// <summary>Installs the pinned uBlock manifest in the profile, so Repair verifies it instead of downloading.</summary>
        private static void SeedPinnedUBlock(string root)
        {
            string extension = System.IO.Path.Combine(root, "Local", "UniversalMediaOS", "Extensions", "ublock-origin");
            System.IO.Directory.CreateDirectory(extension);
            System.IO.File.WriteAllText(System.IO.Path.Combine(extension, "manifest.json"), "{\"version\":\"1.71.0\"}");
        }

        private static Button SettingsButton(AppFixture fixture, string name) =>
            SettingsRoot(fixture).FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button)))?.AsButton()
            ?? throw new InvalidOperationException($"Settings button '{name}' is missing.");

        /// <summary>Puts keyboard focus on an element of the foreground main window, as Tab would.</summary>
        private static void FocusWithKeyboard(AppFixture fixture, AutomationElement element)
        {
            MainWindowOf(fixture).SetForeground();
            element.Focus();
            Assert.True(SpinWait.SpinUntil(() => element.Properties.HasKeyboardFocus.ValueOrDefault, TimeSpan.FromSeconds(5)),
                $"'{element.Name}' did not take keyboard focus; focus is on " + Describe(fixture.Automation.FocusedElement()));
        }

        private static string Describe(AutomationElement? element) => element == null ? "nothing" :
            $"{element.Properties.ControlType.ValueOrDefault} '{element.Properties.Name.ValueOrDefault}' " +
            $"id='{element.Properties.AutomationId.ValueOrDefault}' class='{element.Properties.ClassName.ValueOrDefault}'";

        private static string HealthText(AppFixture fixture) => System.Linq.Enumerable.FirstOrDefault(
            SettingsRoot(fixture).FindAllDescendants(cf => cf.ByControlType(ControlType.Text)),
            text => text.Name.StartsWith("Python scraper: ", StringComparison.Ordinal))?.Name ?? string.Empty;

        [Fact]
        public void Settings_ServicesView_ExposesEnabledClearDebugLogAction()
        {
            NavigateTo("Settings");
            var servicesBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Services"))?.AsButton();
            Assert.NotNull(servicesBtn);
            servicesBtn.Invoke();
            Thread.Sleep(200);

            var clearLogBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Clear debug log"))?.AsButton();
            Assert.NotNull(clearLogBtn);
            Assert.True(clearLogBtn.IsEnabled);
        }

        [Fact]
        public void T1_Settings_05_Encrypt_and_Save_API_Keys()
        {
            using var fixture = new AppFixture();
            const string key = "ui-google-books-key-4815";
            OpenSection(fixture, "Providers & Scrapers");
            Field(fixture, "Google Books API key").Patterns.Value.Pattern.SetValue(key);
            SaveAndConfirm(fixture);

            // The key is stored protected for this Windows account, never as plaintext, and reads back.
            string stored = StoredSetting(fixture, "GoogleBooksApiKey");
            Assert.StartsWith("dpapi:v1:", stored, StringComparison.Ordinal);
            Assert.DoesNotContain(key, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(key, ReadShared(ConfigPath(fixture)), StringComparison.Ordinal);
            Assert.Equal(key, new UniversalMediaOS.Core.Configuration.DomainHotSwapper(ConfigPath(fixture)).GetSetting("GoogleBooksApiKey"));
        }

        [Fact]
        public void T2_Settings_01_Duplicate_Service_Bootstrapping()
        {
            // A second launch on the same profile brings the running window forward and exits before it starts
            // services, so the profile keeps one download queue, one settings writer and one set of services.
            using var fixture = new AppFixture();
            FlaUI.Core.Patterns.IWindowPattern WindowPattern() => MainWindowOf(fixture).Patterns.Window.Pattern;
            WindowPattern().SetWindowVisualState(WindowVisualState.Minimized);
            Assert.True(SpinWait.SpinUntil(() => WindowPattern().WindowVisualState.Value == WindowVisualState.Minimized,
                TimeSpan.FromSeconds(5)), "The running window did not minimize.");

            string exe = System.Diagnostics.Process.GetProcessById(fixture.App.ProcessId).MainModule!.FileName;
            var start = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!
            };
            start.Environment["UNIVERSAL_MEDIA_OS_DATA_ROOT"] = fixture.SandboxPath;
            using var later = System.Diagnostics.Process.Start(start)!;
            try
            {
                Assert.True(later.WaitForExit(15000), "The second launch kept running beside the first.");
                Assert.Equal(0, later.ExitCode);
            }
            finally
            {
                if (!later.HasExited) later.Kill(entireProcessTree: true);
            }

            Assert.True(SpinWait.SpinUntil(() => WindowPattern().WindowVisualState.Value != WindowVisualState.Minimized,
                TimeSpan.FromSeconds(5)), "The running window was not brought back.");
            Assert.False(fixture.App.HasExited);
            string log = ReadShared(System.IO.Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log"));
            Assert.Contains("[Startup] Another launch on this profile brought the running window forward.", log, StringComparison.Ordinal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(log, @"Application session started\."));
        }

        [Fact]
        public void T2_Settings_02_Log_File_Rotation_Boundary()
        {
            // Startup clears a debug log larger than 5 MB and keeps one of exactly 5 MB; Settings shows its size and clears it.
            const int limit = 5 * 1024 * 1024;
            const string marker = "OLD-LOG-MARKER\n";
            static string LogPath(string root) => System.IO.Path.Combine(root, "Roaming", "UniversalMediaOS", "app.log");
            foreach (int size in new[] { limit + 1, limit })
            {
                using var fixture = new AppFixture(mangaPagePng: null, initializeProfile: root =>
                    System.IO.File.WriteAllText(LogPath(root), marker + new string('x', size - marker.Length)));
                string log = LogPath(fixture.SandboxPath);
                Assert.True(SpinWait.SpinUntil(() => ReadShared(log).Contains("Application session started.", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10)), "The new session was not logged.");
                string started = ReadShared(log);
                OpenSection(fixture, "Services");
                if (size > limit)
                {
                    Assert.Contains("Log cleared due to size constraints (exceeded 5MB).", started.Split('\n')[0], StringComparison.Ordinal);
                    Assert.DoesNotContain("OLD-LOG-MARKER", started, StringComparison.Ordinal);
                    Assert.EndsWith(" KB", LogSizeText(fixture), StringComparison.Ordinal);
                    continue;
                }

                Assert.StartsWith(marker, started, StringComparison.Ordinal);
                Assert.Equal("Debug log size: 5.00 MB", LogSizeText(fixture));
                SettingsRoot(fixture).FindFirstDescendant(cf => cf.ByName("Clear debug log").And(cf.ByControlType(ControlType.Button)))!
                    .AsButton().Invoke();
                AnswerMessageBox(fixture, "Clear Debug Log", new[] { "Clear Debug Log", "Logs" }, "6");
                AnswerMessageBox(fixture, "Logs", new[] { "Logs" }, "2");
                string cleared = ReadShared(log);
                Assert.Contains("Log manually cleared.", cleared.Split('\n')[0], StringComparison.Ordinal);
                Assert.DoesNotContain("OLD-LOG-MARKER", cleared, StringComparison.Ordinal);
                Assert.True(SpinWait.SpinUntil(() => LogSizeText(fixture).EndsWith(" KB", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5)), LogSizeText(fixture));
            }
        }

        [Fact]
        public void T2_Settings_03_Settings_Lock_Collision()
        {
            // Another program holds config.json open without delete sharing, as a sync or backup tool can, so the
            // replace fails. Settings reports it and changes nothing on disk; the same Save succeeds once it is released.
            using var fixture = new AppFixture();
            const string seed = "https://locked.example.test/";
            string config = ConfigPath(fixture);
            OpenSection(fixture, "Providers & Scrapers");
            Field(fixture, "Scraper Seed URL").AsTextBox().Text = seed;
            byte[] before = System.IO.File.ReadAllBytes(config);
            using (new System.IO.FileStream(config, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
            {
                SaveAndConfirm(fixture, "Save Error");
                Assert.Equal(before, System.IO.File.ReadAllBytes(config));
            }
            string log = System.IO.Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
            Assert.Contains("Failed to save config synchronously", ReadShared(log), StringComparison.Ordinal);

            SaveAndConfirm(fixture);
            Assert.Equal(seed, StoredSetting(fixture, "OtherMediaScraperUrl"));
            Assert.False(System.IO.File.Exists(config + ".tmp"), "A temporary settings file was left behind.");
        }

        [Fact(Skip = "Excluded by the user (2026-10-24): the app has no low-memory behavior left to test. 182da1b removed the startup " +
                     "RAM check and Settings' resource line; SystemResourceCheck is unused. See docs/qa/packaged-runtime-services-settings-2026-10-23.md.")]
        public void T2_Settings_04_Low_Memory_Resource_Boundary()
        {
            NavigateTo("Settings");
            var saveBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Save Settings"))?.AsButton();
            Assert.NotNull(saveBtn);
        }

        [Fact]
        public void T2_Settings_05_DPAPI_Cryptography_Failure_Fallback()
        {
            // Keys protected for another Windows account or computer cannot be decrypted here; extra entropy
            // fails decryption the same way. One uses the current marked format, the other the legacy one.
            string foreign = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes("other-account-key"), new byte[] { 7, 7, 7 },
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
            string foreignBooks = "dpapi:v1:" + foreign;
            using var fixture = new AppFixture(mangaPagePng: null, initializeProfile: root =>
            {
                string path = System.IO.Path.Combine(root, "Roaming", "UniversalMediaOS", "config.json");
                var settings = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(
                    System.IO.File.ReadAllText(path))!;
                settings["GoogleBooksApiKey"] = foreignBooks;
                settings["TmdbApiKey"] = foreign;
                System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(settings));
            });
            string log = System.IO.Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log");
            foreach (string key in new[] { "GoogleBooksApiKey", "TmdbApiKey" })
                Assert.True(SpinWait.SpinUntil(() => ReadShared(log).Contains(
                        $"Protected setting '{key}' was saved by another Windows account or computer", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10)), $"No unreadable-key warning for {key}:{Environment.NewLine}{ReadShared(log)}");

            // Saving Settings without entering keys keeps both values, so moving the profile back still reads them.
            OpenSection(fixture, "Providers & Scrapers");
            SaveAndConfirm(fixture);
            Assert.Equal(foreignBooks, StoredSetting(fixture, "GoogleBooksApiKey"));
            Assert.Equal(foreign, StoredSetting(fixture, "TmdbApiKey"));

            // A key entered here replaces the unreadable one and reads back; the untouched one is kept.
            Field(fixture, "Google Books API key").Patterns.Value.Pattern.SetValue("replacement-books-key");
            SaveAndConfirm(fixture);
            Assert.StartsWith("dpapi:v1:", StoredSetting(fixture, "GoogleBooksApiKey"), StringComparison.Ordinal);
            Assert.Equal("replacement-books-key",
                new UniversalMediaOS.Core.Configuration.DomainHotSwapper(ConfigPath(fixture)).GetSetting("GoogleBooksApiKey"));
            Assert.Equal(foreign, StoredSetting(fixture, "TmdbApiKey"));
        }

        private static Window MainWindowOf(AppFixture fixture) => System.Linq.Enumerable.Single(
            fixture.App.GetAllTopLevelWindows(fixture.Automation), window => window.Title == AppFixture.ExpectedMainWindowTitle);

        private static void OpenSection(AppFixture fixture, string section)
        {
            MainWindowOf(fixture).FindFirstDescendant(cf => cf.ByAutomationId("OpenSettings"))!.AsButton().Invoke();
            AutomationElement? settings = null;
            Assert.True(SpinWait.SpinUntil(() => (settings = MainWindowOf(fixture).FindFirstDescendant(
                cf => cf.ByAutomationId("SettingsView"))) != null, TimeSpan.FromSeconds(10)), "Settings did not open.");
            settings!.FindFirstDescendant(cf => cf.ByName(section).And(cf.ByControlType(ControlType.Button)))!.AsButton().Invoke();
            Thread.Sleep(300);
        }

        private static AutomationElement SettingsRoot(AppFixture fixture) =>
            MainWindowOf(fixture).FindFirstDescendant(cf => cf.ByAutomationId("SettingsView"))
            ?? throw new InvalidOperationException("Settings is not open.");

        private static AutomationElement Field(AppFixture fixture, string name) =>
            SettingsRoot(fixture).FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Edit)))
            ?? throw new InvalidOperationException($"Settings field '{name}' is missing.");

        private static string LogSizeText(AppFixture fixture) => System.Linq.Enumerable.FirstOrDefault(
            SettingsRoot(fixture).FindAllDescendants(cf => cf.ByControlType(ControlType.Text)),
            text => text.Name.StartsWith("Debug log size: ", StringComparison.Ordinal))?.Name ?? string.Empty;

        /// <summary>Saves through the visible Save Settings button and dismisses the confirmation, as a user does.</summary>
        private static void SaveAndConfirm(AppFixture fixture, string expected = "Settings Saved")
        {
            var saves = SettingsRoot(fixture)
                .FindAllDescendants(cf => cf.ByName("Save Settings").And(cf.ByControlType(ControlType.Button)));
            (System.Linq.Enumerable.FirstOrDefault(saves, button => !button.IsOffscreen) ?? saves[0]).AsButton().Invoke();
            AnswerMessageBox(fixture, expected, new[] { "Settings Saved", "Save Error", "Validation Error" }, "2");
        }

        /// <summary>Waits for one of the message boxes the action can show, which is owned by the main window,
        /// requires the expected one and presses the button with the given Win32 ID (2 OK/Cancel, 6 Yes).</summary>
        private static void AnswerMessageBox(AppFixture fixture, string expected, string[] possible, string buttonId)
        {
            Window? dialog = null;
            Window? Dialog(string[] titles) => System.Linq.Enumerable.FirstOrDefault(
                System.Linq.Enumerable.Concat(MainWindowOf(fixture).ModalWindows, fixture.App.GetAllTopLevelWindows(fixture.Automation)),
                window => Array.IndexOf(titles, window.Title) >= 0);
            Assert.True(SpinWait.SpinUntil(() => (dialog = Dialog(possible)) != null, TimeSpan.FromSeconds(10)), $"No '{expected}' message appeared.");
            Assert.True(dialog!.Title == expected, $"'{dialog.Title}' appeared instead of '{expected}'.");
            dialog.FindFirstDescendant(cf => cf.ByAutomationId(buttonId).And(cf.ByControlType(ControlType.Button)))!.AsButton().Invoke();
            Assert.True(SpinWait.SpinUntil(() => Dialog(new[] { expected }) == null, TimeSpan.FromSeconds(5)), $"'{expected}' did not close.");
        }

        private static string ConfigPath(AppFixture fixture) =>
            System.IO.Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "config.json");

        private static string StoredSetting(AppFixture fixture, string key)
        {
            using var document = System.Text.Json.JsonDocument.Parse(ReadShared(ConfigPath(fixture)));
            return document.RootElement.TryGetProperty(key, out var value) ? value.GetString() ?? string.Empty : string.Empty;
        }

        private static string ReadShared(string path)
        {
            if (!System.IO.File.Exists(path)) return string.Empty;
            using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
