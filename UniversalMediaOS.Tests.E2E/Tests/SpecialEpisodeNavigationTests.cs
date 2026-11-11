using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Streaming;
using UniversalMediaOS.WPF;
using UniversalMediaOS.WPF.Helpers;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

/// <summary>Next/Previous from a special must stay inside that special's own catalog entry and units, and a
/// main series episode must never navigate into a special: each is its own AniList entry and resume work.</summary>
public sealed class SpecialEpisodeNavigationTests
{
    private static readonly MediaResult Special = new()
    {
        Id = 194884, IdMal = 61903, Format = "SPECIAL", TotalEpisodes = 2, AvailableSubEpisodes = 2,
        OfficialTitle = "Kaguya-sama: Love Is War -Stairway to Adulthood-",
        EnglishTitle = "Kaguya-sama: Love Is War -Stairway to Adulthood-",
        RomajiTitle = "Kaguya-sama wa Kokurasetai: Otona e no Kaidan",
        Synonyms = ["Kaguya-sama: Love Is War - The Grown-Up Staircase"]
    };

    private static readonly MediaResult Parent = new()
    {
        Id = 101921, IdMal = 37999, Format = "TV", TotalEpisodes = 12, AvailableSubEpisodes = 12,
        OfficialTitle = "Kaguya-sama: Love is War", EnglishTitle = "Kaguya-sama: Love is War",
        RomajiTitle = "Kaguya-sama wa Kokurasetai: Tensai-tachi no Renai Zunousen", Synonyms = []
    };

    [Fact]
    public async Task SpecialNextAndPreviousResolveOnlyThatSpecialsOwnUnits()
    {
        using var fixture = new Fixture();
        PlayMediaMessage opened = await fixture.WatchAsync(Special, "1");
        var context = Assert.IsType<EpisodePlaybackContext>(opened.EpisodeContext);
        Assert.Equal((1, 2), (context.FirstEpisode, context.LastEpisode));
        Assert.Equal(new AnimePlaybackIdentity(194884, 61903), context.ResumeIdentity);
        Assert.Equal("anime:anilist:194884", context.CreateProgressContext("2")?.WorkKey);

        using var player = new PlaybackViewModel(new DatabaseContext());
        player.LoadEmbed("https://example.invalid/special-1", opened.Title, opened.EpisodeNumber, episodeContext: context);
        Assert.False(player.CanGoToPreviousEpisode);
        Assert.True(player.CanGoToNextEpisode);

        await player.NextEpisodeCommand.ExecuteAsync(null);
        Assert.Equal(("Kaguya-sama: Love Is War -Stairway to Adulthood-", "2", 194884, 61903),
            (fixture.Scraper.Query, fixture.Scraper.Episode, fixture.Scraper.AniListId, fixture.Scraper.MalId));
        Assert.Contains("Kaguya-sama wa Kokurasetai: Otona e no Kaidan", fixture.Scraper.Aliases!);
        Assert.DoesNotContain("Kaguya-sama: Love is War", fixture.Scraper.Aliases!);
        Assert.Equal($"{Special.OfficialTitle} - Ep 2", player.MediaTitle);
        // The special's last unit is the end: Next cannot continue into a parent-series episode 3.
        Assert.False(player.CanGoToNextEpisode);
        Assert.True(player.CanGoToPreviousEpisode);

        await player.PreviousEpisodeCommand.ExecuteAsync(null);
        Assert.Equal(("1", 194884), (fixture.Scraper.Episode, fixture.Scraper.AniListId));
        Assert.Equal($"{Special.OfficialTitle} - Ep 1", player.MediaTitle);
    }

