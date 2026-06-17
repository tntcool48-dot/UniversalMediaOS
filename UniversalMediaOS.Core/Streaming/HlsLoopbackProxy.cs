using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Streaming
{
    /// <summary>
    /// A session registered with the HLS loopback proxy.
    /// Carries all CDN authentication material captured by the Python scraper.
    /// </summary>
    public sealed record ProxySession(
        string RemoteM3U8Url,
        string? UserAgent,
        string? Cookie,
        string? KeyUrl,
        string? Referer,
        DateTime CreatedAt);

    /// <summary>
    /// Lightweight self-contained HTTP proxy running on 127.0.0.1:19475.
    /// Routes LibVLC through a session so CDN authentication headers are
    /// injected transparently for every request (manifests, segments, key files).
    ///
    /// Proxy routes:
    ///   GET /stream?id={guid}[&url={encoded}]  — fetch + rewrite m3u8 manifest
    ///   GET /seg?id={guid}&url={encoded}        — raw segment passthrough
    ///   GET /key?id={guid}&url={encoded}        — AES-128 key passthrough
    /// </summary>
    public sealed class HlsLoopbackProxy : IDisposable
    {
        // Port chosen to avoid conflict with qBit (8080), common dev servers (3000/3001), etc.
        private const string ListenPrefix = "http://127.0.0.1:19475/";
        private const int SessionExpiryHours = 4;

        private readonly HttpListener _listener = new HttpListener();
        private readonly ConcurrentDictionary<string, ProxySession> _sessions = new();
        private readonly Timer _gcTimer;
        private readonly object _lifecycleLock = new();
        private bool _disposed;

        public bool IsRunning => _listener.IsListening;
        public string? LastStartupError { get; private set; }

        // Single shared HttpClient — supports HTTP/3 with graceful downgrade
        private static readonly HttpClient _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            EnableMultipleHttp2Connections = true
        })
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = TimeSpan.FromSeconds(30)
        };

        public HlsLoopbackProxy()
        {
            _listener.Prefixes.Add(ListenPrefix);
            // GC expired sessions every 30 minutes
            _gcTimer = new Timer(_ => PurgeExpiredSessions(), null,
                TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        }

        // ── Lifecycle ────────────────────────────────────────────────────────

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    LastStartupError = "Proxy has already been disposed.";
                    AppLogger.Log($"[HLS Proxy] Start skipped: {LastStartupError}", "WARNING");
                    return;
                }

                if (_listener.IsListening)
                    return;

                try
                {
                    LastStartupError = null;
                    _listener.Start();
                    _ = Task.Run(AcceptLoopAsync);
                    AppLogger.Log($"[HLS Proxy] Listening on {ListenPrefix}");
                }
                catch (Exception ex)
                {
                    LastStartupError = ex.Message;
                    AppLogger.Log($"[HLS Proxy] Failed to start on {ListenPrefix}: {ex.Message}", "ERROR");
                }
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (!_listener.IsListening)
                    return;

                try { _listener.Stop(); } catch { }
            }
        }

        // ── Session Management ───────────────────────────────────────────────

        /// <summary>
        /// Registers a new session and returns its GUID for use in proxied URLs.
        /// </summary>
        public string RegisterSession(ProxySession session)
        {
            string id = Guid.NewGuid().ToString("N");
            _sessions[id] = session;
            AppLogger.Log($"[HLS Proxy] Session registered: {id} → {session.RemoteM3U8Url}");
            return id;
        }

        private void PurgeExpiredSessions()
        {
            var cutoff = DateTime.UtcNow.AddHours(-SessionExpiryHours);
            foreach (var kvp in _sessions)
            {
                if (kvp.Value.CreatedAt < cutoff)
                    _sessions.TryRemove(kvp.Key, out _);
            }
        }

        // ── Request Dispatch ─────────────────────────────────────────────────

        private async Task AcceptLoopAsync()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(ctx));
                }
                catch (HttpListenerException) when (_disposed || !_listener.IsListening)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex) when (!_disposed)
                {
                    AppLogger.Log($"[HLS Proxy] Accept error: {ex.Message}", "WARNING");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var resp = ctx.Response;
                string query = req.Url?.Query ?? "";

                string path = req.Url?.AbsolutePath ?? "/";
                string? sessionId = DecodeQueryComponent(GetRawQueryValue(query, "id"));

                if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
                {
                    await WriteError(resp, 404, "Session not found or expired.");
                    return;
                }

                if (path == "/stream")
                {
                    // Manifest route — fetch remote m3u8 and rewrite all URIs
                    string remoteUrl = GetRawQueryValue(query, "url") is { } u && u.Length > 0
                        ? DecodeQueryComponent(u)
                        : session.RemoteM3U8Url;

                    await ServeManifestAsync(resp, session, sessionId, remoteUrl, req);
                }
                else if (path == "/seg")
                {
                    // Segment route — raw byte passthrough
                    string? rawUrl = GetRawQueryValue(query, "url");
                    if (string.IsNullOrEmpty(rawUrl))
                    { await WriteError(resp, 400, "Missing url param"); return; }
                    await ServeSegmentAsync(resp, session, DecodeQueryComponent(rawUrl), req);
                }
                else if (path == "/key")
                {
                    // AES-128 key route — fetch key with Cookie+Referer injected
                    string? rawUrl = GetRawQueryValue(query, "url");
                    string? keyUrl = !string.IsNullOrEmpty(rawUrl)
                        ? DecodeQueryComponent(rawUrl)
                        : session.KeyUrl;
                    if (string.IsNullOrEmpty(keyUrl))
                    { await WriteError(resp, 400, "Missing url param"); return; }
                    await ServeKeyAsync(resp, session, keyUrl);
                }
                else
                {
                    await WriteError(resp, 404, "Unknown proxy route.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[HLS Proxy] Request handler error: {ex.Message}", "WARNING");
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        // ── Manifest Handler ─────────────────────────────────────────────────

        private async Task ServeManifestAsync(
            HttpListenerResponse resp,
            ProxySession session,
            string sessionId,
            string remoteUrl,
            HttpListenerRequest clientReq)
        {
            using var outReq = BuildRequest(HttpMethod.Get, remoteUrl, session);
            using var outResp = await _http.SendAsync(outReq, HttpCompletionOption.ResponseContentRead);

            if (!outResp.IsSuccessStatusCode)
            {
                await WriteError(resp, (int)outResp.StatusCode,
                    $"CDN returned {outResp.StatusCode} for manifest.");
                return;
            }

            string m3u8Text = await outResp.Content.ReadAsStringAsync();
            string baseUrl = GetBaseUrl(remoteUrl);
            string rewritten = RewriteManifest(m3u8Text, baseUrl, sessionId, session);

            byte[] bytes = Encoding.UTF8.GetBytes(rewritten);
            resp.StatusCode = 200;
            resp.ContentType = "application/x-mpegURL";
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes);
            resp.Close();
        }

        // ── Segment Handler ──────────────────────────────────────────────────

        private async Task ServeSegmentAsync(
            HttpListenerResponse resp,
            ProxySession session,
            string remoteUrl,
            HttpListenerRequest clientReq)
        {
            using var outReq = BuildRequest(HttpMethod.Get, remoteUrl, session);

            // Forward Range header faithfully for seek support
            string? rangeHeader = clientReq.Headers["Range"];
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                outReq.Headers.TryAddWithoutValidation("Range", rangeHeader);
            }

            using var outResp = await _http.SendAsync(outReq, HttpCompletionOption.ResponseHeadersRead);

            resp.StatusCode = (int)outResp.StatusCode;
            resp.ContentType = outResp.Content.Headers.ContentType?.ToString() ?? "video/MP2T";

            if (outResp.Content.Headers.ContentLength.HasValue)
                resp.ContentLength64 = outResp.Content.Headers.ContentLength.Value;

            // Propagate Content-Range for 206 responses
            if (outResp.Headers.TryGetValues("Content-Range", out var cr))
                resp.Headers["Content-Range"] = string.Join(",", cr);

            await using var stream = await outResp.Content.ReadAsStreamAsync();
            await stream.CopyToAsync(resp.OutputStream);
            resp.Close();
        }

        // ── AES Key Handler ──────────────────────────────────────────────────

        private async Task ServeKeyAsync(
            HttpListenerResponse resp,
            ProxySession session,
            string remoteUrl)
        {
            using var outReq = BuildRequest(HttpMethod.Get, remoteUrl, session);
            using var outResp = await _http.SendAsync(outReq, HttpCompletionOption.ResponseContentRead);

            byte[] keyBytes = await outResp.Content.ReadAsByteArrayAsync();
            resp.StatusCode = (int)outResp.StatusCode;
            resp.ContentType = "application/octet-stream";
            resp.ContentLength64 = keyBytes.Length;
            await resp.OutputStream.WriteAsync(keyBytes);
            resp.Close();
        }

        // ── M3U8 Rewriter ────────────────────────────────────────────────────

        /// <summary>
        /// Rewrites all non-comment URIs in an HLS manifest so they route through
        /// the local proxy. Handles:
        ///   - Relative URIs   → resolved to absolute, then proxied
        ///   - .m3u8 variants  → routed via /stream  (recursive manifest rewriting)
        ///   - .ts/.aac/.mp4   → routed via /seg     (raw byte passthrough)
        ///   - #EXT-X-KEY URI= → rewritten in-place, routed via /key
        /// </summary>
        private string RewriteManifest(
            string m3u8Text,
            string baseUrl,
            string sessionId,
            ProxySession session)
        {
            var lines = m3u8Text.Split('\n');
            var sb = new StringBuilder(m3u8Text.Length + lines.Length * 80);

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');

                // Tags such as EXT-X-KEY, EXT-X-MAP, EXT-X-MEDIA, and
                // EXT-X-I-FRAME-STREAM-INF can hide CDN URLs inside URI=.
                if (line.StartsWith("#", StringComparison.Ordinal)
                    && line.Contains("URI=", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(RewriteUriTag(line, baseUrl, sessionId, session));
                    continue;
                }

                if (IsKeyTag(line) && !string.IsNullOrWhiteSpace(session.KeyUrl))
                {
                    string absoluteKey = ResolveAbsoluteUri(session.KeyUrl!, baseUrl);
                    string localKey =
                        $"http://127.0.0.1:19475/key?id={sessionId}&url={EncodeQueryComponent(absoluteKey)}";
                    sb.AppendLine($"{line},URI=\"{localKey}\"");
                    continue;
                }

                // All other # tags and blank lines pass through unchanged
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendLine(line);
                    continue;
                }

                // Any non-comment line is a URI (segment, variant playlist, init segment, etc.)
                string absolute = ResolveAbsoluteUri(line, baseUrl);
                bool isPlaylist = absolute.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
                string endpoint = isPlaylist ? "stream" : "seg";

                sb.AppendLine(
                    $"http://127.0.0.1:19475/{endpoint}?id={sessionId}&url={EncodeQueryComponent(absolute)}");
            }

            return sb.ToString();
        }

        private string RewriteUriTag(
            string tagLine,
            string baseUrl,
            string sessionId,
            ProxySession session)
        {
            bool isKeyTag = IsKeyTag(tagLine);

            return Regex.Replace(tagLine,
                @"URI=(?:""([^""]*)""|'([^']*)'|([^,\s]+))",
                m =>
                {
                    string uri = m.Groups[1].Success
                        ? m.Groups[1].Value
                        : m.Groups[2].Success
                            ? m.Groups[2].Value
                            : m.Groups[3].Value;

                    if (string.IsNullOrWhiteSpace(uri) && isKeyTag && !string.IsNullOrWhiteSpace(session.KeyUrl))
                        uri = session.KeyUrl!;

                    if (string.IsNullOrWhiteSpace(uri))
                        return m.Value;

                    string absolute = ResolveAbsoluteUri(uri, baseUrl);
                    string endpoint = isKeyTag
                        ? "key"
                        : absolute.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                            ? "stream"
                            : "seg";

                    string localUrl =
                        $"http://127.0.0.1:19475/{endpoint}?id={sessionId}&url={EncodeQueryComponent(absolute)}";
                    return $"URI=\"{localUrl}\"";
                },
                RegexOptions.IgnoreCase);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static bool IsKeyTag(string line) =>
            line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-SESSION-KEY", StringComparison.OrdinalIgnoreCase);

        private static HttpRequestMessage BuildRequest(
            HttpMethod method, string url, ProxySession session)
        {
            var req = new HttpRequestMessage(method, url);

            if (!string.IsNullOrEmpty(session.UserAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", session.UserAgent);
            else
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (!string.IsNullOrEmpty(session.Cookie))
                req.Headers.TryAddWithoutValidation("Cookie", session.Cookie);

            if (!string.IsNullOrEmpty(session.Referer))
                req.Headers.TryAddWithoutValidation("Referer", session.Referer);

            req.Headers.Accept.ParseAdd("*/*");
            req.Headers.AcceptEncoding.ParseAdd("identity"); // prevent gzip on TS chunks
            return req;
        }

        /// <summary>
        /// Resolves a potentially relative URI to an absolute URL using the manifest's base URL.
        /// </summary>
        private static string ResolveAbsoluteUri(string uri, string baseUrl)
        {
            if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return uri;

            if (uri.StartsWith("//"))
            {
                // Protocol-relative
                string scheme = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? "https:" : "http:";
                return scheme + uri;
            }

            try
            {
                return new Uri(new Uri(baseUrl), uri).AbsoluteUri;
            }
            catch
            {
                return uri; // best-effort fallback
            }
        }

        private static string EncodeQueryComponent(string value) =>
            Uri.EscapeDataString(value);

        private static string DecodeQueryComponent(string? value) =>
            string.IsNullOrEmpty(value) ? string.Empty : Uri.UnescapeDataString(value);

        private static string? GetRawQueryValue(string query, string key)
        {
            if (query.StartsWith("?", StringComparison.Ordinal))
                query = query[1..];

            if (string.IsNullOrEmpty(query))
                return null;

            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = part.IndexOf('=');
                string rawKey = equals >= 0 ? part[..equals] : part;
                if (DecodeQueryComponent(rawKey).Equals(key, StringComparison.OrdinalIgnoreCase))
                    return equals >= 0 ? part[(equals + 1)..] : string.Empty;
            }

            return null;
        }

        /// <summary>
        /// Returns the base URL (directory portion) of a full URL, used to resolve relative URIs.
        /// e.g. "https://cdn.com/stream/ep1/index.m3u8" → "https://cdn.com/stream/ep1/"
        /// </summary>
        private static string GetBaseUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                string path = uri.AbsolutePath;
                int lastSlash = path.LastIndexOf('/');
                string dir = lastSlash >= 0 ? path[..(lastSlash + 1)] : "/";
                return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{dir}";
            }
            catch
            {
                return url;
            }
        }

        private static async Task WriteError(HttpListenerResponse resp, int code, string msg)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(msg);
            resp.StatusCode = code;
            resp.ContentType = "text/plain";
            resp.ContentLength64 = bytes.Length;
            try
            {
                await resp.OutputStream.WriteAsync(bytes);
                resp.Close();
            }
            catch { }
        }

        // ── IDisposable ──────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gcTimer.Dispose();
            Stop();
            _listener.Close();
        }
    }
}
