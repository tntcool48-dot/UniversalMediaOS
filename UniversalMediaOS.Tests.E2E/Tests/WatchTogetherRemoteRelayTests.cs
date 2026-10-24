using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

// Remote peers reach the relay through this machine's LAN or VPN address, and leaving a room
// must finish even when the other end never completes the WebSocket close handshake.
public sealed class WatchTogetherRemoteRelayTests
{
    private static readonly TimeSpan DisconnectLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task UnelevatedRelayAcceptsPeersThroughThisMachinesNetworkAddress()
    {
        IPAddress address = FindNetworkAddress();
        string directory = Path.Combine(Path.GetTempPath(), "umos-watch-remote", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var relay = new WatchRoomRelayService();
        using var host = new WatchTogetherClientService(new DomainHotSwapper(Path.Combine(directory, "host.json")));
        using var remoteClient = new WatchTogetherClientService(new DomainHotSwapper(Path.Combine(directory, "remote.json")));
        using var remoteSocket = new ClientWebSocket();
        try
        {
            int port = ReservePort();
            await relay.StartAsync(port, deadline.Token);
            await host.ConnectAsync($"localhost:{port}", "remote-room", 0, deadline.Token);
            Assert.Equal(WatchRoomConnectionState.Connected, host.State);

            await remoteSocket.ConnectAsync(new Uri($"ws://{address}:{port}/ws/remote-room"), deadline.Token);
            using (JsonDocument assignment = JsonDocument.Parse(await ReceiveAsync(remoteSocket, deadline.Token)))
                Assert.Equal("peer", assignment.RootElement.GetProperty("role").GetString());

            await host.SendAsync(new { action = "pause", timestamp = 42.5 }, deadline.Token);
            using (JsonDocument forwarded = JsonDocument.Parse(await ReceiveUntilAsync(remoteSocket, "action", deadline.Token)))
            {
                Assert.Equal("pause", forwarded.RootElement.GetProperty("action").GetString());
                Assert.Equal(42.5, forwarded.RootElement.GetProperty("timestamp").GetDouble());
            }

            var remotePeer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            remoteClient.StateChanged += (_, _) =>
            {
                if (remoteClient.Role == WatchRoomRole.Peer) remotePeer.TrySetResult();
            };
            await remoteClient.ConnectAsync($"{address}:{port}", "remote-room", 2.5, deadline.Token);
            Assert.Equal(WatchRoomConnectionState.Connected, remoteClient.State);
            await remotePeer.Task.WaitAsync(deadline.Token);
        }
        finally
        {
            remoteSocket.Abort();
            await remoteClient.DisconnectAsync().WaitAsync(DisconnectLimit);
            await host.DisconnectAsync().WaitAsync(DisconnectLimit);
            relay.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DisconnectDuringAMessageHandlerCompletesAgainstTheRelay()
    {
        string directory = Path.Combine(Path.GetTempPath(), "umos-watch-remote", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var relay = new WatchRoomRelayService();
        using var client = new WatchTogetherClientService(new DomainHotSwapper(Path.Combine(directory, "client.json")));
        var handling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        client.MessageReceived += (_, _) =>
        {
            handling.TrySetResult();
            release.Wait(deadline.Token);
        };
        try
        {
            int port = ReservePort();
            await relay.StartAsync(port, deadline.Token);
            await client.ConnectAsync($"localhost:{port}", "handler-room", 0, deadline.Token);
            await handling.Task.WaitAsync(deadline.Token);

            Task disconnect = client.DisconnectAsync();
            release.Set();
            await disconnect.WaitAsync(DisconnectLimit);
            Assert.Equal(WatchRoomConnectionState.Disconnected, client.State);
            Assert.Equal(WatchRoomRole.None, client.Role);

            await client.ConnectAsync($"localhost:{port}", "handler-room", 0, deadline.Token);
            Assert.Equal(WatchRoomConnectionState.Connected, client.State);
        }
        finally
        {
            release.Set();
            await client.DisconnectAsync().WaitAsync(DisconnectLimit);
            relay.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DisconnectCompletesWhenTheRelayNeverAnswersTheCloseHandshake()
    {
        string directory = Path.Combine(Path.GetTempPath(), "umos-watch-remote", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int port = ReservePort();
        using var silentRelay = new HttpListener();
        silentRelay.Prefixes.Add($"http://localhost:{port}/");
        silentRelay.Start();
        var closeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task serve = Task.Run(async () =>
        {
            HttpListenerContext context = await silentRelay.GetContextAsync().WaitAsync(deadline.Token);
            WebSocketContext accepted = await context.AcceptWebSocketAsync(null);
            await accepted.WebSocket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"role_assignment\",\"role\":\"host\"}"),
                WebSocketMessageType.Text, true, deadline.Token);
            byte[] buffer = new byte[1024];
            while (true)
            {
                WebSocketReceiveResult result = await accepted.WebSocket.ReceiveAsync(buffer, deadline.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Like a relay behind a dropped VPN link: the close frame is never answered.
                    closeSeen.TrySetResult();
                    await Task.Delay(Timeout.Infinite, deadline.Token);
                }
            }
        });
        using var client = new WatchTogetherClientService(new DomainHotSwapper(Path.Combine(directory, "client.json")));
        var handling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        client.MessageReceived += (_, _) =>
        {
            handling.TrySetResult();
            release.Wait(deadline.Token);
        };
        try
        {
            await client.ConnectAsync($"localhost:{port}", "silent-room", 0, deadline.Token);
            await handling.Task.WaitAsync(deadline.Token);

            Task disconnect = client.DisconnectAsync();
            release.Set();
            await closeSeen.Task.WaitAsync(deadline.Token);
            await disconnect.WaitAsync(DisconnectLimit);
            Assert.Equal(WatchRoomConnectionState.Disconnected, client.State);
            Assert.Equal(WatchRoomRole.None, client.Role);
        }
        finally
        {
            release.Set();
            deadline.Cancel();
            silentRelay.Close();
            try { await serve; } catch (Exception) { }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RelayRejectsPlainRequestsAndUnknownPathsWithoutJoiningARoom()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var relay = new WatchRoomRelayService();
        using var http = new HttpClient();
        int port = ReservePort();
        await relay.StartAsync(port, deadline.Token);
        try
        {
            using HttpResponseMessage plain = await http.GetAsync($"http://localhost:{port}/ws/plain-room", deadline.Token);
            Assert.Equal(HttpStatusCode.NotFound, plain.StatusCode);

            using var wrongPath = new ClientWebSocket();
            var refused = await Assert.ThrowsAsync<WebSocketException>(() =>
                wrongPath.ConnectAsync(new Uri($"ws://localhost:{port}/rooms/plain-room"), deadline.Token));
            Assert.Contains("404", refused.Message);

            using var joined = new ClientWebSocket();
            await joined.ConnectAsync(new Uri($"ws://localhost:{port}/ws/plain-room"), deadline.Token);
            using JsonDocument assignment = JsonDocument.Parse(await ReceiveAsync(joined, deadline.Token));
            Assert.Equal("host", assignment.RootElement.GetProperty("role").GetString());
            joined.Abort();
        }
        finally
        {
            relay.Stop();
        }
    }

    private static IPAddress FindNetworkAddress()
    {
        IPAddress[] candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .OrderBy(ip => ip.GetAddressBytes()[0] == 169 && ip.GetAddressBytes()[1] == 254 ? 1 : 0)
            .ToArray();
        Assert.True(candidates.Length > 0, "This check needs a non-loopback IPv4 address on an active adapter.");
        return candidates[0];
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> ReceiveUntilAsync(ClientWebSocket socket, string property, CancellationToken token)
    {
        while (true)
        {
            string message = await ReceiveAsync(socket, token);
            using JsonDocument document = JsonDocument.Parse(message);
            if (document.RootElement.TryGetProperty(property, out _)) return message;
        }
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[8192];
        WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
