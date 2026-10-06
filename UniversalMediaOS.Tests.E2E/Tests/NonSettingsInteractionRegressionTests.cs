using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class NonSettingsInteractionRegressionTests
    {
        [Fact]
        public void Player_ExposesManualUrlAndFileActionsAndStartsTheStagedSource()
        {
            string root = FindWorkspaceRoot();
            XDocument view = XDocument.Load(Path.Combine(
                root,
                "UniversalMediaOS.WPF",
                "Views",
                "PlaybackView.xaml"));

            Assert.Contains(view.Descendants(), element =>
                element.Name.LocalName == "Button" &&
                (string?)element.Attribute("AutomationProperties.Name") == "Open media URL or path" &&
                (string?)element.Attribute("Command") == "{Binding OpenSourceCommand}");
            Assert.Contains(view.Descendants(), element =>
                element.Name.LocalName == "Button" &&
                (string?)element.Attribute("AutomationProperties.Name") == "Choose a local media file" &&
                (string?)element.Attribute("Click") == "OpenMediaFileButton_Click");

            string source = File.ReadAllText(Path.Combine(
                root,
                "UniversalMediaOS.WPF",
                "ViewModels",
                "PlaybackViewModel.cs"));
            int methodStart = source.IndexOf("private void OpenSource()", StringComparison.Ordinal);
            int methodEnd = source.IndexOf("private bool CanOpenSource()", methodStart, StringComparison.Ordinal);
            Assert.True(methodStart >= 0 && methodEnd > methodStart);
            string method = source[methodStart..methodEnd];
            Assert.True(
                method.IndexOf("LoadMedia(", StringComparison.Ordinal) <
                method.IndexOf("PlayPending();", StringComparison.Ordinal));
        }

        [Fact]
        public void Manga_ResultAndChapterActionsUseKeyboardAccessibleButtons()
        {
            string root = FindWorkspaceRoot();
            XDocument view = XDocument.Load(Path.Combine(
                root,
                "UniversalMediaOS.WPF",
                "Views",
                "MangaView.xaml"));

            Assert.Contains(view.Descendants(), element =>
                element.Name.LocalName == "Button" &&
                ((string?)element.Attribute("Command"))?.Contains("ReadCommand", StringComparison.Ordinal) == true);
            Assert.Contains(view.Descendants(), element =>
                element.Name.LocalName == "Button" &&
                ((string?)element.Attribute("Command"))?.Contains("SelectChapterCommand", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(view.Descendants(), element => element.Name.LocalName == "MouseBinding");
        }

        [Fact]
        public void StatefulActionsDeclareCanExecuteAndRefreshTabMoveState()
        {
            string root = FindWorkspaceRoot();
            string watchTogether = File.ReadAllText(Path.Combine(
                root,
                "UniversalMediaOS.WPF",
                "ViewModels",
                "WatchTogetherViewModel.cs"));
            foreach (string predicate in new[]
            {
                "CanExecute = nameof(CanStartRelay)",
                "CanExecute = nameof(CanStopRelay)",
                "CanExecute = nameof(CanConnect)",
                "CanExecute = nameof(CanDisconnect)",
                "CanExecute = nameof(CanOpenHostUrl)"
            })
            {
                Assert.Contains(predicate, watchTogether, StringComparison.Ordinal);
            }

            string main = File.ReadAllText(Path.Combine(
                root,
                "UniversalMediaOS.WPF",
                "ViewModels",
                "MainViewModel.cs"));
            Assert.Contains("CanExecute = nameof(CanMoveTabLeft)", main, StringComparison.Ordinal);
            Assert.Contains("CanExecute = nameof(CanMoveTabRight)", main, StringComparison.Ordinal);
            Assert.Contains("Tabs.CollectionChanged", main, StringComparison.Ordinal);
            Assert.True(main.Split("MoveTabLeftCommand.NotifyCanExecuteChanged();").Length >= 4);
            Assert.True(main.Split("MoveTabRightCommand.NotifyCanExecuteChanged();").Length >= 4);
        }

        private static string FindWorkspaceRoot()
        {
            foreach (string startingPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var candidate = new DirectoryInfo(startingPath);
                while (candidate != null)
                {
                    if (File.Exists(Path.Combine(candidate.FullName, "UniversalMediaOS.sln")))
                    {
                        return candidate.FullName;
                    }

                    candidate = candidate.Parent;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the UniversalMediaOS workspace root.");
        }
    }
}
