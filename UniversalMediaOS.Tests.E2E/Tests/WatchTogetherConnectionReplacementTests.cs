using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class WatchTogetherConnectionReplacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedFailedOrCancelledConnectCannotDisconnectReplacement(bool cancelled)
    {
        string directory = Path.Combine(Path.GetTempPath(), "umos-watch-replacement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var rejectedServer = new HttpListener();
        int rejectedPort = ReservePort();
        rejectedServer.Prefixes.Add($"http://localhost:{rejectedPort}/");
        rejectedServer.Start();
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task rejection = Task.Run(async () =>
        {
            HttpListenerContext request = await rejectedServer.GetContextAsync().WaitAsync(deadline.Token);
            requestSeen.TrySetResult();
            await releaseResponse.Task.WaitAsync(deadline.Token);
            request.Response.StatusCode = 503;
            request.Response.ContentLength64 = 0;
            request.Response.Close();
        });

        using var relay = new WatchRoomRelayService();
        using var client = new WatchTogetherClientService(new DomainHotSwapper(Path.Combine(directory, "config.json")));
        using var peer = new ClientWebSocket();
        var heldUi = new HeldUiContext();
        Task? firstConnect = null;
        try
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(heldUi);
                firstConnect = client.ConnectAsync($"localhost:{rejectedPort}", "old-room", 0, firstCancellation.Token);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }

            await requestSeen.Task.WaitAsync(deadline.Token);
            if (cancelled) firstCancellation.Cancel();
            else releaseResponse.TrySetResult();
            await heldUi.Posted.Task.WaitAsync(deadline.Token);
            Assert.False(firstConnect.IsCompleted);
            await client.DisconnectAsync();

            int relayPort = ReservePort();
            await relay.StartAsync(relayPort, deadline.Token);
            var hostAssigned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.StateChanged += (_, _) =>
            {
                if (client.Role == WatchRoomRole.Host) hostAssigned.TrySetResult();
            };
            await client.ConnectAsync($"localhost:{relayPort}", "replacement-room", 2.5, deadline.Token);
            Assert.Equal(WatchRoomConnectionState.Connected, client.State);
            await hostAssigned.Task.WaitAsync(deadline.Token);
            await peer.ConnectAsync(new Uri($"ws://localhost:{relayPort}/ws/replacement-room"), deadline.Token);
            using (JsonDocument assignment = JsonDocument.Parse(await ReceiveAsync(peer, deadline.Token)))
                Assert.Equal("peer", assignment.RootElement.GetProperty("role").GetString());

            heldUi.Release();
            if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstConnect);
            else await firstConnect.WaitAsync(deadline.Token);

            Assert.Equal(WatchRoomConnectionState.Connected, client.State);
            Assert.Equal(WatchRoomRole.Host, client.Role);
            Assert.Equal("replacement-room", client.RoomId);
            Assert.Equal(2.5, client.OffsetSeconds);
            await client.SendAsync(new { type = "action", action = "seek", timestamp = 37.5 }, deadline.Token);
            using JsonDocument received = JsonDocument.Parse(await ReceiveAsync(peer, deadline.Token));
            Assert.Equal("seek", received.RootElement.GetProperty("action").GetString());
            Assert.Equal(37.5, received.RootElement.GetProperty("timestamp").GetDouble());
        }
        finally
        {
            releaseResponse.TrySetResult();
            heldUi.Release();
            await client.DisconnectAsync();
            peer.Abort();
            relay.Stop();
            try { await rejection.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            if (firstConnect is not null)
            {
                try { await firstConnect.WaitAsync(deadline.Token); }
                catch (OperationCanceledException) when (cancelled) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[8192];
        WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private sealed class HeldUiContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _pending.Enqueue((callback, state));
            Posted.TrySetResult();
        }
        public void Release()
        {
            while (_pending.TryDequeue(out var work)) work.Callback(work.State);
        }
    }
}
