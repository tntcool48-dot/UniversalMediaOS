using System.Text.Json;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.WPF.ViewModels;
using UniversalMediaOS.WPF.Views;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class FramePlaybackTests
{
    private const string Source = "https://video.example/embed/one";
    private static string Progress(string token) => JsonSerializer.Serialize(new
    {
        type = "ums-video-progress", sessionToken = token,
        currentTime = 25, duration = 1400, paused = false, ended = false
    });

    [Fact]
    public void FrameMessagesRequireTheReadyDocumentOriginAndToken()
    {
        var document = new PlaybackView.FramePlaybackDocument(Source, 7);
        string message = Progress(document.SessionToken);
        Assert.False(document.TryRead(message, Source, out _));
        Assert.False(document.MarkReady(6));
        Assert.True(document.MarkReady(7));
        Assert.True(document.TryRead(message, Source, out var telemetry));
        Assert.False(telemetry.Paused);
        Assert.False(document.TryRead(message, "https://other.example/embed", out _));
        Assert.False(document.TryRead(Progress("wrong-token"), Source, out _));
    }

    [Fact]
    public void NavigatedOrDestroyedFramesCannotReviveOldTelemetry()
    {
        var oldDocument = new PlaybackView.FramePlaybackDocument(Source, 7);
        oldDocument.MarkReady(7);
        oldDocument.Invalidate();
        Assert.False(oldDocument.MarkReady(7));
        Assert.False(oldDocument.TryRead(Progress(oldDocument.SessionToken), Source, out _));
        var newDocument = new PlaybackView.FramePlaybackDocument(Source, 8);
        newDocument.MarkReady(8);
        Assert.NotEqual(oldDocument.SessionToken, newDocument.SessionToken);
        Assert.False(newDocument.TryRead(Progress(oldDocument.SessionToken), Source, out _));
        Assert.True(newDocument.TryRead(Progress(newDocument.SessionToken), Source, out _));
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html,video")]
    public void FramesWithoutAnHttpOriginCannotBecomePlaybackSenders(string source)
    {
        var document = new PlaybackView.FramePlaybackDocument(source, 1);
        Assert.False(document.MarkReady(1));
    }

    [Fact]
    public void AudioAcknowledgementRequiresTheRequestedModeAndPageSession()
    {
        string message = JsonSerializer.Serialize(new
        { type = "ums-audio-selection", sessionToken = "current", preference = "dub", selected = true });
        Assert.True(PlaybackView.TryParseAudioSelection(message, Source, Source, "current", "dub", out bool selected));
        Assert.True(selected);
        Assert.False(PlaybackView.TryParseAudioSelection(message, Source, Source, "old", "dub", out _));
        Assert.False(PlaybackView.TryParseAudioSelection(message, "https://other.example", Source, "current", "dub", out _));
        Assert.False(PlaybackView.TryParseAudioSelection(message, Source, Source, "current", "sub", out _));
    }

    [Fact]
    public void BrowserPolicyRejectionShowsPagePlayInstructionWithoutReportingPlayback()
    {
        var document = new PlaybackView.FramePlaybackDocument(Source, 1);
        document.MarkReady(1);
        string message = JsonSerializer.Serialize(new
        {
            type = "ums-video-action", action = "play-blocked", sessionToken = document.SessionToken,
            currentTime = 25, duration = 1400
        });
        Assert.True(document.TryRead(message, Source, out var telemetry));
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.IsWebViewActive = true;
        player.ReportWebPlaybackAction(telemetry.Action, telemetry.CurrentTime, telemetry.Duration);
        Assert.False(player.IsPlaying);
        Assert.False(player.HasPlaybackError);
        Assert.Equal("Press Play in the page", player.PlaybackStatusText);
        player.ReportWebPlaybackAction("playing", 26, 1400);
        Assert.True(player.IsPlaying);
        Assert.Equal("Playing", player.PlaybackStatusText);
    }

    [Fact]
    public void BrowserPlayWaitsForVideoConfirmationAndProgressRestoresPausedState()
    {
        using var player = new PlaybackViewModel(new DatabaseContext());
        player.IsWebViewActive = true;
        string? action = null;
        player.WebPlaybackCommandRequested += (_, request) => action = request.Action;
        player.TogglePlayPauseCommand.Execute(null);
        Assert.Equal("play", action);
        Assert.False(player.IsPlaying);
        player.ReportWebPlaybackProgress(25, 1400, false, paused: false);
        Assert.True(player.IsPlaying);
        Assert.Equal("Playing", player.PlaybackStatusText);
        player.ReportWebPlaybackProgress(26, 1400, false, paused: true);
        Assert.False(player.IsPlaying);
        Assert.Equal("Paused", player.PlaybackStatusText);
    }
}
