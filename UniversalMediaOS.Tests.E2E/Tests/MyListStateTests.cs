using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MyListStateTests
{
    [Theory]
    [InlineData("Finished")]
    [InlineData("Releasing")]
    [InlineData("Ongoing")]
    [InlineData("")]
    public async Task AnimeCatalogAvailabilityIsVisibleWithoutInventingPersonalWatchingStatus(string availability)
    {
        using var profile = new Profile();
        var favorites = profile.Favorites();
        favorites.Toggle(new MediaResult { Id = 101, IdMal = 202, OfficialTitle = "Same title",
            DisplayStatus = availability, AvailableSubEpisodes = 12, DisplayYear = "2010" });
        string original = File.ReadAllText(profile.FavoritesPath);
        using var list = profile.List(favorites);
        await Ready(list, 1);
        var anime = Assert.Single(list.Items);
        Assert.Equal("Favorite", anime.Status);
        Assert.Equal(availability.Length == 0 ? "" : $"Availability: {availability}", anime.Availability);
        Assert.Equal("Available: Sub 12", anime.Progress);
        Assert.Equal(0, list.Completed);
        Assert.Equal(0, list.Watching);
        Assert.Equal(availability, anime.Anime!.DisplayStatus);
        Assert.Equal(101, anime.Anime.Id);
        Assert.Equal(202, anime.Anime.IdMal);
        list.ShowCompletedCommand.Execute(null);
        Assert.Empty(list.Items);
        list.ShowWatchingCommand.Execute(null);
        Assert.Empty(list.Items);
        list.ShowAllCommand.Execute(null);
        Assert.Single(list.Items);
        Assert.Equal(original, File.ReadAllText(profile.FavoritesPath));
    }

    [Fact]
    public async Task AvailabilityRefreshAndNewViewModelKeepAvailabilitySeparateFromWatchingHistory()
    {
        using var profile = new Profile();
        var favorites = profile.Favorites();
        favorites.Toggle(new MediaResult { Id = 101, IdMal = 202, OfficialTitle = "Same title",
            DisplayStatus = "Releasing", AvailableSubEpisodes = 1 });
        using (var list = profile.List(favorites))
        {
            await Ready(list, 1);
            Assert.Single(favorites.UpdateAvailability(new Dictionary<int, FavoriteMediaAvailability>
                { [101] = new(2, "Finished") }));
            await list.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("Availability: Finished", Assert.Single(list.Items).Availability);
            Assert.Equal("Available: Sub 2", Assert.Single(list.Items).Progress);
            Assert.Equal(0, list.Completed);
            Assert.Equal(0, list.Watching);
        }
        string original = File.ReadAllText(profile.FavoritesPath);
        using var reloaded = profile.List(profile.Favorites());
        await Ready(reloaded, 1);
        Assert.Equal("Finished", Assert.Single(reloaded.Items).Anime!.DisplayStatus);
        Assert.Equal(2, Assert.Single(reloaded.Items).Anime!.AvailableSubEpisodes);
        Assert.Equal(0, reloaded.Completed);
        Assert.Equal(0, reloaded.Watching);
        Assert.Equal(original, File.ReadAllText(profile.FavoritesPath));
    }

    [Fact]
    public async Task SameTitleYearAndNumericIdsReopenTheirExactFilmTelevisionAndAnimeRecordsAfterStoreReload()
    {
        using var profile = new Profile();
        var film = new AudiovisualIdentity { Kind = AudiovisualMediaKind.Movie, ContentForm = AudiovisualContentForm.Feature,
            PrimaryId = new("fixture", "movie", "42"), Title = "Same title", Year = 2010 };
        var tv = film with { Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series,
            PrimaryId = new("fixture", "show", "42") };
        var filmKey = AudiovisualLibraryKey.Create(film);
        var tvKey = AudiovisualLibraryKey.Create(tv);
        var library = profile.Library();
        await library.SetFavoriteAsync(filmKey, film.Title, "", true);
        await library.RecordProgressAsync(filmKey, film.Title, "", null, null, 90, 3600);
        await library.SetFavoriteAsync(tvKey, tv.Title, "", true);
        await library.RecordProgressAsync(tvKey, tv.Title, "", 2, 3, 185, 2500);
        await library.SetStatusAsync(tvKey, tv.Title, "", AudiovisualLibraryStatus.Completed);
        profile.Favorites().Toggle(new MediaResult { Id = 42, IdMal = 42, OfficialTitle = "Same title",
            DisplayYear = "2010", DisplayStatus = "Finished", AvailableSubEpisodes = 12 });
        string originalFavorites = File.ReadAllText(profile.FavoritesPath);
        string originalLibrary = File.ReadAllText(profile.LibraryPath);
        object recipient = new();
        var audiovisualMessages = new List<AudiovisualMediaItem>();
        var animeMessages = new List<MediaResult?>();
        WeakReferenceMessenger.Default.Register<NavigateToAudiovisualDetailsMessage>(recipient, (_, message) => audiovisualMessages.Add(message.Media));
        WeakReferenceMessenger.Default.Register<NavigateToDetailsMessage>(recipient, (_, message) => animeMessages.Add(message.Media));
        try
        {
            for (int reload = 0; reload < 2; reload++)
            {
                using var list = profile.List(profile.Favorites());
                await Ready(list, 3);
                Assert.Equal(1, list.Completed);
                Assert.Equal(1, list.Watching);
                Assert.Equal("Watching", list.Items.Single(item => item.Kind == "Movie").Status);
                Assert.Equal("S02E03", list.Items.Single(item => item.Kind == "TV Show").Progress);
                Assert.All(list.Items, item => Assert.Equal("2010", item.Year));
                foreach (var item in list.Items) list.OpenDetailsCommand.Execute(item);
                list.ShowCompletedCommand.Execute(null);
                Assert.Equal("TV Show", Assert.Single(list.Items).Kind);
                list.ShowWatchingCommand.Execute(null);
                Assert.Equal("Movie", Assert.Single(list.Items).Kind);
            }
            Assert.Equal(4, audiovisualMessages.Count);
            Assert.Equal(2, animeMessages.Count);
            Assert.Equal(new[] { filmKey.StableId, filmKey.StableId, tvKey.StableId, tvKey.StableId }.Order(),
                audiovisualMessages.Select(message => message.PersistedWorkKey).Order());
            foreach (var message in audiovisualMessages)
            {
                var expected = message.Identity.Kind == AudiovisualMediaKind.Movie ? film : tv;
                Assert.Equal(expected.PrimaryId, message.Identity.PrimaryId);
                Assert.Equal(expected.ContentForm, message.Identity.ContentForm);
                Assert.Equal(AudiovisualLibraryKey.Create(expected).StableId, message.PersistedWorkKey);
            }
            Assert.All(animeMessages, message =>
            {
                Assert.NotNull(message);
                Assert.Equal(42, message.Id);
                Assert.Equal(42, message.IdMal);
                Assert.Equal("Finished", message.DisplayStatus);
                Assert.Equal(12, message.AvailableSubEpisodes);
            });
            Assert.Equal(originalFavorites, File.ReadAllText(profile.FavoritesPath));
            Assert.Equal(originalLibrary, File.ReadAllText(profile.LibraryPath));
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
    }

    private static async Task Ready(MyListViewModel list, int expected)
    {
        for (int attempt = 0; attempt < 200 && list.Total != expected; attempt++) await Task.Delay(10);
        Assert.Equal(expected, list.Total);
        await list.RefreshCommand.ExecuteAsync(null);
    }

    private sealed class Profile : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UMOS-MyList-State-" + Guid.NewGuid().ToString("N"));
        public string FavoritesPath => Path.Combine(_root, "favorites.json");
        public string LibraryPath => Path.Combine(_root, "audiovisual-library.json");
        public Profile() => Directory.CreateDirectory(_root);
        public FavoriteMediaService Favorites() => new(FavoritesPath);
        public AudiovisualLibraryService Library() => new(LibraryPath);
        public MyListViewModel List(FavoriteMediaService favorites) => new(favorites, Library(),
            new JsonReadingProgressStore(Path.Combine(_root, "empty-reading-progress.json")), new NoDialogs());
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class NoDialogs : IDialogService
    {
        public (bool DialogResult, SelectedSourceTier SelectedTier) ShowSourceSelection() => throw new InvalidOperationException("Unexpected source dialog.");
        public bool ShowConfirmDialog(string message, string title) => throw new InvalidOperationException("Unexpected removal dialog.");
        public void ShowErrorDialog(string message, string title) => throw new InvalidOperationException("Unexpected error dialog.");
        public void ShowInfoDialog(string message, string title) => throw new InvalidOperationException("Unexpected info dialog.");
    }
}