    [Fact]
    public async Task MainEpisodeNavigationNeverResolvesTheSpecial()
    {
        using var fixture = new Fixture();
        PlayMediaMessage opened = await fixture.WatchAsync(Parent, "12");
        var context = Assert.IsType<EpisodePlaybackContext>(opened.EpisodeContext);
        Assert.Equal((1, 12), (context.FirstEpisode, context.LastEpisode));

        using var player = new PlaybackViewModel(new DatabaseContext());
        player.LoadEmbed("https://example.invalid/parent-12", opened.Title, opened.EpisodeNumber, episodeContext: context);
        Assert.False(player.CanGoToNextEpisode); // No episode 13: the special is a separate entry.

        await player.PreviousEpisodeCommand.ExecuteAsync(null);
        Assert.Equal(("Kaguya-sama: Love is War", "11", 101921, 37999),
            (fixture.Scraper.Query, fixture.Scraper.Episode, fixture.Scraper.AniListId, fixture.Scraper.MalId));
        Assert.DoesNotContain(fixture.Scraper.Aliases!, alias => alias.Contains("Otona e no Kaidan") || alias.Contains("Stairway"));
        Assert.Equal("anime:anilist:101921", context.CreateProgressContext("11")?.WorkKey);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _previousRoot = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        private readonly HlsLoopbackProxy _proxy = new();
        private readonly MalOAuthService _oauth;
        public RecordingResolver Scraper { get; } = new();
        public AnimeDetailsViewModel Details { get; }

        public Fixture()
        {
            string configDirectory = Path.Combine(_root, "Roaming", "UniversalMediaOS");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(Path.Combine(configDirectory, "config.json"),
                JsonSerializer.Serialize(new { DatabasePath = Path.Combine(_root, "resume.db") }));
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _root);
            var config = new DomainHotSwapper(Path.Combine(_root, "details-config.json"));
            config.SetSetting(DubAvailabilityService.ProviderConfigKey, "[]");
            _oauth = new MalOAuthService(config);
            var temporary = new TemporaryEpisodeWatchService((_, _, _) => Task.FromResult(new List<TorrentResult>()),
                Path.Combine(_root, "TemporaryWatch"));
            Details = new AnimeDetailsViewModel(config, new TripleNetHandoff(config, Scraper, _proxy), null!, temporary,
                new StreamDialog(), new DubAvailabilityService(config), _oauth);
        }

        public async Task<PlayMediaMessage> WatchAsync(MediaResult media, string episode)
        {
            var opened = new TaskCompletionSource<PlayMediaMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            object recipient = new();
            WeakReferenceMessenger.Default.Register<PlayMediaMessage>(recipient, (_, message) =>
            {
                if (message.Title.StartsWith(media.OfficialTitle, StringComparison.Ordinal)) opened.TrySetResult(message);
            });
            try
            {
                Details.Media = media;
                Details.SelectedAudioMode = "Sub";
                Details.SelectedEpisode = episode;
                await Details.WatchNowCommand.ExecuteAsync(null);
                return await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { WeakReferenceMessenger.Default.UnregisterAll(recipient); }
        }

        public void Dispose()
        {
            Details.CancelActiveWork();
            Details.Dispose();
            _oauth.Dispose();
            _proxy.Dispose();
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _previousRoot);
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class StreamDialog : IDialogService
    {
        public (bool DialogResult, SelectedSourceTier SelectedTier) ShowSourceSelection() => (true, SelectedSourceTier.Stream_Auto);
        public bool ShowConfirmDialog(string message, string title) => false;
        public void ShowErrorDialog(string message, string title) { }
        public void ShowInfoDialog(string message, string title) { }
    }

    private sealed class RecordingResolver : IScraperResolver
    {
        public string? Query { get; private set; }
        public string? Episode { get; private set; }
        public int AniListId { get; private set; }
        public int MalId { get; private set; }
        public IReadOnlyList<string>? Aliases { get; private set; }
        public bool IsAvailable => true;
        public Task EnsureReadyAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<ScraperStreamResult?> ResolveAsync(string query, string episodeId, int maxSiteAttempts,
            CancellationToken token = default, Action<string>? progressLog = null, string audioPreference = "sub",
            bool preferNative = true, IReadOnlyList<string>? titleAliases = null,
            int aniListId = 0, int malId = 0, IReadOnlyList<string>? titleSynonyms = null)
        {
            (Query, Episode, AniListId, MalId) = (query, episodeId, aniListId, malId);
            Aliases = (titleAliases ?? []).Concat(titleSynonyms ?? []).ToArray();
            return Task.FromResult<ScraperStreamResult?>(new ScraperStreamResult(
                $"https://media.invalid/{aniListId}/episode-{episodeId}.mp4", null, null, null, null, null, false, null));
        }
    }
}
