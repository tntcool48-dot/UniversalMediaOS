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

        [Fact(Skip = "Requires a controllable download-engine fixture; the previous check only opened the Downloads view.")]
        public void T1_Downloads_02_Pause_and_Resume_Download()
        {
            NavigateTo("Downloads");
            var refreshBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Refresh"))?.AsButton();
            Assert.NotNull(refreshBtn);
        }

        [Fact(Skip = "Requires a controllable download-engine fixture; the previous check only opened the Downloads view.")]
        public void T1_Downloads_03_Download_Progress_Updates()
        {
            NavigateTo("Downloads");
            var refreshBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Refresh"))?.AsButton();
            Assert.NotNull(refreshBtn);
        }

        [Fact(Skip = "Requires a disposable download fixture; the previous check only opened the Downloads view.")]
        public void T1_Downloads_04_Cancel_Download_and_Clean_Files()
        {
            NavigateTo("Downloads");
            var refreshBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Refresh"))?.AsButton();
            Assert.NotNull(refreshBtn);
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

        [Fact(Skip = "Requires a restartable download fixture; the previous check did not interrupt a download.")]
        public void T2_Downloads_05_Interrupted_Download_Recovery()
        {
            NavigateTo("Downloads");
            var refreshBtn = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByName("Refresh"))?.AsButton();
            Assert.NotNull(refreshBtn);
        }
    }
}
