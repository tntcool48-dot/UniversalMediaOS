using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class VaDetectWatchTogetherServiceTests
    {
        [Fact]
        public async Task VoiceActorIndex_SeparatesSubAndDubMatchesAndHighlightsTopScores()
        {
            using var sandbox = new AppDataSandbox();
            SeedVoiceCastFixture();

            using var httpClient = new HttpClient(new StubHttpHandler((_, _) =>
                throw new InvalidOperationException("Cached VA index test should not fetch network data.")));
            var castService = new VoiceCastService(new DomainHotSwapper(sandbox.ConfigPath), httpClient);
            var index = new VoiceActorIndexService(castService, new FavoriteMediaService());
            var target = new VoiceCastMedia("mal:100", 100, 0, "Target Anime", "", "", "", "");

            var sub = await index.FindMatchesAsync(target, VoiceLanguageMode.Sub);
            var dub = await index.FindMatchesAsync(target, VoiceLanguageMode.Dub);

            var subMatch = Assert.Single(sub.Matches);
            Assert.Equal("Kana Hanazawa", subMatch.VoiceActorName);
            Assert.Equal("Completed", subMatch.Group);
            Assert.True(Assert.Single(subMatch.KnownFrom).IsTopRated);
            Assert.DoesNotContain(sub.TargetCast, role => role.VoiceActorName == "Cherami Leigh");

            var dubMatch = Assert.Single(dub.Matches);
            Assert.Equal("Cherami Leigh", dubMatch.VoiceActorName);
            Assert.Equal("Watching", dubMatch.Group);
            Assert.False(Assert.Single(dubMatch.KnownFrom).IsTopRated);
            Assert.DoesNotContain(dub.TargetCast, role => role.VoiceActorName == "Kana Hanazawa");
        }

        [Fact]
        public async Task MalPublicFallback_ImportsLoadJsonEntriesWithWarningReadyStatusData()
        {
            var handler = new StubHttpHandler((request, _) =>
            {
                string query = request.RequestUri?.Query ?? string.Empty;
                if (query.Contains("status=2", StringComparison.OrdinalIgnoreCase) &&
                    query.Contains("offset=0", StringComparison.OrdinalIgnoreCase))
                {
                    string json = """
                        [
                          {
                            "anime_id": 123,
                            "anime_title": "Mock Anime",
                            "anime_title_eng": "Mock Anime EN",
                            "anime_image_path": "https://img.example/mock.jpg",
                            "score": 9,
                            "num_watched_episodes": 12,
                            "anime_num_episodes": 12
                          }
                        ]
                        """;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                });
            });
            using var httpClient = new HttpClient(handler);
            var service = new MalPublicListFallbackService(httpClient);

            var entries = await service.FetchAsync("public-user");

            var entry = Assert.Single(entries);
            Assert.Equal(123, entry.MalId);
            Assert.Equal("Completed", entry.ListStatus);
            Assert.Equal(9, entry.UserScore);
            Assert.Equal(12, entry.WatchedEpisodes);
            Assert.Contains(handler.RequestUris, uri => uri.Contains("/animelist/public-user/load.json", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task MalPublicFallback_FailedPublicListIsReportedAsUnavailable()
        {
            var handler = new StubHttpHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
            using var httpClient = new HttpClient(handler);
            var service = new MalPublicListFallbackService(httpClient);

            var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchAsync("private-user"));
            Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        }

        [Fact]
        public void PlaybackSyncController_AppliesOffsetToPayloads()
        {
            using var sandbox = new AppDataSandbox(new Dictionary<string, string>
            {
                ["WatchTogetherOffsetSeconds"] = "2.5"
            });
            var client = new WatchTogetherClientService(new DomainHotSwapper(sandbox.ConfigPath));
            var controller = new PlaybackSyncController(client);

            Assert.Equal(10, controller.ToBaseTime(12.5), precision: 3);
            Assert.Equal(12.5, controller.ToLocalTime(10), precision: 3);

            using var actionDoc = JsonDocument.Parse(JsonSerializer.Serialize(controller.CreateActionPayload("seek", 12.5, "Mock Anime")));
            Assert.Equal("action", actionDoc.RootElement.GetProperty("type").GetString());
            Assert.Equal("seek", actionDoc.RootElement.GetProperty("action").GetString());
            Assert.Equal(10, actionDoc.RootElement.GetProperty("timestamp").GetDouble(), precision: 3);
            Assert.Equal("Mock Anime", actionDoc.RootElement.GetProperty("media_title").GetString());

            using var syncDoc = JsonDocument.Parse(JsonSerializer.Serialize(controller.CreateSyncResponsePayload("peer-1", 20)));
            Assert.Equal("sync_response", syncDoc.RootElement.GetProperty("type").GetString());
            Assert.Equal("peer-1", syncDoc.RootElement.GetProperty("target_id").GetString());
            Assert.Equal(17.5, syncDoc.RootElement.GetProperty("timestamp").GetDouble(), precision: 3);
            Assert.False(syncDoc.RootElement.GetProperty("is_playing").GetBoolean());
        }

        [Fact]
        public void PlaybackSyncController_OnlyTreatsMostRecentlyActivatedPlaybackAsOwner()
        {
            using var sandbox = new AppDataSandbox();
            using var client = new WatchTogetherClientService(new DomainHotSwapper(sandbox.ConfigPath));
            var controller = new PlaybackSyncController(client);
            var firstPlayback = new object();
            var secondPlayback = new object();

            Assert.False(controller.IsActivePlayback(firstPlayback));
            controller.ActivatePlayback(firstPlayback);
            Assert.True(controller.IsActivePlayback(firstPlayback));

            controller.ActivatePlayback(secondPlayback);
            Assert.False(controller.IsActivePlayback(firstPlayback));
            Assert.True(controller.IsActivePlayback(secondPlayback));

            // Unloading a background view must not release the current owner.
            controller.DeactivatePlayback(firstPlayback);
            Assert.True(controller.IsActivePlayback(secondPlayback));
            controller.DeactivatePlayback(secondPlayback);
            Assert.False(controller.IsActivePlayback(secondPlayback));
        }

        [Fact]
        public void PlaybackSyncController_IncludesPlayingStateAndCompletionKeepsRoomOpen()
        {
            using var sandbox = new AppDataSandbox();
            using var client = new WatchTogetherClientService(new DomainHotSwapper(sandbox.ConfigPath));
            var controller = new PlaybackSyncController(client);

            using var syncDoc = JsonDocument.Parse(JsonSerializer.Serialize(
                controller.CreateSyncResponsePayload("peer-2", 30, isPlaying: true)));

            Assert.True(syncDoc.RootElement.GetProperty("is_playing").GetBoolean());
            Assert.Equal("pause", PlaybackViewModel.CompletionWatchTogetherAction);
            Assert.NotEqual("close_room", PlaybackViewModel.CompletionWatchTogetherAction);
        }

        [Fact]
        public async Task WatchTogetherClient_RejectsOversizedOutboundMessages()
        {
            using var sandbox = new AppDataSandbox();
            using var client = new WatchTogetherClientService(new DomainHotSwapper(sandbox.ConfigPath));
            string oversized = new('x', WatchTogetherClientService.MaximumIncomingMessageBytes + 1);

            var error = await Assert.ThrowsAsync<ArgumentException>(() => client.SendRawAsync(oversized));

            Assert.Contains("cannot exceed", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task WatchRoomRelay_ForwardsInitialPlayingStateAndKeepsRoomAfterEndedAction()
        {
            int port = ReserveLoopbackPort();
            using var relay = new WatchRoomRelayService();
            using var host = new ClientWebSocket();
            using var peer = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await relay.StartAsync(port, timeout.Token);
            var roomUri = new Uri($"ws://localhost:{port}/ws/test-room");
            try
            {
                await host.ConnectAsync(roomUri, timeout.Token);
                Assert.Equal("host", (await ReceiveJsonAsync(host, timeout.Token)).GetProperty("role").GetString());

                await peer.ConnectAsync(roomUri, timeout.Token);
                Assert.Equal("peer", (await ReceiveJsonAsync(peer, timeout.Token)).GetProperty("role").GetString());
                JsonElement syncRequest = await ReceiveJsonAsync(host, timeout.Token);
                string targetId = syncRequest.GetProperty("target_id").GetString()!;

                await SendJsonAsync(host, new
                {
                    type = "sync_response",
                    target_id = targetId,
                    timestamp = 42.5,
                    is_playing = true
                }, timeout.Token);
                JsonElement initialSync = await ReceiveJsonAsync(peer, timeout.Token);
                Assert.Equal("initial_sync", initialSync.GetProperty("type").GetString());
                Assert.Equal(42.5, initialSync.GetProperty("timestamp").GetDouble(), precision: 3);
                Assert.True(initialSync.GetProperty("is_playing").GetBoolean());

                await SendJsonAsync(host, new { type = "action", action = "ended", timestamp = 50 }, timeout.Token);
                Assert.Equal("ended", (await ReceiveJsonAsync(peer, timeout.Token)).GetProperty("action").GetString());

                // A normal episode end is an ordinary action, so the same room can
                // immediately carry playback for the next episode.
                await SendJsonAsync(host, new { type = "action", action = "play", timestamp = 0 }, timeout.Token);
                Assert.Equal("play", (await ReceiveJsonAsync(peer, timeout.Token)).GetProperty("action").GetString());
                Assert.Equal(WebSocketState.Open, host.State);
                Assert.Equal(WebSocketState.Open, peer.State);

                byte[] oversized = Encoding.UTF8.GetBytes(new string('x', WatchRoomRelayService.MaximumIncomingMessageBytes + 1));
                await peer.SendAsync(new ArraySegment<byte>(oversized), WebSocketMessageType.Text, true, timeout.Token);
                var closeResult = await peer.ReceiveAsync(new ArraySegment<byte>(new byte[256]), timeout.Token);
                Assert.Equal(WebSocketMessageType.Close, closeResult.MessageType);
                Assert.Equal(WebSocketCloseStatus.MessageTooBig, closeResult.CloseStatus);
            }
            finally
            {
                relay.Stop();
            }
        }

        [Fact]
        public async Task WatchTogetherClient_SerializesRapidOutboundActionsInCallOrder()
        {
            int port = ReserveLoopbackPort();
            using var sandbox = new AppDataSandbox();
            using var relay = new WatchRoomRelayService();
            using var client = new WatchTogetherClientService(new DomainHotSwapper(sandbox.ConfigPath));
            using var peer = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await relay.StartAsync(port, timeout.Token);
            try
            {
                await client.ConnectAsync($"localhost:{port}", "ordered-room", 0, timeout.Token);
                await peer.ConnectAsync(new Uri($"ws://localhost:{port}/ws/ordered-room"), timeout.Token);
                Assert.Equal("peer", (await ReceiveJsonAsync(peer, timeout.Token)).GetProperty("role").GetString());

                Task[] sends = Enumerable.Range(0, 25)
                    .Select(sequence => client.SendAsync(new { type = "action", action = "seek", sequence }, timeout.Token))
                    .ToArray();
                await Task.WhenAll(sends);

                for (int expected = 0; expected < sends.Length; expected++)
                {
                    JsonElement message = await ReceiveJsonAsync(peer, timeout.Token);
                    Assert.Equal(expected, message.GetProperty("sequence").GetInt32());
                }
            }
            finally
            {
                await client.DisconnectAsync();
                relay.Stop();
            }
        }

        private static int ReserveLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            using var stream = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    using JsonDocument document = JsonDocument.Parse(stream.ToArray());
                    return document.RootElement.Clone();
                }
            }
        }

        private static void SeedVoiceCastFixture()
        {
            using var db = new DatabaseContext();
            db.EnsureVaDetectSchema();
            db.MalLibraryEntries.AddRange(
                new MalLibraryEntry
                {
                    MalId = 1,
                    DefaultTitle = "Familiar Sub Anime",
                    EnglishTitle = "Familiar Sub Anime",
                    ListStatus = "Completed",
                    UserScore = 10,
                    LastSyncedUtc = DateTime.UtcNow
                },
                new MalLibraryEntry
                {
                    MalId = 2,
                    DefaultTitle = "Familiar Dub Anime",
                    EnglishTitle = "Familiar Dub Anime",
                    ListStatus = "Watching",
                    UserScore = 8,
                    LastSyncedUtc = DateTime.UtcNow
                });

            db.VoiceCastRecords.AddRange(
                Cast("mal:100", 100, "Target Anime", VoiceLanguageMode.Sub, "Target Sub Character", "Kana Hanazawa"),
                Cast("mal:100", 100, "Target Anime", VoiceLanguageMode.Dub, "Target Dub Character", "Cherami Leigh"),
                Cast("mal:1", 1, "Familiar Sub Anime", VoiceLanguageMode.Sub, "Known Sub Character", "Kana Hanazawa"),
                Cast("mal:2", 2, "Familiar Dub Anime", VoiceLanguageMode.Dub, "Known Dub Character", "Cherami Leigh"));
            db.SaveChanges();
        }

        private static VoiceCastRecord Cast(
            string mediaKey,
            int malId,
            string title,
            VoiceLanguageMode mode,
            string character,
            string voiceActor)
        {
            return new VoiceCastRecord
            {
                MediaKey = mediaKey,
                MalId = malId,
                ShowTitle = title,
                LanguageMode = mode.ToString(),
                Source = "Test cache",
                RoleType = "Main",
                CharacterName = character,
                VoiceActorName = voiceActor,
                FetchedAtUtc = DateTime.UtcNow
            };
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            public List<string> RequestUris { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
                return _handler(request, cancellationToken);
            }
        }

        private sealed class AppDataSandbox : IDisposable
        {
            private readonly string? _previousAppData;

            public AppDataSandbox(Dictionary<string, string>? extraSettings = null)
            {
                _previousAppData = Environment.GetEnvironmentVariable("APPDATA");
                Root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
                string appDir = Path.Combine(Root, "UniversalMediaOS");
                Directory.CreateDirectory(appDir);
                ConfigPath = Path.Combine(appDir, "config.json");
                string databasePath = Path.Combine(Root, "va-detect.db");
                var settings = new Dictionary<string, string>
                {
                    ["DatabasePath"] = databasePath
                };

                if (extraSettings != null)
                {
                    foreach (var item in extraSettings)
                    {
                        settings[item.Key] = item.Value;
                    }
                }

                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings));
                Environment.SetEnvironmentVariable("APPDATA", Root);
            }

            public string Root { get; }
            public string ConfigPath { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("APPDATA", _previousAppData);
                try
                {
                    Directory.Delete(Root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
