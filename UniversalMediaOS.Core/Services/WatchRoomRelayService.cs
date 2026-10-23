using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public sealed class WatchRoomRelayService : IDisposable
    {
        internal const int MaximumIncomingMessageBytes = WatchTogetherClientService.MaximumIncomingMessageBytes;
        private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaximumHandshakeBytes = 16 * 1024;
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
        private readonly ConcurrentDictionary<string, WatchRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        public bool IsRunning => _listener != null;
        public int Port { get; private set; }
        public string ListenUrl { get; private set; } = string.Empty;

        public Task StartAsync(int port, CancellationToken token = default)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            Port = Math.Clamp(port <= 0 ? 8000 : port, 1, 65535);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            TcpListener listener = StartListener(Port);
            _listener = listener;
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            AppLogger.Log($"Watch Together relay listening at {ListenUrl}");
            return Task.CompletedTask;
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }

            foreach (var room in _rooms.Values)
            {
                foreach (var connection in room.GetConnections())
                {
                    _ = SafeCloseAsync(connection.Socket);
                }
            }

            _rooms.Clear();
            _listener = null;
            ListenUrl = string.Empty;
        }

        private TcpListener StartListener(int port)
        {
            // HttpListener needs an administrator URL reservation for anything but localhost,
            // so an ordinary user's relay rejected LAN and VPN peers. A socket listener does not.
            bool dualMode = Socket.OSSupportsIPv6;
            var listener = new TcpListener(dualMode ? IPAddress.IPv6Any : IPAddress.Any, port);
            try
            {
                if (dualMode)
                {
                    listener.Server.DualMode = true;
                }

                listener.Start();
            }
            catch (SocketException ex)
            {
                listener.Stop();
                throw new InvalidOperationException($"Unable to start Watch Together relay on port {port}: {ex.Message}", ex);
            }

            ListenUrl = $"http://localhost:{port}";
            return listener;
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token);
                }
                catch (Exception) when (token.IsCancellationRequested || !ReferenceEquals(_listener, listener))
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Watch Together accept loop failed: {ex.Message}", "WARNING");
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client, token));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                string roomId;
                try
                {
                    using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    handshakeCts.CancelAfter(HandshakeTimeout);
                    UpgradeRequest? request = await ReadUpgradeRequestAsync(stream, handshakeCts.Token);
                    roomId = ResolveRoomId(request?.Path ?? string.Empty);
                    if (request == null || !request.IsWebSocketUpgrade || string.IsNullOrWhiteSpace(roomId))
                    {
                        await WriteHandshakeAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", handshakeCts.Token);
                        return;
                    }

                    if (!request.Version.Equals("13", StringComparison.Ordinal) || !IsValidKey(request.Key))
                    {
                        await WriteHandshakeAsync(stream, "HTTP/1.1 400 Bad Request\r\nSec-WebSocket-Version: 13\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", handshakeCts.Token);
                        return;
                    }

                    string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(request.Key + WebSocketAcceptGuid)));
                    await WriteHandshakeAsync(stream, $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n", handshakeCts.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    if (!token.IsCancellationRequested)
                    {
                        AppLogger.Log($"Watch Together WebSocket handshake failed: {ex.Message}", "WARNING");
                    }

                    return;
                }

                using WebSocket socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, WebSocket.DefaultKeepAliveInterval);
                var connection = new WatchConnection(socket, Guid.NewGuid().ToString("N"));
                var room = _rooms.GetOrAdd(roomId, _ => new WatchRoom());
                await ConnectAsync(roomId, room, connection, token);
                await ReceiveLoopAsync(roomId, room, connection, token);
            }
        }

        private static async Task<UpgradeRequest?> ReadUpgradeRequestAsync(Stream stream, CancellationToken token)
        {
            // Read byte by byte so no WebSocket frame bytes are consumed past the header.
            var header = new List<byte>(512);
            byte[] next = new byte[1];
            while (!EndsWithBlankLine(header))
            {
                if (header.Count >= MaximumHandshakeBytes || await stream.ReadAsync(next, token) == 0)
                {
                    return null;
                }

                header.Add(next[0]);
            }

            string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || !requestLine[0].Equals("GET", StringComparison.Ordinal))
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            string target = requestLine[1];
            string path = Uri.TryCreate(target, UriKind.Absolute, out var absolute) &&
                          absolute.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? absolute.AbsolutePath
                : target.Split('?', '#')[0];
            bool upgrade = HasToken(headers, "Upgrade", "websocket") && HasToken(headers, "Connection", "upgrade");
            return new UpgradeRequest(
                path,
                upgrade,
                headers.GetValueOrDefault("Sec-WebSocket-Key") ?? string.Empty,
                headers.GetValueOrDefault("Sec-WebSocket-Version") ?? string.Empty);
        }

        private static bool EndsWithBlankLine(List<byte> header)
        {
            int count = header.Count;
            return count >= 4 &&
                   header[count - 4] == '\r' && header[count - 3] == '\n' &&
                   header[count - 2] == '\r' && header[count - 1] == '\n';
        }

        private static bool HasToken(Dictionary<string, string> headers, string name, string token)
        {
            return headers.TryGetValue(name, out string? value) &&
                   value.Split(',').Any(part => part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsValidKey(string key)
        {
            Span<byte> decoded = stackalloc byte[32];
            return Convert.TryFromBase64String(key, decoded, out int length) && length == 16;
        }

        private static async Task WriteHandshakeAsync(Stream stream, string response, CancellationToken token)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
            await stream.FlushAsync(token);
        }

        private async Task ConnectAsync(string roomId, WatchRoom room, WatchConnection connection, CancellationToken token)
        {
            WatchRoomRole role;
            string? hostUrl;
            string? targetId = null;
            WatchConnection? host = null;
            lock (room.Sync)
            {
                room.ById[connection.Id] = connection;
                if (room.Host == null)
                {
                    room.Host = connection;
                    role = WatchRoomRole.Host;
                    hostUrl = null;
                }
                else
                {
                    room.Peers.Add(connection);
                    role = WatchRoomRole.Peer;
                    hostUrl = room.HostUrl;
                    targetId = connection.Id;
                    host = room.Host;
                }
            }

            await connection.SendJsonAsync(new { type = "role_assignment", role = role == WatchRoomRole.Host ? "host" : "peer" }, token);
            if (role == WatchRoomRole.Peer && !string.IsNullOrWhiteSpace(hostUrl))
            {
                await connection.SendJsonAsync(new { type = "room_info", host_url = hostUrl }, token);
            }

            if (host != null && targetId != null)
            {
                await host.SendJsonAsync(new { type = "sync_request", target_id = targetId }, token);
            }

            AppLogger.Log($"Watch Together client {connection.Id} joined {roomId} as {role}.");
        }

        private async Task ReceiveLoopAsync(string roomId, WatchRoom room, WatchConnection sender, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && sender.Socket.State == WebSocketState.Open)
                {
                    string? message = await ReceiveTextAsync(sender.Socket, token);
                    if (message == null)
                    {
                        break;
                    }

                    await RouteMessageAsync(roomId, room, sender, message, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Watch Together receive loop failed: {ex.Message}", "WARNING");
            }
            finally
            {
                await DisconnectAsync(roomId, room, sender, token);
            }
        }

        private async Task RouteMessageAsync(string roomId, WatchRoom room, WatchConnection sender, string rawJson, CancellationToken token)
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            string type = TryGetString(root, "type");
            string action = TryGetString(root, "action");

            if (action.Equals("close_room", StringComparison.OrdinalIgnoreCase) && ReferenceEquals(sender, room.Host))
            {
                await CloseRoomAsync(roomId, room, token);
                return;
            }

            if (action.Equals("set_host", StringComparison.OrdinalIgnoreCase) && ReferenceEquals(sender, room.Host))
            {
                string url = TryGetString(root, "url");
                List<WatchConnection> peers;
                lock (room.Sync)
                {
                    room.HostUrl = url;
                    peers = room.Peers.ToList();
                }

                foreach (var peer in peers)
                {
                    await peer.SendJsonAsync(new { type = "room_info", host_url = url }, token);
                }

                return;
            }

            if (action.Equals("request_sync", StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(sender, room.Host))
            {
                WatchConnection? host;
                lock (room.Sync)
                {
                    host = room.Host;
                }

                if (host != null)
                {
                    await host.SendJsonAsync(new { type = "sync_request", target_id = sender.Id }, token);
                }

                return;
            }

            if (type.Equals("sync_response", StringComparison.OrdinalIgnoreCase) && ReferenceEquals(sender, room.Host))
            {
                string targetId = TryGetString(root, "target_id");
                double timestamp = TryGetDouble(root, "timestamp");
                bool isPlaying = TryGetBool(root, "is_playing");
                WatchConnection? target = null;
                lock (room.Sync)
                {
                    room.ById.TryGetValue(targetId, out target);
                }

                if (target != null)
                {
                    await target.SendJsonAsync(new { type = "initial_sync", timestamp, is_playing = isPlaying }, token);
                }

                return;
            }

            foreach (var connection in room.GetConnections().Where(connection => !ReferenceEquals(connection, sender)))
            {
                await connection.SendTextAsync(rawJson, token);
            }
        }

        private async Task DisconnectAsync(string roomId, WatchRoom room, WatchConnection connection, CancellationToken token)
        {
            WatchConnection? promotedHost = null;
            bool removeRoom;
            lock (room.Sync)
            {
                room.ById.Remove(connection.Id);
                if (ReferenceEquals(room.Host, connection))
                {
                    room.Host = null;
                    room.HostUrl = null;
                    if (room.Peers.Count > 0)
                    {
                        promotedHost = room.Peers[0];
                        room.Peers.RemoveAt(0);
                        room.Host = promotedHost;
                    }
                }
                else
                {
                    room.Peers.Remove(connection);
                }

                removeRoom = room.Host == null && room.Peers.Count == 0;
            }

            if (promotedHost != null && promotedHost.Socket.State == WebSocketState.Open)
            {
                await promotedHost.SendJsonAsync(new { type = "role_assignment", role = "host" }, token);
            }

            if (removeRoom)
            {
                _rooms.TryRemove(roomId, out _);
            }
        }

        private async Task CloseRoomAsync(string roomId, WatchRoom room, CancellationToken token)
        {
            var connections = room.GetConnections();
            foreach (var connection in connections)
            {
                try
                {
                    await connection.SendJsonAsync(new { type = "room_closed" }, token);
                    await SafeCloseAsync(connection.Socket);
                }
                catch
                {
                }
            }

            _rooms.TryRemove(roomId, out _);
        }

        private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken token)
        {
            var buffer = new byte[8192];
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Answer the close frame; a client waiting on its close handshake would otherwise hang.
                    await CloseOutputSafelyAsync(socket, WebSocketCloseStatus.NormalClosure, "Left room");
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await CloseOutputSafelyAsync(socket, WebSocketCloseStatus.InvalidMessageType, "Text messages only");
                    return null;
                }

                if (stream.Length + result.Count > MaximumIncomingMessageBytes)
                {
                    await CloseOutputSafelyAsync(socket, WebSocketCloseStatus.MessageTooBig, "Message too large");
                    return null;
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }

        private static async Task CloseOutputSafelyAsync(WebSocket socket, WebSocketCloseStatus status, string description)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(status, description, CancellationToken.None);
                }
            }
            catch
            {
            }
        }

        private static async Task SafeCloseAsync(WebSocket socket)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Room closed", CancellationToken.None);
                }
            }
            catch
            {
            }
        }

        private static string ResolveRoomId(string path)
        {
            string[] parts = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && parts[0].Equals("ws", StringComparison.OrdinalIgnoreCase)
                ? Uri.UnescapeDataString(parts[1])
                : string.Empty;
        }

        private static string TryGetString(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static double TryGetDouble(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.TryGetDouble(out double parsed)
                ? parsed
                : 0;
        }

        private static bool TryGetBool(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(name, out var value) &&
                   value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                   value.GetBoolean();
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }

        private sealed record UpgradeRequest(string Path, bool IsWebSocketUpgrade, string Key, string Version);

        private sealed class WatchRoom
        {
            public object Sync { get; } = new();
            public WatchConnection? Host { get; set; }
            public string? HostUrl { get; set; }
            public List<WatchConnection> Peers { get; } = new();
            public Dictionary<string, WatchConnection> ById { get; } = new(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyList<WatchConnection> GetConnections()
            {
                lock (Sync)
                {
                    return new[] { Host }
                        .Concat(Peers)
                        .Where(connection => connection != null)
                        .Cast<WatchConnection>()
                        .ToArray();
                }
            }
        }

        private sealed class WatchConnection
        {
            private readonly SemaphoreSlim _sendLock = new(1, 1);

            public WatchConnection(WebSocket socket, string id)
            {
                Socket = socket;
                Id = id;
            }

            public WebSocket Socket { get; }
            public string Id { get; }

            public Task SendJsonAsync<T>(T payload, CancellationToken token)
            {
                return SendTextAsync(JsonSerializer.Serialize(payload), token);
            }

            public async Task SendTextAsync(string payload, CancellationToken token)
            {
                if (Socket.State != WebSocketState.Open)
                {
                    return;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                await _sendLock.WaitAsync(token);
                try
                {
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
        }
    }
}
