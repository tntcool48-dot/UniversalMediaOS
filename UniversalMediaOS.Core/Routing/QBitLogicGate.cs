using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Routing
{
    public class QBitFileInfo
    {
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    public class QBitLogicGate
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private readonly string _qbitUrl;
        public string Cookie { get; private set; } = string.Empty;

        public QBitLogicGate(string baseUrl = "http://localhost:8080")
        {
            _qbitUrl = baseUrl.TrimEnd('/');
        }

        public async Task<bool> AuthenticateAsync(Action<string> logger = null, string username = "admin", string password = "adminadmin")
        {
            void Log(string msg) { logger?.Invoke(msg); System.Diagnostics.Debug.WriteLine(msg); }
            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", username),
                    new KeyValuePair<string, string>("password", password)
                });

                var response = await _httpClient.PostAsync($"{_qbitUrl}/api/v2/auth/login", content);
                if (response.IsSuccessStatusCode && response.Headers.TryGetValues("Set-Cookie", out var cookies))
                {
                    foreach (var c in cookies)
                    {
                        if (c.StartsWith("SID="))
                        {
                            Cookie = c.Split(';')[0];
                            return true;
                        }
                    }
                }
                else
                {
                    Log($"> [QBit] WebUI Login rejected. Please check {_qbitUrl}. Status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Log($"> [QBit] WebUI unreachable at {_qbitUrl}. Enable 'Web User Interface' in qBittorrent settings. ({ex.Message})");
            }
            return false;
        }



        public async Task<bool> AddMagnetAsync(string magnetLink, string savePath)
        {
            if (string.IsNullOrEmpty(Cookie)) return false;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_qbitUrl}/api/v2/torrents/add");
                request.Headers.Add("Cookie", Cookie);

                var content = new MultipartFormDataContent();
                content.Add(new StringContent(magnetLink), "urls");
                content.Add(new StringContent("true"), "sequentialDownload");
                if (!string.IsNullOrEmpty(savePath))
                {
                    content.Add(new StringContent(savePath), "savepath");
                }

                request.Content = content;
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    return body.Trim().Equals("Ok.", StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to add magnet: {ex.Message}");
            }
            return false;
        }

        public async Task ShutdownAsync()
        {
            if (string.IsNullOrEmpty(Cookie)) return;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{_qbitUrl}/api/v2/app/shutdown");
                request.Headers.Add("Cookie", Cookie);
                await _httpClient.SendAsync(request);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to gracefully shutdown qBittorrent: {ex.Message}");
            }
        }

        /// <summary>
        /// Polls the qBittorrent WebUI for download progress until the torrent completes or times out.
        /// </summary>
        public async Task<bool> MonitorDownloadAsync(string infoHash, Action<string> logger, int timeoutSeconds = 300)
        {
            void Log(string msg) { logger?.Invoke(msg); System.Diagnostics.Debug.WriteLine(msg); }
            if (string.IsNullOrEmpty(Cookie)) return false;

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{_qbitUrl}/api/v2/torrents/info?hashes={infoHash}");
                    request.Headers.Add("Cookie", Cookie);

                    var response = await _httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);

                        if (doc.RootElement.GetArrayLength() > 0)
                        {
                            var torrent = doc.RootElement[0];
                            double progress = torrent.GetProperty("progress").GetDouble();
                            string state = torrent.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : "unknown";
                            long dlSpeed = torrent.TryGetProperty("dlspeed", out var speedProp) ? speedProp.GetInt64() : 0;

                            double pct = progress * 100.0;
                            string speedStr = dlSpeed > 0 ? $"{dlSpeed / 1024.0:F1} KB/s" : "0 KB/s";
                            Log($"> [Tier 1] Download: {pct:F1}% | Speed: {speedStr} | State: {state}");

                            if (progress >= 1.0)
                            {
                                Log("> [Tier 1] Download complete!");
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"> [Tier 1] Monitor error: {ex.Message}");
                }

                await Task.Delay(3000);
            }

            Log($"> [Tier 1] Download timed out after {timeoutSeconds}s.");
            return false;
        }

        /// <summary>
        /// Returns the list of file paths within a torrent.
        /// </summary>
        public async Task<List<QBitFileInfo>> GetTorrentFilesAsync(string infoHash)
        {
            var files = new List<QBitFileInfo>();
            if (string.IsNullOrEmpty(Cookie)) return files;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_qbitUrl}/api/v2/torrents/files?hash={infoHash}");
                request.Headers.Add("Cookie", Cookie);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.TryGetProperty("name", out var nameProp))
                        {
                            long size = 0;
                            if (element.TryGetProperty("size", out var sizeProp))
                            {
                                size = sizeProp.GetInt64();
                            }
                            files.Add(new QBitFileInfo { Name = nameProp.GetString() ?? "", Size = size });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to get torrent files: {ex.Message}");
            }

            return files;
        }

        public async Task<QBitTransferInfo?> GetGlobalTransferInfoAsync()
        {
            if (string.IsNullOrEmpty(Cookie)) return null;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_qbitUrl}/api/v2/transfer/info");
                request.Headers.Add("Cookie", Cookie);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    
                    var info = new QBitTransferInfo();
                    if (doc.RootElement.TryGetProperty("dl_info_speed", out var dlSpeed))
                        info.DlInfoSpeed = dlSpeed.GetInt64();
                    
                    if (doc.RootElement.TryGetProperty("up_info_speed", out var upSpeed))
                        info.UpInfoSpeed = upSpeed.GetInt64();
                        
                    return info;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to get transfer info: {ex.Message}");
            }
            return null;
        }
    }

    public class QBitTransferInfo
    {
        public long DlInfoSpeed { get; set; }
        public long UpInfoSpeed { get; set; }
    }
}
