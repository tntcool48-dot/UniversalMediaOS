using System;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public sealed class WatchTogetherMessageEventArgs : EventArgs
    {
        public WatchTogetherMessageEventArgs(string rawJson, JsonElement payload)
        {
            RawJson = rawJson;
            Payload = payload.Clone();
        }

        public string RawJson { get; }
        public JsonElement Payload { get; }
    }

    public sealed class WatchTogetherClientService : IDisposable
    {
        private readonly DomainHotSwapper _config;
        private ClientWebSocket? _socket;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        internal const int MaximumIncomingMessageBytes = 64 * 1024;

        public event EventHandler<WatchTogetherMessageEventArgs>? MessageReceived;
        public event EventHandler? StateChanged;

        public WatchRoomConnectionState State { get; private set; } = WatchRoomConnectionState.Disconnected;
        public WatchRoomRole Role { get; private set; } = WatchRoomRole.None;
        public string HostUrl { get; private set; } = string.Empty;
        public string ServerUrl { get; private set; } = string.Empty;
        public string RoomId { get; private set; } = string.Empty;
        public double OffsetSeconds { get; private set; }

        public WatchTogetherClientService(DomainHotSwapper config)
        {
            _config = config;
            ServerUrl = string.IsNullOrWhiteSpace(config.GetSetting("WatchTogetherLastServerUrl"))
                ? "localhost"
                : config.GetSetting("WatchTogetherLastServerUrl");
            string configuredRoomId = config.GetSetting("WatchTogetherLastRoomId");
            RoomId = string.IsNullOrWhiteSpace(configuredRoomId) ||
                     configuredRoomId.Equals("anime-room", StringComparison.OrdinalIgnoreCase)
                ? "room-" + Guid.NewGuid().ToString("N")[..12]
                : configuredRoomId;
            if (!RoomId.Equals(configuredRoomId, StringComparison.Ordinal))
            {
                config.SetSetting("WatchTogetherLastRoomId", RoomId);
            }
            OffsetSeconds = double.TryParse(config.GetSetting("WatchTogetherOffsetSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out double offset)
                ? offset
                : 0;
        }

        public async Task ConnectAsync(string serverUrl, string roomId, double offsetSeconds, CancellationToken token = default)
        {
            await DisconnectAsync();
            ServerUrl = string.IsNullOrWhiteSpace(serverUrl) ? "localhost" : serverUrl.Trim();
            RoomId = string.IsNullOrWhiteSpace(roomId)
                ? string.IsNullOrWhiteSpace(RoomId) ? "room-" + Guid.NewGuid().ToString("N")[..12] : RoomId
                : roomId.Trim();
            OffsetSeconds = offsetSeconds;
            _config.SetSetting("WatchTogetherLastServerUrl", ServerUrl);
            _config.SetSetting("WatchTogetherLastRoomId", RoomId);
            _config.SetSetting("WatchTogetherOffsetSeconds", OffsetSeconds.ToString("R", CultureInfo.InvariantCulture));

            SetState(WatchRoomConnectionState.Connecting);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _socket = new ClientWebSocket();
            try
            {
                await _socket.ConnectAsync(BuildWebSocketUri(ServerUrl, RoomId), _cts.Token);
                SetState(WatchRoomConnectionState.Connected);
                ClientWebSocket connectedSocket = _socket;
                CancellationToken receiveToken = _cts.Token;
                _ = Task.Run(() => ReceiveLoopAsync(connectedSocket, receiveToken));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await DisconnectAsync();
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Watch Together connection failed: {ex.Message}", "WARNING");
                await DisconnectAsync();
                SetState(WatchRoomConnectionState.Failed);
            }
        }

        public async Task DisconnectAsync()
        {
            ClientWebSocket? socket = _socket;
            CancellationTokenSource? cts = _cts;
            _socket = null;
            _cts = null;

            try { cts?.Cancel(); } catch { }
            if (socket != null)
            {
                await _sendLock.WaitAsync();
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None);
                    }
                }
                catch
                {
                }
                finally
                {
                    socket.Dispose();
                    _sendLock.Release();
                }
            }

            cts?.Dispose();
            Role = WatchRoomRole.None;
            HostUrl = string.Empty;
            SetState(WatchRoomConnectionState.Disconnected);
        }

        public Task SendAsync(object payload, CancellationToken token = default)
        {
            return SendRawAsync(JsonSerializer.Serialize(payload), token);
        }

        public async Task SendRawAsync(string rawJson, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(rawJson))
            {
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(rawJson);
            if (bytes.Length > MaximumIncomingMessageBytes)
            {
                throw new ArgumentException($"Watch Together messages cannot exceed {MaximumIncomingMessageBytes} bytes.", nameof(rawJson));
            }

            // ClientWebSocket permits only one send at a time. Keeping the wait inside
            // this method also preserves the order in which callers enqueue actions.
            await _sendLock.WaitAsync(token);
            try
            {
                ClientWebSocket? socket = _socket;
                if (socket?.State != WebSocketState.Open)
                {
                    return;
                }

                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            bool failed = false;
            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    string? message = await ReceiveTextAsync(socket, token);
                    if (message == null)
                    {
                        break;
                    }

                    using var doc = JsonDocument.Parse(message);
                    ApplyProtocolState(doc.RootElement);
                    MessageReceived?.Invoke(this, new WatchTogetherMessageEventArgs(message, doc.RootElement));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Watch Together receive failed: {ex.Message}", "WARNING");
                failed = true;
            }
            finally
            {
                // Explicit DisconnectAsync clears _socket before cancelling its token.
                // If a caller-supplied connection token is cancelled instead, the socket
                // is still current and must transition out of Connected here.
                if (ReferenceEquals(_socket, socket))
                {
                    await DisconnectAsync();
                    if (failed)
                    {
                        SetState(WatchRoomConnectionState.Failed);
                    }
                }
            }
        }

        private void ApplyProtocolState(JsonElement root)
        {
            string type = TryGetString(root, "type");
            if (type.Equals("role_assignment", StringComparison.OrdinalIgnoreCase))
            {
                string role = TryGetString(root, "role");
                Role = role.Equals("host", StringComparison.OrdinalIgnoreCase)
                    ? WatchRoomRole.Host
                    : WatchRoomRole.Peer;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (type.Equals("room_info", StringComparison.OrdinalIgnoreCase))
            {
                HostUrl = TryGetString(root, "host_url");
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (type.Equals("room_closed", StringComparison.OrdinalIgnoreCase))
            {
                _ = DisconnectAsync();
            }
        }

        private void SetState(WatchRoomConnectionState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private static Uri BuildWebSocketUri(string serverUrl, string roomId)
        {
            string normalized = serverUrl.Trim().TrimEnd('/');
            if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "http://" + normalized;
            }

            if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(normalized, UriKind.Absolute, out var httpUri) &&
                httpUri.Port == 80 &&
                !HasExplicitPort(normalized))
            {
                normalized += ":8000";
            }

            normalized = normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? "wss://" + normalized["https://".Length..]
                : normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    ? "ws://" + normalized["http://".Length..]
                    : normalized;

            return new Uri($"{normalized}/ws/{Uri.EscapeDataString(roomId)}");
        }

        private static bool HasExplicitPort(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                   uri.Authority.Contains(':', StringComparison.Ordinal);
        }

        private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
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

        private static string TryGetString(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        public void Dispose()
        {
            _ = DisconnectAsync();
        }
    }
}
