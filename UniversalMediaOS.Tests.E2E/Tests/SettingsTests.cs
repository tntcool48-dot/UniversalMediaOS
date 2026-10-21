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

        [Fact(Skip = "Requires deterministic resource probes; the previous check only located Save Settings.")]
        public void T1_Settings_03_System_Resource_Check_Status_View()
        {
            NavigateTo("Settings");
            var saveBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Save Settings"))?.AsButton();
            Assert.NotNull(saveBtn);
        }

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

        [Fact(Skip = "Requires an injectable service bootstrapper; the previous check only located Save Settings.")]
        public void T2_Settings_01_Duplicate_Service_Bootstrapping()
        {
            NavigateTo("Settings");
            var saveBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Save Settings"))?.AsButton();
            Assert.NotNull(saveBtn);
        }

        [Fact(Skip = "Requires an injectable log sink and size boundary; the previous check did not write logs.")]
        public void T2_Settings_02_Log_File_Rotation_Boundary()
        {
            NavigateTo("Settings");
            var saveBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Save Settings"))?.AsButton();
            Assert.NotNull(saveBtn);
        }

        [Fact(Skip = "Requires concurrent settings writers; the previous check did not create a lock collision.")]
        public void T2_Settings_03_Settings_Lock_Collision()
        {
            NavigateTo("Settings");
            var saveBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Save Settings"))?.AsButton();
            Assert.NotNull(saveBtn);
        }

        [Fact(Skip = "Requires an injectable resource monitor; the previous check did not create a low-memory condition.")]
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

        private static AutomationElement Field(AppFixture fixture, string name) =>
            MainWindowOf(fixture).FindFirstDescendant(cf => cf.ByAutomationId("SettingsView"))!
                .FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Edit)))
            ?? throw new InvalidOperationException($"Settings field '{name}' is missing.");

        /// <summary>Saves through the visible Save Settings button and dismisses the confirmation, as a user does.</summary>
        private static void SaveAndConfirm(AppFixture fixture)
        {
            var saves = MainWindowOf(fixture).FindFirstDescendant(cf => cf.ByAutomationId("SettingsView"))!
                .FindAllDescendants(cf => cf.ByName("Save Settings").And(cf.ByControlType(ControlType.Button)));
            (System.Linq.Enumerable.FirstOrDefault(saves, button => !button.IsOffscreen) ?? saves[0]).AsButton().Invoke();

            // The confirmation, or the error that replaces it, is a message box owned by the main window.
            Window? dialog = null;
            Window? Dialog() => System.Linq.Enumerable.FirstOrDefault(
                System.Linq.Enumerable.Concat(MainWindowOf(fixture).ModalWindows, fixture.App.GetAllTopLevelWindows(fixture.Automation)),
                window => window.Title is "Settings Saved" or "Save Error" or "Validation Error");
            Assert.True(SpinWait.SpinUntil(() => (dialog = Dialog()) != null, TimeSpan.FromSeconds(10)), "Saving showed no confirmation.");
            Assert.True(dialog!.Title == "Settings Saved", $"Saving showed '{dialog.Title}' instead of the confirmation.");
            dialog.FindFirstDescendant(cf => cf.ByAutomationId("2").And(cf.ByControlType(ControlType.Button)))!.AsButton().Invoke();
            Assert.True(SpinWait.SpinUntil(() => Dialog() == null, TimeSpan.FromSeconds(5)), "The confirmation did not close.");
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
