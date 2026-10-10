using System.IO;
using System.Text.Json;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class EpisodeNavigationRecoveryTests
{
    [Theory]
    [InlineData("", "Unknown entry - Ep 2")]
    [InlineData("13.5", "Special - Ep 2")]
    [InlineData("special", "Special - Ep 2")]
    [InlineData("99", "Outside known episode range")]
    public void UnknownOrUnmappedUnitsCannotNavigateNumberedEpisodes(string episode, string title)
    {
        using var sandbox = new AppDataSandbox();
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.LoadEmbed("https://example.invalid/special", title, episode,
            episodeContext: Context((_, _) => Task.FromResult<ResolvedEpisodePlayback?>(null)));

        Assert.False(player.CanGoToPreviousEpisode);
        Assert.False(player.CanGoToNextEpisode);
        Assert.False(player.PreviousEpisodeCommand.CanExecute(null));
        Assert.False(player.NextEpisodeCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewLoadCancelsPendingNavigationAndRejectsItsLateSource(bool loadNative)
    {
        using var sandbox = new AppDataSandbox();
        using var player = new PlaybackViewModel(new DatabaseContext());
        var pending = new TaskCompletionSource<ResolvedEpisodePlayback?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var context = Context((_, token) => { observedToken = token; return pending.Task; });
        player.LoadEmbed("https://example.invalid/one", "Original - Ep 1", "1", episodeContext: context);
        Task navigation = player.NextEpisodeCommand.ExecuteAsync(null);
        Assert.True(player.IsEpisodeNavigationBusy);

        // Reuse the same context so context-reference checking alone cannot
        // protect a newer explicitly selected episode in this player.
        if (loadNative)
            player.LoadMedia("https://example.invalid/seven.mp4", "New selection - Ep 7", episodeNumber: "7", episodeContext: context);
        else
            player.LoadEmbed("https://example.invalid/seven", "New selection - Ep 7", "7", episodeContext: context);
        bool canceledAtReplacement = observedToken.IsCancellationRequested;
        bool busyAtReplacement = player.IsEpisodeNavigationBusy;
        pending.SetResult(new("https://example.invalid/stale-two", "Old result - Ep 2", true));
        await navigation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("New selection - Ep 7", player.MediaTitle);
        Assert.Equal(loadNative ? "https://example.invalid/seven.mp4" : "https://example.invalid/seven",
            loadNative ? player.SourceInput : player.EmbedUrl);
        Assert.Equal(!loadNative, player.IsWebViewActive);
        Assert.Equal("dub", player.BrowserAudioPreference);
        Assert.True(canceledAtReplacement);
        Assert.False(busyAtReplacement);
        Assert.False(player.HasPlaybackError);
    }

    [Fact]
    public async Task AReplacedNavigationFailureCannotReportAnErrorOnTheNewItem()
    {
        using var sandbox = new AppDataSandbox();
        using var player = new PlaybackViewModel(new DatabaseContext());
        var pending = new TaskCompletionSource<ResolvedEpisodePlayback?>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.LoadEmbed("https://example.invalid/one", "Original - Ep 1", "1",
            episodeContext: Context((_, _) => pending.Task));
        Task navigation = player.NextEpisodeCommand.ExecuteAsync(null);
        player.LoadEmbed("https://example.invalid/new-work", "Different work");
        pending.SetException(new InvalidOperationException("Retired provider failed"));
        await navigation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Different work", player.MediaTitle);
        Assert.False(player.HasPlaybackError);
        Assert.Equal(string.Empty, player.PlaybackErrorText);
        Assert.Equal("Opening web player...", player.PlaybackStatusText);
    }

    [Fact]
    public async Task NumberedNextAndPreviousKeepAudioAndRejectRangeBoundaries()
    {
        using var sandbox = new AppDataSandbox();
        using var player = new PlaybackViewModel(new DatabaseContext());
        var requested = new List<int>();
        var resolvedTokens = new List<CancellationToken>();
        var context = Context((episode, token) =>
        {
            requested.Add(episode);
            resolvedTokens.Add(token);
            return Task.FromResult<ResolvedEpisodePlayback?>(
                new($"https://example.invalid/{episode}", $"Entry - Ep {episode}", true));
        });
        player.LoadEmbed("https://example.invalid/1", "Entry - Ep 1", "1", episodeContext: context);
        Assert.False(player.CanGoToPreviousEpisode);
        await player.NextEpisodeCommand.ExecuteAsync(null);
        Assert.Equal("https://example.invalid/2", player.EmbedUrl);
        Assert.True(player.CanGoToPreviousEpisode);
        await player.PreviousEpisodeCommand.ExecuteAsync(null);
        Assert.Equal("https://example.invalid/1", player.EmbedUrl);
        Assert.Equal(new[] { 2, 1 }, requested);
        Assert.All(resolvedTokens, token => Assert.False(token.IsCancellationRequested));
        Assert.Equal("dub", player.BrowserAudioPreference);
        Assert.False(player.IsEpisodeNavigationBusy);
        player.LoadEmbed("https://example.invalid/12", "Entry - Ep 12", "12", episodeContext: context);
        Assert.False(player.CanGoToNextEpisode);
    }

    [Fact]
    public async Task AnActiveProviderFailureRetainsTheCurrentEpisodeAndReportsItsError()
    {
        using var sandbox = new AppDataSandbox();
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.LoadEmbed("https://example.invalid/one", "Current - Ep 1", "1",
            episodeContext: Context((_, _) => Task.FromException<ResolvedEpisodePlayback?>(
                new InvalidOperationException("Current provider failed"))));

        await player.NextEpisodeCommand.ExecuteAsync(null);

        Assert.Equal("Current - Ep 1", player.MediaTitle);
        Assert.Equal("https://example.invalid/one", player.EmbedUrl);
        Assert.True(player.HasPlaybackError);
        Assert.Contains("Current provider failed", player.PlaybackErrorText);
        Assert.False(player.IsEpisodeNavigationBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfterFailedPreviousResolvesTheRequestedUnitInsteadOfReloadingTheCurrentUnit(bool throws)
    {
        using var sandbox = new AppDataSandbox();
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.SetTabActive(false);
        var requests = new List<int>();
        var context = Context((episode, _) =>
        {
            requests.Add(episode);
            if (requests.Count == 1)
                return throws ? Task.FromException<ResolvedEpisodePlayback?>(new IOException("Provider unavailable"))
                    : Task.FromResult<ResolvedEpisodePlayback?>(null);
            return Task.FromResult<ResolvedEpisodePlayback?>(new("https://example.invalid/one.mp4", "Entry - Ep 1", false));
        });
        player.LoadMedia("https://example.invalid/two.mp4", "Entry - Ep 2", "https://example.invalid/two",
            "2", episodeContext: context);
        await player.PreviousEpisodeCommand.ExecuteAsync(null);
        Assert.True(player.HasPlaybackError);
        Assert.Equal("Entry - Ep 2", player.MediaTitle);

        player.RetryPlaybackCommand.Execute(null);

        Assert.Equal(new[] { 1, 1 }, requests);
        Assert.Equal("Entry - Ep 1", player.MediaTitle);
        Assert.Equal("https://example.invalid/one.mp4", player.SourceInput);
        Assert.False(player.IsWebViewActive);
        Assert.Equal("dub", player.BrowserAudioPreference);
        Assert.False(player.HasPlaybackError);
        await player.ResumeLoadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailedNavigationCannotOfferThePreviousUnitsBrowserFallback()
    {
        using var sandbox = new AppDataSandbox();
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.SetTabActive(false);
        player.LoadMedia("https://example.invalid/two.mp4", "Entry - Ep 2", "https://example.invalid/two",
            "2", episodeContext: Context((_, _) => Task.FromResult<ResolvedEpisodePlayback?>(null)));
        Assert.True(player.HasWebFallback);
        await player.PreviousEpisodeCommand.ExecuteAsync(null);

        Assert.False(player.HasWebFallback);
        player.OpenWebFallbackCommand.Execute(null);
        Assert.False(player.IsWebViewActive);
        Assert.Equal("Entry - Ep 2", player.MediaTitle);
        Assert.True(player.HasPlaybackError);
        await player.ResumeLoadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ANewSelectionCancelsAFailedUnitsRetryAndRejectsItsLateResult()
    {
        using var sandbox = new AppDataSandbox();
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.SetTabActive(false);
        var pending = new TaskCompletionSource<ResolvedEpisodePlayback?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        CancellationToken retryToken = default;
        var context = Context((_, token) =>
        {
            if (++requests == 1) return Task.FromResult<ResolvedEpisodePlayback?>(null);
            retryToken = token;
            return pending.Task;
        });
        player.LoadMedia("https://example.invalid/two.mp4", "Entry - Ep 2", episodeNumber: "2", episodeContext: context);
        await player.PreviousEpisodeCommand.ExecuteAsync(null);
        Task retry = player.RetryPlaybackCommand.ExecuteAsync(null);
        Assert.True(player.IsEpisodeNavigationBusy);

        player.LoadMedia("https://example.invalid/seven.mp4", "New selection - Ep 7", episodeNumber: "7", episodeContext: context);
        Assert.True(retryToken.IsCancellationRequested);
        pending.SetResult(new("https://example.invalid/stale-one.mp4", "Retired - Ep 1", false));
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("New selection - Ep 7", player.MediaTitle);
        Assert.Equal("https://example.invalid/seven.mp4", player.SourceInput);
        Assert.False(player.HasPlaybackError);
        Assert.False(player.IsEpisodeNavigationBusy);
        await player.RetryPlaybackCommand.ExecuteAsync(null);
        Assert.Equal(2, requests);
        Assert.Equal("New selection - Ep 7", player.MediaTitle);
        await player.ResumeLoadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NormalNativeRetryKeepsItsFallbackButAnExplicitBrowserErrorCannotExposeTheOldNativeUnitsUrl()
    {
        using var sandbox = new AppDataSandbox();
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.SetTabActive(false);
        player.LoadMedia("https://example.invalid/two.mp4", "Entry - Ep 2", "https://example.invalid/two", "2");
        player.ReportPlaybackError("Current native stream failed");
        Assert.True(player.HasWebFallback);
        player.LoadEmbed("https://example.invalid/three", "Entry - Ep 3", "3");
        player.ReportPlaybackError("Explicit browser route failed");
        Assert.False(player.HasWebFallback);
        await player.RetryPlaybackCommand.ExecuteAsync(null);
        Assert.Equal("https://example.invalid/three", player.EmbedUrl);
        Assert.Equal("Entry - Ep 3", player.MediaTitle);
        await player.ResumeLoadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static EpisodePlaybackContext Context(Func<int, CancellationToken, Task<ResolvedEpisodePlayback?>> resolver) =>
        new(1, 12, resolver, "dub", new AnimePlaybackIdentity(1234, 5678));

    private sealed class AppDataSandbox : IDisposable
    {
        private readonly string? _previousRoot = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));

        public AppDataSandbox()
        {
            string configDirectory = Path.Combine(_root, "Roaming", "UniversalMediaOS");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(Path.Combine(configDirectory, "config.json"),
                JsonSerializer.Serialize(new { DatabasePath = Path.Combine(_root, "resume.db") }));
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("UNIVERSAL_MEDIA_OS_DATA_ROOT", _previousRoot);
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
