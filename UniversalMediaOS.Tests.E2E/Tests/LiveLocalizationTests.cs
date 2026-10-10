using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class LiveLocalizationTests
{
    [Fact]
    public void NewlyOpenedDownloadsUsesSelectedLanguageAndRestoresEnglish()
    {
        using var fixture = new AppFixture();
        string directory = Path.Combine(fixture.SandboxPath, "Local", "UniversalMediaOS", "Downloads");
        Directory.CreateDirectory(directory);
        // Listing fixture only; this file is never sent to the player.
        File.WriteAllBytes(Path.Combine(directory, "Local episode.mkv"), [0]);
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        Button Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(name))!.AsButton();
        void SelectLanguage(string label)
        {
            Window().FindFirstDescendant(cf => cf.ByAutomationId("OpenSettings"))!.AsButton().Invoke();
            Button(label == "Arabic" ? "Language" : "اللغة").Invoke();
            Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.ComboBox)) != null, TimeSpan.FromSeconds(3)));
            Window().FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox))!.AsComboBox().Select(label);
        }

        // Opening the view after changing language exercises loaded templates,
        // rather than translating a Downloads view that was already present.
        SelectLanguage("Arabic");
        Button("Downloads").Invoke();
        Assert.True(SpinWait.SpinUntil(() => Window().FindFirstDescendant(cf =>
            cf.ByAutomationId("DownloadsViewRoot"))?.FindFirstDescendant(cf => cf.ByText("التنزيلات")) != null,
            TimeSpan.FromSeconds(3)), "The newly opened Downloads header must use Arabic. " +
            string.Join("; ", Window().FindAllDescendants(cf => cf.ByControlType(ControlType.Custom)
                .Or(cf.ByText("Downloads")).Or(cf.ByText("التنزيلات")))
                .Select(element => $"{element.ControlType}/{element.AutomationId}/{element.Name}")));
        var downloads = Window().FindFirstDescendant(cf => cf.ByAutomationId("DownloadsViewRoot"))!;
        Assert.NotNull(downloads.FindFirstDescendant(cf => cf.ByText("تحديث")));
        Assert.Equal("أعد فحص مجلد التنزيل وتحديث القائمة.", downloads.FindFirstDescendant(cf =>
            cf.ByName("تحديث").And(cf.ByControlType(ControlType.Button)))!.Properties.HelpText.ValueOrDefault);
        Assert.NotNull(downloads.FindFirstDescendant(cf => cf.ByText("فتح المجلد")));
        Assert.Null(downloads.FindFirstDescendant(cf => cf.ByText("Open folder")));
        Assert.True(SpinWait.SpinUntil(() => downloads.FindFirstDescendant(cf => cf.ByText("تشغيل")) != null,
            TimeSpan.FromSeconds(3)), "Arabic file actions must appear after the asynchronous download-folder refresh.");
        Assert.NotNull(downloads.FindFirstDescendant(cf => cf.ByText("حذف")));
        Assert.NotNull(downloads.FindFirstDescendant(cf => cf.ByText("تشغيل")));
        Assert.NotNull(downloads.FindFirstDescendant(cf => cf.ByText("Local episode.mkv")));

        SelectLanguage("الإنجليزية");
        Button("Downloads").Invoke();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var view = Window().FindFirstDescendant(cf => cf.ByAutomationId("DownloadsViewRoot"));
            return view?.FindFirstDescendant(cf => cf.ByText("Open folder")) != null &&
                view.FindFirstDescendant(cf => cf.ByText("Refresh")) != null;
        }, TimeSpan.FromSeconds(3)), "Both English controls must appear in the intended Downloads view. " +
            string.Join("; ", Window().FindAllDescendants(cf => cf.ByText("Open folder").Or(cf.ByText("Refresh"))
                .Or(cf.ByText("فتح المجلد")).Or(cf.ByText("تحديث")))
                .Select(element => $"{element.ControlType}/{element.AutomationId}/{element.Name}")));
        downloads = Window().FindFirstDescendant(cf => cf.ByAutomationId("DownloadsViewRoot"))!;
        Assert.Equal("Rescan the download folder and refresh this list.", downloads.FindFirstDescendant(cf =>
            cf.ByName("Refresh").And(cf.ByControlType(ControlType.Button)))!.Properties.HelpText.ValueOrDefault);
        Assert.Null(Window().FindFirstDescendant(cf => cf.ByText("فتح المجلد")));
        Assert.True(SpinWait.SpinUntil(() => downloads.FindFirstDescendant(cf => cf.ByText("Play")) != null,
            TimeSpan.FromSeconds(3)), "English file actions must appear after the asynchronous download-folder refresh.");
    }
}
