using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class LiveWatchTogetherPlaybackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TwoNativeAppWindowsRetainPausedOffsetAcrossRoomReconnectsAndSyncTransport()
    {
        using var host = new AppFixture();
        using var peer = new AppFixture();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string room = "ui-reconnect-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(host.SandboxPath, "watch-media");
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "episode.mp4");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var encoded = await new PreparationProcessRunner().RunAsync("ffmpeg", ["-nostdin", "-hide_banner", "-v", "error",
                "-f", "lavfi", "-i", "testsrc=size=160x90:rate=25", "-t", "90", "-c:v", "mpeg4", "-y", video], deadline.Token);
            Assert.Equal(0, encoded.ExitCode);
        }
        File.WriteAllText(Path.Combine(directory, "caption.en.srt"), "1\n00:00:00,000 --> 00:01:30,000\nWatch Together\nPaused reconnect fixture\n");
        File.WriteAllText(Path.Combine(directory, "library-item.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, WorkKey = "av:fixture:show:watch-reconnect", UnitKey = "season:1:episode:1",
            Identity = new AudiovisualIdentity { PrimaryId = new("fixture", "show", "watch-reconnect"),
                Kind = AudiovisualMediaKind.Television, ContentForm = AudiovisualContentForm.Series, Title = "Watch reconnect fixture" },
            Unit = new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 }, Title = "Watch reconnect fixture",
            MediaFile = "episode.mp4", CaptionFiles = new[] { "caption.en.srt" },
            SourceProvider = "generated-fixture", AudioNotice = "Generated video without audio."
        }));
        long bytes = new FileInfo(video).Length;
        DateTime timestamp = File.GetLastWriteTimeUtc(video);

        Window Window(AppFixture fixture) => fixture.App.GetAllTopLevelWindows(fixture.Automation)
            .Single(window => window.Name == AppFixture.ExpectedMainWindowTitle);
        Button Button(AppFixture fixture, string name) => Window(fixture).FindFirstDescendant(cf => cf.ByName(name))!.AsButton();
        double Position(AppFixture fixture) => Window(fixture).FindFirstDescendant(cf => cf.ByAutomationId("PlaybackSlider"))!
            .Patterns.RangeValue.Pattern.Value.Value;
        bool Paused(AppFixture fixture) => Window(fixture).FindFirstDescendant(cf => cf.ByName("Play or pause"))?
            .FindFirstDescendant(cf => cf.ByText("Play")) != null;
        string Log(AppFixture fixture)
        {
            using var stream = new FileStream(Path.Combine(fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(stream).ReadToEnd();
        }
        void Wait(Func<bool> condition, string message) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), message);
        void Room(AppFixture fixture) => Button(fixture, "Watch Together").Invoke();
        void Player(AppFixture fixture) => Window(fixture).FindAllDescendants(cf => cf.ByAutomationId("SelectTab"))
            .Last().AsButton().Invoke();
        void ConfigureRoom(AppFixture fixture, double offset)
        {
            Room(fixture);
            Wait(() => Window(fixture).FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Length == 5,
                "Room fields must be available.");
            var fields = Window(fixture).FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Select(e => e.AsTextBox()).ToArray();
            fields[0].Text = port.ToString(CultureInfo.InvariantCulture);
            fields[1].Text = "localhost:" + port;
            fields[2].Text = room;
            fields[3].Text = offset.ToString(CultureInfo.InvariantCulture);
        }
        void OpenMedia(AppFixture fixture)
        {
            Button(fixture, "Player").Invoke();
            Wait(() => Window(fixture).FindFirstDescendant(cf => cf.ByName("Playback options")) != null, "Native options must load.");
            Window(fixture).FindFirstDescendant(cf => cf.ByName("Playback options"))!.Patterns.ExpandCollapse.Pattern.Expand();
            Window(fixture).FindFirstDescendant(cf => cf.ByName("Playback volume"))!.Patterns.RangeValue.Pattern.SetValue(0);
            Window(fixture).FindFirstDescendant(cf => cf.ByName("Media URL or local file path"))!.AsTextBox().Text = video;
            Button(fixture, "Open media URL or path").Invoke();
            Wait(() => Window(fixture).FindFirstDescendant(cf => cf.ByName("Subtitle track"))?.IsEnabled == true && Position(fixture) >= 1500,
                "Native decode and downloaded caption selection must initialize.");
            if (!Paused(fixture)) Button(fixture, "Play or pause").Invoke();
            Wait(() => Paused(fixture) && Log(fixture).Contains("LibVLC paused event fired", StringComparison.Ordinal), "Native pause must settle.");
        }
        ConfigureRoom(host, 0);
        Button(host, "Start Relay").Invoke();
        Wait(() => Button(host, "Stop Relay").IsEnabled, "Relay must start.");
        Button(host, "Connect").Invoke();
        Wait(() => Window(host).FindFirstDescendant(cf => cf.ByText("Host")) != null && Button(host, "Disconnect").IsEnabled,
            "First client must be connected as host.");
        OpenMedia(host);
        ConfigureRoom(peer, 2.5);
        Button(peer, "Connect").Invoke();
        Wait(() => Window(peer).FindFirstDescendant(cf => cf.ByText("Peer")) != null && Button(peer, "Disconnect").IsEnabled,
            "Second client must be connected as peer.");
        OpenMedia(peer);
        Wait(() => Paused(host) && Paused(peer) && Math.Abs(Position(peer) - Position(host) - 2500) <= 1500,
            "Initial paused synchronization must retain the configured offset.");

        for (int cycle = 1; cycle <= 3; cycle++)
        {
            Room(peer);
            Button(peer, "Disconnect").Invoke();
            Wait(() => Button(peer, "Connect").IsEnabled && !Button(peer, "Disconnect").IsEnabled, "Peer must disconnect.");
            int before = Log(peer).Length;
            Button(peer, "Connect").Invoke();
            Wait(() => Window(peer).FindFirstDescendant(cf => cf.ByText("Peer")) != null && Button(peer, "Disconnect").IsEnabled,
                "Peer must rejoin the same room.");
            Player(peer);
            Wait(() => Paused(peer) && Math.Abs(Position(peer) - Position(host) - 2500) <= 1500, "Paused native reconnect must retain offset.");
            await Task.Delay(600);
            Assert.DoesNotContain("LibVLC playing event fired", Log(peer)[before..], StringComparison.Ordinal);
            Assert.Equal("English (downloaded)", Window(peer).FindFirstDescendant(cf => cf.ByName("Subtitle track"))!.AsComboBox().SelectedItem?.Text);
            output.WriteLine($"UI reconnect {cycle}: host {Position(host):0} ms / peer {Position(peer):0} ms; both paused, no unintended native start.");
        }
        await Task.Delay(2200); // Allow the production remote-echo suppression interval to expire.
        double oldPosition = Position(host);
        Button(host, "Skip forward 30 seconds").Invoke();
        Wait(() => Position(host) >= oldPosition + 28000 && Math.Abs(Position(peer) - Position(host) - 2500) <= 1500,
            "Native host seek must reach the peer with its offset.");
        int hostBefore = Log(host).Length, peerBefore = Log(peer).Length;
        Button(host, "Play or pause").Invoke();
        Wait(() => !Paused(host) && !Paused(peer) && Log(host)[hostBefore..].Contains("LibVLC playing event fired") &&
            Log(peer)[peerBefore..].Contains("LibVLC playing event fired"), "Explicit UI play must reach both native decoders.");
        await Task.Delay(600);
        Button(host, "Play or pause").Invoke();
        Wait(() => Paused(host) && Paused(peer) && Log(host)[hostBefore..].Contains("LibVLC paused event fired") &&
            Log(peer)[peerBefore..].Contains("LibVLC paused event fired"), "Explicit UI pause must reach both native decoders.");
        Assert.Equal(bytes, new FileInfo(video).Length);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(video));
        output.WriteLine($"Native UI final: host {Position(host):0} ms / peer {Position(peer):0} ms. Windows {Window(host).BoundingRectangle} / {Window(peer).BoundingRectangle}.");

        string? evidence = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_QA_WATCH_EVIDENCE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "native-ui-ready.json"), JsonSerializer.Serialize(new
            {
                HostPid = host.App.ProcessId, PeerPid = peer.App.ProcessId,
                HostWindow = Window(host).Properties.NativeWindowHandle.Value.ToInt64(),
                PeerWindow = Window(peer).Properties.NativeWindowHandle.Value.ToInt64(),
                HostPosition = Position(host), PeerPosition = Position(peer), Port = port, Room = room,
                HostBounds = Window(host).BoundingRectangle.ToString(), PeerBounds = Window(peer).BoundingRectangle.ToString()
            }));
            File.WriteAllText(Path.Combine(evidence, "native-ui-host.log"), Log(host));
            File.WriteAllText(Path.Combine(evidence, "native-ui-peer.log"), Log(peer));
            if (int.TryParse(Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_QA_WATCH_HOLD_SECONDS"), out int hold))
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(hold, 0, 60)));
        }
    }
}
