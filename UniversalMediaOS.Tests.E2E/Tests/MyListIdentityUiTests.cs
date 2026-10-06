using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MyListIdentityUiTests
{
    [Fact]
    public async Task SameTitleFilmAndTelevisionRemainDistinguishableAndReopenWithIndependentStatusAfterRestart()
    {
        var film = new AudiovisualIdentity { Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature,
            PrimaryId = new("fixture", "movie", "42"), Title = "Shared title", Year = 2010 };
        var television = film with { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series,
            PrimaryId = new("fixture", "show", "42") };
        var filmKey = AudiovisualLibraryKey.Create(film);
        var tvKey = AudiovisualLibraryKey.Create(television);
        using var fixture = new AppFixture(mangaPagePng: null, initializeProfile: profile =>
        {
            var favorites = new FavoriteMediaService(Path.Combine(profile, "Roaming", "UniversalMediaOS", "favorites.json"));
            Assert.True(favorites.Toggle(new MediaResult { Id = 1, IdMal = 1, OfficialTitle = "Mock English",
                DisplayYear = "2010", AvailableSubEpisodes = 12 }));
            var library = new AudiovisualLibraryService(Path.Combine(profile, "Local", "UniversalMediaOS", "OtherMedia", "audiovisual-library.json"));
            library.SetFavoriteAsync(filmKey, film.Title, "", true).GetAwaiter().GetResult();
            library.SetStatusAsync(filmKey, film.Title, "", AudiovisualLibraryStatus.Watching).GetAwaiter().GetResult();
            library.SetFavoriteAsync(tvKey, television.Title, "", true).GetAwaiter().GetResult();
            library.SetStatusAsync(tvKey, television.Title, "", AudiovisualLibraryStatus.Completed).GetAwaiter().GetResult();
        });
        string favoritesPath = Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "favorites.json");
        string libraryPath = Path.Combine(fixture.SandboxPath, "Local", "UniversalMediaOS", "OtherMedia", "audiovisual-library.json");
        string originalFavorites = File.ReadAllText(favoritesPath);
        string originalTelevision = JsonSerializer.Serialize(await new AudiovisualLibraryService(libraryPath).GetAsync(tvKey));
        Window Window() => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Properties.Name.ValueOrDefault == AppFixture.ExpectedMainWindowTitle);
        bool Has(string name) => Window().FindAllDescendants(cf => cf.ByName(name)).Any(element => !element.IsOffscreen);
        Button Button(string name) => Window().FindAllDescendants(cf => cf.ByName(name))
            .First(element => !element.IsOffscreen && element.ControlType == ControlType.Button).AsButton();
        void WaitFor(string name) => Assert.True(SpinWait.SpinUntil(() => Has(name), TimeSpan.FromSeconds(6)), name);
        AutomationElement Row(string metadata) => Window().FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Single(item => !item.IsOffscreen && item.FindFirstDescendant(cf => cf.ByName(metadata)) != null);
        void OpenRow(string metadata) => Row(metadata).FindFirstDescendant(cf => cf.ByName("Open"))!.AsButton().Invoke();
        void MyList()
        {
            Button("My List").Invoke();
            WaitFor("All (3)");
        }
        void VisualHold(string phase)
        {
            string? directory = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_MY_LIST_UI_QA_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "my-list-ui-ready.json"), JsonSerializer.Serialize(new
            {
                Phase = phase, Pid = fixture.App.ProcessId, Window = Window().Properties.NativeWindowHandle.Value.ToInt64(),
                Profile = fixture.SandboxPath, FilmWorkKey = filmKey.StableId, TvWorkKey = tvKey.StableId, Utc = DateTime.UtcNow
            }));
            if (int.TryParse(Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_MY_LIST_UI_HOLD_MS"), out int hold))
                Thread.Sleep(Math.Clamp(hold, 0, 60_000));
        }
        MyList();
        VisualHold("initial-three-identities");
        WaitFor("Movie - 2010");
        WaitFor("TV Show - 2010");
        WaitFor("Anime - 2010");
        WaitFor("In progress (1)");
        WaitFor("Completed (1)");
        OpenRow("Movie - 2010");
        WaitFor("Choose Find sources when you are ready to watch.");
        var status = Window().FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
            .Select(element => element.AsComboBox()).Single(combo => combo.SelectedItem?.Text == "Watching");
        status.Select("Completed");
        Assert.True(SpinWait.SpinUntil(() => status.SelectedItem?.Text == "Completed", TimeSpan.FromSeconds(5)),
            "The visible film status selection must settle before Save status.");
        Button("Save status").Invoke();
        WaitFor("Status: Completed.");
        Assert.Equal(AudiovisualLibraryStatus.Completed,
            (await new AudiovisualLibraryService(libraryPath).GetAsync(filmKey))?.Status);
        Assert.Equal(originalTelevision, JsonSerializer.Serialize(await new AudiovisualLibraryService(libraryPath).GetAsync(tvKey)));
        MyList();
        WaitFor("Completed (2)");
        WaitFor("In progress (0)");
        VisualHold("film-status-saved-other-identities-retained");
        int oldPid = fixture.App.ProcessId;
        fixture.Restart();
        Assert.NotEqual(oldPid, fixture.App.ProcessId);
        MyList();
        WaitFor("Movie - 2010");
        WaitFor("TV Show - 2010");
        WaitFor("Anime - 2010");
        WaitFor("Completed (2)");
        Assert.Equal(originalFavorites, File.ReadAllText(favoritesPath));
        var restored = await new AudiovisualLibraryService(libraryPath).GetAllAsync();
        Assert.Equal(2, restored.Count);
        Assert.Equal(new[] { filmKey.StableId, tvKey.StableId }.Order(), restored.Select(entry => entry.Key.StableId).Order());
        Assert.All(restored, entry => { Assert.True(entry.IsFavorite); Assert.Equal(AudiovisualLibraryStatus.Completed, entry.Status); });
        Assert.Equal(originalTelevision, JsonSerializer.Serialize(restored.Single(entry => entry.Key.StableId == tvKey.StableId)));
        VisualHold("three-identities-after-process-restart");
        OpenRow("TV Show - 2010");
        WaitFor("Season number");
        WaitFor("Episode number");
        Assert.Contains(Window().FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox)),
            element => !element.IsOffscreen && element.AsComboBox().SelectedItem?.Text == "Completed");
        MyList();
        OpenRow("Anime - 2010");
        WaitFor("Mock English");
        WaitFor("AniList 1");
        WaitFor("MAL 1");
        Assert.Equal(originalFavorites, File.ReadAllText(favoritesPath));
        Assert.False(fixture.App.HasExited);
    }
}
