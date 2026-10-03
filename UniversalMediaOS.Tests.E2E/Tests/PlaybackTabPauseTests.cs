using UniversalMediaOS.Core.Data;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class PlaybackTabPauseTests
{
    [Fact]
    public void QueuedNativeVideoDoesNotTimeOutWhileWaitingForItsFirstPlay()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"queued-episode-{Guid.NewGuid():N}.mp4");
        System.IO.File.WriteAllBytes(path, Array.Empty<byte>());
        try
        {
            // No decoder is started: this checks the queued-media lifecycle.
            using var player = new PlaybackViewModel(new DatabaseContext());
            player.LoadMedia(path, "Queued episode");
            int generation = player.PlaybackStartupGeneration;
            player.SetTabActive(false);
            player.CheckPlaybackStartup(generation, isWeb: false);
            player.SetTabActive(true);
            player.CheckPlaybackStartup(generation, isWeb: false);
            Assert.False(player.HasPlaybackError);
            Assert.False(player.IsPlaybackBusy);
            Assert.False(player.IsPlaying);
            Assert.Equal("Ready to play", player.PlaybackStatusText);
            Assert.Equal(path, player.PendingMediaPath);
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void LeavingBrowserTabPausesAndRetainsItsSourcePositionUntilExplicitResume()
    {
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.IsWebViewActive = true;
        player.EmbedUrl = "https://player.example/episode-one";
        player.ReportWebPlaybackProgress(82, 1400, false, paused: false);
        var actions = new List<string>();
        player.WebPlaybackCommandRequested += (_, request) => actions.Add(request.Action);

        player.SetTabActive(false);
        Assert.Equal(new[] { "pause" }, actions);
        Assert.False(player.IsPlaying);
        Assert.False(player.IsDisposed);
        Assert.Equal(82000, player.PlaybackTime);
        Assert.Equal("https://player.example/episode-one", player.EmbedUrl);

        player.SetTabActive(true);
        Assert.Equal(new[] { "pause" }, actions); // Selection does not resume.
        player.TogglePlayPauseCommand.Execute(null);
        Assert.Equal(new[] { "pause", "play" }, actions);
    }

    [Theory]
    [InlineData("play")]
    [InlineData("playing")]
    [InlineData("buffer_resume")]
    public void VideoStartingLateInAnInactiveBrowserTabIsPaused(string action)
    {
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.IsWebViewActive = true;
        player.SetTabActive(false);
        var actions = new List<string>();
        player.WebPlaybackCommandRequested += (_, request) => actions.Add(request.Action);
        player.ReportWebPlaybackAction(action, 25, 1400);
        Assert.Equal(new[] { "pause" }, actions);
        Assert.False(player.IsPlaying);
        Assert.False(player.IsDisposed);
    }

    [Fact]
    public void LateProgressCannotRestartAnInactiveVideoOrAffectAnotherPlayer()
    {
        using var inactive = new PlaybackViewModel(new DatabaseContext());
        using var active = new PlaybackViewModel(new DatabaseContext());
        inactive.IsWebViewActive = active.IsWebViewActive = true;
        inactive.ReportWebPlaybackProgress(25, 1400, false, paused: false);
        active.ReportWebPlaybackProgress(77, 1400, false, paused: false);
        inactive.SetTabActive(false);
        inactive.ReportWebPlaybackProgress(26, 1400, false, paused: false);
        Assert.False(inactive.IsPlaying);
        Assert.Equal(26000, inactive.PlaybackTime);
        Assert.True(active.IsPlaying);
        Assert.Equal(77000, active.PlaybackTime);
    }

    [Fact]
    public void HiddenOrDisposedPlayersIgnorePlayAndRepeatedDeactivation()
    {
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.IsWebViewActive = true;
        var actions = new List<string>();
        player.WebPlaybackCommandRequested += (_, request) => actions.Add(request.Action);
        player.SetTabActive(false);
        player.SetTabActive(false);
        player.TogglePlayPauseCommand.Execute(null);
        Assert.Equal(new[] { "pause" }, actions);
        player.Dispose();
        Assert.Null(Record.Exception(() => player.SetTabActive(true)));
        Assert.False(player.IsTabActive);
    }
}
