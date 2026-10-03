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

        [Fact(Skip = "Requires editing and reloading a domain setting; the previous check only clicked Save Settings.")]
        public void T1_Settings_02_Set_Domain_Hot_Swap_URL()
        {
            NavigateTo("Settings");
            var saveBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Save Settings"))?.AsButton();
            Assert.NotNull(saveBtn);
            saveBtn.Invoke();
            Thread.Sleep(500);
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

        [Fact(Skip = "Covered by CoreHardeningRegressionTests; the previous UI check did not save or inspect a secret.")]
        public void T1_Settings_05_Encrypt_and_Save_API_Keys()
        {
            NavigateTo("Settings");
            var saveBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Save Settings"))?.AsButton();
            Assert.NotNull(saveBtn);
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

        [Fact(Skip = "Requires an injectable DPAPI failure; the previous check did not execute cryptography.")]
        public void T2_Settings_05_DPAPI_Cryptography_Failure_Fallback()
        {
            NavigateTo("Settings");
            var saveBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Save Settings"))?.AsButton();
            Assert.NotNull(saveBtn);
        }
    }
}
