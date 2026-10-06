using System.Text.Json;
using FlaUI.Core.AutomationElements;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MangaPaginationUiTests
{
    [Fact]
    public void IncompleteChapterChoicesRemainReadableAndRetryReplacesTheWarning()
    {
        bool retry = false;
        using var fixture = new AppFixture(mangaPagePng: null, mangaChapterFeed: request =>
        {
            int offset = int.Parse(request.QueryString["offset"]!);
            if (offset > 0 && !Volatile.Read(ref retry)) return (503, "{\"error\":\"fixture unavailable\"}");
            return (200, JsonSerializer.Serialize(new
            {
                total = 3,
                data = Enumerable.Range(offset + 1, offset == 0 ? 2 : 1).Select(number => new
                {
                    id = $"fixture-chapter-{number}",
                    attributes = new { chapter = number.ToString(), title = $"Chapter {number}", pages = 2 }
                })
            }));
        });
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Name == AppFixture.ExpectedMainWindowTitle);
        Button Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(name))!.AsButton();
        const string warning = "Some chapters could not load. Showing available chapters. Go back and retry.";
        bool Has(string name) => Window().FindFirstDescendant(cf => cf.ByName(name)) != null;
        void WaitFor(string name) => Assert.True(SpinWait.SpinUntil(() => Has(name), TimeSpan.FromSeconds(5)), name);

        Button("Library").Invoke();
        WaitFor("Open Mock Manga chapters");
        Button("Open Mock Manga chapters").Invoke();
        WaitFor(warning);
        Assert.True(Has("Open chapter 1"));
        Assert.True(Has("Open chapter 2"));
        Assert.False(Has("Open chapter 3"));
        Button("Open chapter 1").Invoke();
        WaitFor("Mock Manga › Ch. 1");
        Button("Back").Invoke();
        WaitFor(warning);
        Button("Back").Invoke();
        WaitFor("Open Mock Manga chapters");
        Volatile.Write(ref retry, true);
        Button("Open Mock Manga chapters").Invoke();
        WaitFor("Open chapter 3");
        Assert.False(Has(warning));
        Assert.False(fixture.App.HasExited);
    }
}
