using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalMediaOS.Tests.E2E.Infrastructure
{
    public class MockHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts;
        private Task? _listenerTask;
        private readonly byte[]? _mangaPagePng;
        private readonly Func<HttpListenerRequest, (int Status, string Body)>? _mangaChapterFeed;

        public string BaseUrl { get; }

        public MockHttpServer(int port = 0, byte[]? mangaPagePng = null,
            Func<HttpListenerRequest, (int Status, string Body)>? mangaChapterFeed = null)
        {
            _mangaPagePng = mangaPagePng;
            _mangaChapterFeed = mangaChapterFeed;
            if (port <= 0)
            {
                using var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            }

            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl);
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            _listener.Start();
            _listenerTask = Task.Run(ListenLoop);
        }

        private async Task ListenLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"MockServer error: {ex.Message}");
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var req = context.Request;
                var res = context.Response;
                res.Headers.Add("Access-Control-Allow-Origin", "*");
                res.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
                res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
                
                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 204;
                    res.Close();
                    return;
                }

                string path = req.Url?.AbsolutePath ?? "/";
                string query = req.Url?.Query ?? "";
                string responseBody = "";
                res.ContentType = "application/json";

                // Serve dummy media file
                if (path == "/mock_video.mkv" || path.EndsWith(".mkv") || path.EndsWith(".mp4"))
                {
                    res.ContentType = "video/x-matroska";
                    byte[] dummyVideo = new byte[1024 * 1024]; // 1MB of zeroes
                    res.ContentLength64 = dummyVideo.Length;
                    res.OutputStream.Write(dummyVideo, 0, dummyVideo.Length);
                    res.Close();
                    return;
                }

                // Serve dummy cover image
                if (path.StartsWith("/cover") || path.Contains("cover.png") || path.Contains("cover.jpg") || path.Contains("/va.png"))
                {
                    res.ContentType = "image/png";
                    // 1x1 transparent PNG byte array
                    byte[] dummyPng = new byte[] {
                        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0xDA, 0x63, 0x60, 0x60, 0x60, 0x60,
                        0x00, 0x00, 0x00, 0x05, 0x00, 0x01, 0xA5, 0x67, 0x7D, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
                        0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
                    };
                    res.ContentLength64 = dummyPng.Length;
                    res.OutputStream.Write(dummyPng, 0, dummyPng.Length);
                    res.Close();
                    return;
                }

                // 1. QBitLogicGate Mock
                if (path == "/api/v2/auth/login")
                {
                    res.Headers.Add("Set-Cookie", "SID=mock_sid_value; Path=/");
                    responseBody = "Ok.";
                }
                else if (path == "/api/v2/torrents/add")
                {
                    responseBody = "Ok.";
                }
                else if (path == "/api/v2/torrents/info")
                {
                    responseBody = "[{\"progress\": 1.0, \"state\": \"downloading\", \"dlspeed\": 1024000, \"hash\": \"0123456789abcdef0123456789abcdef01234567\", \"name\": \"[MockSubs] Mock Anime - Season 1 [1080p] [Batch]\"}]";
                }
                else if (path == "/api/v2/torrents/files")
                {
                    responseBody = "[{\"name\": \"Mock_Episode_01.mkv\", \"size\": 50000000}]";
                }
                else if (path == "/api/v2/transfer/info")
                {
                    responseBody = "{\"dl_info_speed\": 1024000, \"up_info_speed\": 204800}";
                }
                else if (path == "/api/v2/app/shutdown")
                {
                    responseBody = "Ok.";
                }
                
                // 2. Consumet Scraper Microservice Mock
                else if (path.Contains("/anime/") && path.Contains("/info/"))
                {
                    responseBody = "{\"id\":\"mock-anime\",\"title\":\"Mock Anime\",\"totalEpisodes\":1,\"episodes\":[{\"id\":\"mock-anime-episode-1\",\"number\":1,\"url\":\"/mock-anime-episode-1\"}]}";
                }
                else if (path.Contains("/anime/") && path.Contains("/watch/"))
                {
                    responseBody = "{\"episodeId\":\"mock-anime-episode-1\",\"embedUrl\":\"http://localhost:5000/embed\",\"sources\":[{\"url\":\"http://localhost:5000/mock_video.mkv\",\"quality\":\"1080p\",\"isM3U8\":false}]}";
                }
                else if (path.Contains("/anime/"))
                {
                    responseBody = "{\"results\":[{\"id\":\"mock-anime\",\"title\":\"Mock Anime\",\"url\":\"/info/mock-anime\",\"image\":\"http://localhost:5000/cover.png\"}]}";
                }

                // 3. AniList GraphQL Mock
                else if (path == "/graphql" || path.Contains("graphql"))
                {
                    // Returns Romaji, English, and Characters with both Japanese and English voice actors for T1_Details_01 character gallery swapping test.
                    responseBody = "{\"data\":{\"Page\":{\"media\":[{\"id\":1,\"idMal\":1,\"title\":{\"romaji\":\"Mock Romaji\",\"english\":\"Mock English\"},\"coverImage\":{\"extraLarge\":\"http://localhost:5000/cover.png\"},\"description\":\"Mock Description\"}]},\"Media\":{\"id\":1,\"title\":{\"romaji\":\"Mock Romaji\",\"english\":\"Mock English\"},\"description\":\"Mock Description\",\"characters\":{\"edges\":[{\"role\":\"MAIN\",\"node\":{\"name\":{\"full\":\"Frieren\"},\"image\":{\"large\":\"http://localhost:5000/cover.png\"}},\"voiceActors\":[{\"name\":{\"full\":\"Atsumi Tanezaki\"},\"language\":\"JAPANESE\",\"image\":{\"large\":\"http://localhost:5000/va.png\"}},{\"name\":{\"full\":\"John Doe\"},\"language\":\"ENGLISH\",\"image\":{\"large\":\"http://localhost:5000/va.png\"}}]}]}}}}";
                }

                // 4. AniSkip API Mock
                else if (path.Contains("/v2/skip-times/"))
                {
                    responseBody = "{\"found\":true,\"results\":[{\"skipType\":\"op\",\"interval\":{\"startTime\":10.0,\"endTime\":90.0}},{\"skipType\":\"ed\",\"interval\":{\"startTime\":1000.0,\"endTime\":1100.0}}]}";
                }

                // 5. MAL Progress Update Mock
                else if (path.Contains("/my_list_status"))
                {
                    responseBody = "{\"status\":\"completed\"}";
                }

                // 6. RSS Feeds (Nyaa / AnimeTosho)
                else if (path == "/nyaa" || path == "/animetosho" || path.Contains("nyaa") || path.Contains("animetosho"))
                {
                    res.ContentType = "application/xml";
                    responseBody = @"<rss version=""2.0"" xmlns:nyaa=""https://nyaa.si/xmlns/nyaa"">
                        <channel>
                            <title>Mock Feed</title>
                            <item>
                                <title>[MockSubs] Mock Anime - Season 1 [1080p] [Batch]</title>
                                <link>magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567</link>
                                <nyaa:infoHash>0123456789abcdef0123456789abcdef01234567</nyaa:infoHash>
                                <nyaa:seeders>50</nyaa:seeders>
                            </item>
                        </channel>
                    </rss>";
                }

                // 7. MangaDex API Mock
                else if (path.Contains("/manga") && path.Contains("/feed"))
                {
                    var feed = _mangaChapterFeed?.Invoke(req);
                    res.StatusCode = feed?.Status ?? 200;
                    responseBody = feed?.Body ?? "{\"total\": 1, \"data\": [{\"id\": \"mock-chapter-1\", \"type\": \"chapter\", \"attributes\": {\"chapter\": \"1\", \"title\": \"Chapter 1\", \"pages\": 2, \"externalUrl\": \"\"}}]}";
                }
                else if (path.Contains("/at-home/server/"))
                {
                    responseBody = "{\"baseUrl\": \"http://localhost:5000/mangapages\", \"chapter\": {\"hash\": \"mockhash\", \"data\": [\"page1.png\", \"page2.png\"]}}";
                }
                else if (path.Contains("/mangapages/data/"))
                {
                    // Serve dummy page image
                    res.ContentType = "image/png";
                    byte[] dummyPng = _mangaPagePng ?? new byte[] {
                        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0xDA, 0x63, 0x60, 0x60, 0x60, 0x60,
                        0x00, 0x00, 0x00, 0x05, 0x00, 0x01, 0xA5, 0x67, 0x7D, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
                        0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
                    };
                    res.ContentLength64 = dummyPng.Length;
                    res.OutputStream.Write(dummyPng, 0, dummyPng.Length);
                    res.Close();
                    return;
                }
                else if (path.Contains("/manga"))
                {
                    responseBody = "{\"data\": [{\"id\": \"mock-manga-id\", \"type\": \"manga\", \"attributes\": {\"title\": {\"en\": \"Mock Manga\"}}, \"relationships\": [{\"id\": \"mock-cover-id\", \"type\": \"cover_art\", \"attributes\": {\"fileName\": \"cover.jpg\"}}]}]}";
                }

                string baseUrl = BaseUrl.TrimEnd('/');
                responseBody = responseBody.Replace("http://localhost:5000", baseUrl);

                byte[] buffer = Encoding.UTF8.GetBytes(responseBody);
                res.ContentLength64 = buffer.Length;
                res.OutputStream.Write(buffer, 0, buffer.Length);
                res.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MockHttpServer HandleRequest error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _listenerTask?.Wait(); } catch { }
        }
    }
}
