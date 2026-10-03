using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.Services;

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
        DateTime CreatedAt,
        IReadOnlyDictionary<string, string>? RequestHeaders = null,
        string SubtitleContent = "");

    /// <summary>
    /// Lightweight self-contained HTTP proxy running on a dynamically selected
    /// 127.0.0.1 port.
    /// Routes LibVLC through a session so CDN authentication headers are
    /// injected transparently for every request (manifests, segments, key files).
    ///
    /// Proxy routes:
    ///   GET /stream?id={guid}[&url={encoded}]  — fetch + rewrite m3u8 manifest
    ///   GET /seg?id={guid}&url={encoded}        — raw segment passthrough
    ///   GET /key?id={guid}&url={encoded}        — AES-128 key passthrough
    /// </summary>
    public sealed partial class HlsLoopbackProxy : IDisposable
    {
        private const int PortSelectionAttempts = 12;

        private HttpListener? _listener;
        private readonly ConcurrentDictionary<string, RegisteredSession> _sessions = new();
        private static readonly ConcurrentDictionary<string, (bool IsPublic, DateTime ExpiresUtc)> DnsSafetyCache = new();
        private readonly Timer _gcTimer;
        private readonly object _lifecycleLock = new();
        private readonly int _preferredPort;
        private bool _disposed;

        public bool IsRunning
        {
            get
            {
                try { return _listener?.IsListening == true; }
                catch (ObjectDisposedException) { return false; }
            }
        }
        public Uri? ListeningUri { get; private set; }
        public string ListeningEndpoint => IsRunning && ListeningUri != null
            ? ListeningUri.Authority
            : "Not listening";
        public string? LastStartupError { get; private set; }

        // Single shared HttpClient with connect-time public-IP enforcement.
        private static readonly HttpClient _http = CreatePublicNetworkClient();

        internal static HttpClient CreatePublicNetworkClient()
        {
            SocketsHttpHandler handler =
                OtherMediaHttpClientFactory.CreatePublicNetworkHandler(allowAutoRedirect: false);
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15);
            handler.EnableMultipleHttp2Connections = true;
            return new HttpClient(handler, disposeHandler: true)
            {
                // HTTP/2 is the ceiling because QUIC/HTTP/3 does not use the
                // validated TCP ConnectCallback above.
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        /// <param name="preferredPort">
        /// Optional preferred loopback port. Zero lets the operating system choose.
        /// If a preferred port is busy, startup falls back to a free dynamic port.
        /// </param>
        public HlsLoopbackProxy(int preferredPort = 0) : this(_http, new ProxyRequestLimits(), preferredPort) { }

        internal HlsLoopbackProxy(HttpClient remoteHttp, ProxyRequestLimits limits, int preferredPort = 0)
        {
            if (preferredPort is < 0 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(preferredPort));
            }

            _preferredPort = preferredPort;
            _remoteHttp = remoteHttp;
            _limits = limits;
            // Idle sessions expire; active transfers do not age out mid-film.
            _gcTimer = new Timer(_ => PurgeExpiredSessions(), null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
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

                if (IsRunning)
                    return;

                LastStartupError = null;
                ListeningUri = null;
                StopCore();

                Exception? lastError = null;
                var attemptedPorts = new HashSet<int>();
                for (int attempt = 0; attempt < PortSelectionAttempts; attempt++)
                {
                    int port;
                    try
                    {
                        port = attempt == 0 && _preferredPort > 0
                            ? _preferredPort
                            : SelectAvailableLoopbackPort();
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        continue;
                    }

                    if (!attemptedPorts.Add(port))
                    {
                        continue;
                    }

                    string prefix = $"http://127.0.0.1:{port}/";
                    var candidate = new HttpListener();
                    candidate.Prefixes.Add(prefix);
                    try
                    {
                        // Start is the authoritative bind. If another process wins
                        // after port selection, retry with another OS-selected port.
                        candidate.Start();
                        _listener = candidate;
                        ListeningUri = new Uri(prefix, UriKind.Absolute);
                        _runCancellation = new CancellationTokenSource();
                        CancellationToken runToken = _runCancellation.Token;
                        _acceptTask = Task.Run(() => AcceptLoopAsync(candidate, runToken));
                        AppLogger.Log($"[HLS Proxy] Listening on {prefix}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        try { candidate.Close(); } catch { }
                        if (ex is not HttpListenerException)
                        {
                            break;
                        }
                    }
                }

                LastStartupError = lastError == null
                    ? "No unique loopback port could be selected."
                    : $"Unable to bind a loopback port after {PortSelectionAttempts} attempts: {lastError.Message}";
                AppLogger.Log($"[HLS Proxy] Failed to start: {LastStartupError}", "ERROR");
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                StopCore();
            }
        }

        private static int SelectAvailableLoopbackPort()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                reservation.Start();
                return ((IPEndPoint)reservation.LocalEndpoint).Port;
            }
            finally
            {
                reservation.Stop();
            }
        }

        private void CloseListener()
        {
            HttpListener? listener = _listener;
            _listener = null;
            if (listener == null)
            {
                return;
            }

            try { listener.Close(); } catch { }
        }

        // ── Session Management ───────────────────────────────────────────────

        /// <summary>
        /// Registers a new session and returns its GUID for use in proxied URLs.
        /// </summary>
        public string RegisterSession(ProxySession session)
        {
            if (!IsAllowedRemoteUri(session.RemoteM3U8Url))
            {
                throw new ArgumentException("The HLS session URL must be a public HTTP(S) address.", nameof(session));
            }
            if (session.SubtitleContent.Length > 0 && MediaSubtitleTrack.NormalizeInlineVtt(session.SubtitleContent).Length == 0)
                throw new ArgumentException("Loaded captions must be bounded timed WebVTT.", nameof(session));

            lock (_lifecycleLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                PurgeExpiredSessions();
                if (_sessions.Count >= _limits.MaximumSessions)
                    throw new InvalidOperationException("The native player session limit was reached; close an unused player.");
                string id = Guid.NewGuid().ToString("N");
                _sessions[id] = new RegisteredSession(session);
                AppLogger.Log("[HLS Proxy] Player session registered.");
                return id;
            }
        }

        public string CreateStreamUrl(string sessionId, string? remoteUrl = null) =>
            CreateLocalUrl("stream", sessionId, remoteUrl);

        public string CreateDashUrl(string sessionId) => CreateStreamUrl(sessionId) + "&dash=1";

        public string CreateSegmentUrl(string sessionId, string remoteUrl) =>
            CreateLocalUrl("seg", sessionId, remoteUrl);

        public string CreateMediaUrl(string sessionId, string remoteUrl) =>
            CreateLocalUrl("media", sessionId, remoteUrl);

        public string CreateSubtitleUrl(string sessionId, string remoteUrl, string label = "")
        {
            string extension = Path.GetExtension(new Uri(remoteUrl).AbsolutePath).ToLowerInvariant();
            if (_sessions.TryGetValue(sessionId, out var session) && session.Value.SubtitleContent.Length > 0) extension = ".vtt";
            if (extension is not (".vtt" or ".srt" or ".ass" or ".ssa")) extension = ".vtt";
            string name = Regex.Replace(label, @"[^\p{L}\p{N} _-]", "").Trim();
            if (name.Length > 64) name = name[..64];
            return CreateLocalUrl("subtitle/" + Uri.EscapeDataString(
                (name.Length == 0 ? "Captions" : name) + extension), sessionId, remoteUrl);
        }

        public bool UnregisterSession(string sessionId) => RemoveSession(sessionId);

        public bool UsesSession(string source, string sessionId) =>
            ListeningUri is { } listening && Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            IsSameOrigin(uri, listening) && _sessions.ContainsKey(sessionId) &&
            DecodeQueryComponent(GetRawQueryValue(uri.Query, "id")) == sessionId;

        public string CreateVariantUrl(string masterUrl, string variantUrl)
        {
            if (!OwnsStreamUrl(masterUrl) || !OwnsStreamUrl(variantUrl))
                throw new ArgumentException("Quality selection must belong to an active proxy session.");
            var master = new Uri(masterUrl);
            var variant = new Uri(variantUrl);
            string masterId = DecodeQueryComponent(GetRawQueryValue(master.Query, "id"));
            string variantId = DecodeQueryComponent(GetRawQueryValue(variant.Query, "id"));
            string remote = DecodeQueryComponent(GetRawQueryValue(variant.Query, "url"));
            if (masterId != variantId || string.IsNullOrWhiteSpace(remote))
                throw new ArgumentException("Quality selection must reference a variant of the same session.");
            return masterUrl + "&variant=" + Uri.EscapeDataString(remote);
        }

        public bool OwnsStreamUrl(string value)
        {
            Uri? listeningUri = ListeningUri;
            if (!IsRunning || listeningUri == null ||
                !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !uri.Scheme.Equals(listeningUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !uri.Host.Equals(listeningUri.Host, StringComparison.OrdinalIgnoreCase) ||
                uri.Port != listeningUri.Port ||
                !uri.AbsolutePath.Equals("/stream", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string sessionId = DecodeQueryComponent(GetRawQueryValue(uri.Query, "id"));
            lock (_lifecycleLock)
            {
                if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
                    return false;
                if (IsExpired(session)) { RemoveSession(sessionId); return false; }
                return true;
            }
        }

        private string CreateLocalUrl(string endpoint, string sessionId, string? remoteUrl = null)
        {
            Uri? listeningUri = ListeningUri;
            if (!IsRunning || listeningUri == null)
            {
                throw new InvalidOperationException("The HLS loopback proxy is not listening.");
            }

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("A session ID is required.", nameof(sessionId));
            }

            string url = $"{listeningUri.AbsoluteUri.TrimEnd('/')}/{endpoint}?id={EncodeQueryComponent(sessionId)}";
            return string.IsNullOrWhiteSpace(remoteUrl)
                ? url
                : $"{url}&url={EncodeQueryComponent(remoteUrl)}";
        }

        private void PurgeExpiredSessions()
        {
            lock (_lifecycleLock)
            {
                foreach (var kvp in _sessions)
                    if (IsExpired(kvp.Value)) RemoveSession(kvp.Key);
            }

            foreach (var cached in DnsSafetyCache)
            {
                if (cached.Value.ExpiresUtc <= DateTime.UtcNow)
                    DnsSafetyCache.TryRemove(cached.Key, out _);
            }
        }

        // ── Request Dispatch ─────────────────────────────────────────────────

        private async Task AcceptLoopAsync(HttpListener listener, CancellationToken runToken)
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    if (!TryAdmitRequest(runToken)) { RejectRequest(ctx.Response, 503); continue; }
                    _ = Task.Run(() => HandleRequestAsync(ctx, runToken));
                }
                catch (HttpListenerException) when (_disposed || !listener.IsListening)
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

        private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken runToken)
        {
            RegisteredSession? owner = null;
            try
            {
                var req = ctx.Request;
                var resp = ctx.Response;
                string query = req.Url?.Query ?? "";

                string path = req.Url?.AbsolutePath ?? "/";
                string? sessionId = DecodeQueryComponent(GetRawQueryValue(query, "id"));

                if (req.HttpMethod != "GET") { RejectRequest(resp, 405); return; }
                CancellationTokenSource requestCancellation;
                lock (_lifecycleLock)
                {
                    if (runToken.IsCancellationRequested || string.IsNullOrEmpty(sessionId) ||
                        !_sessions.TryGetValue(sessionId, out var registered))
                    {
                        RejectRequest(resp, 404); return;
                    }
                    if (IsExpired(registered)) { RemoveSession(sessionId); RejectRequest(resp, 404); return; }
                    if (registered.ActiveRequests >= _limits.MaximumSessionRequests) { RejectRequest(resp, 429); return; }
                    requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(runToken, registered.Cancellation.Token);
                    owner = registered;
                    owner.ActiveRequests++;
                    owner.LastActivityUtc = DateTime.UtcNow;
                }
                using var requestLifetime = requestCancellation;
                CancellationToken token = requestLifetime.Token;
                var session = owner.Value;

                if (path == "/stream")
                {
                    // Manifest route — fetch remote m3u8 and rewrite all URIs
                    string remoteUrl = GetRawQueryValue(query, "url") is { } u && u.Length > 0
                        ? DecodeQueryComponent(u)
                        : session.RemoteM3U8Url;

                    string? variant = GetRawQueryValue(query, "variant") is { Length: > 0 } selected
                        ? DecodeQueryComponent(selected) : null;
                    if (GetRawQueryValue(query, "dash") == "1")
                        await ServeDashManifestAsync(resp, session, sessionId, remoteUrl, token);
                    else await ServeManifestAsync(resp, session, sessionId, remoteUrl, variant, token);
                }
                else if (path.StartsWith("/subtitle/", StringComparison.Ordinal))
                {
                    await ServeSubtitleAsync(resp, session, token);
                }
                else if (path == "/seg" || path == "/media" || path == "/dashseg" || path == "/dashinit")
                {
                    // Segment route — raw byte passthrough
                    string? rawUrl = GetRawQueryValue(query, "url");
                    if (string.IsNullOrEmpty(rawUrl))
                    { await WriteError(resp, 400, "Missing url param"); return; }
                    if (path == "/dashinit") await ServeDashInitializationAsync(resp, session, DecodeQueryComponent(rawUrl), token);
                    else if (path == "/dashseg") await ServeDashSegmentAsync(resp, session, DecodeQueryComponent(rawUrl), req, token);
                    else await ServeSegmentAsync(resp, session, DecodeQueryComponent(rawUrl), req, token, path == "/media");
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
                    await ServeKeyAsync(resp, session, keyUrl, token);
                }
                else
                {
                    await WriteError(resp, 404, "Unknown proxy route.");
                }
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    AppLogger.Log($"[HLS Proxy] Request failed: {ex.GetType().Name}.", "WARNING");
                // A partially relayed body must be aborted, never completed as a
                // successful truncated response or replaced with error bytes.
                try { ctx.Response.Abort(); } catch { }
            }
            finally { CompleteRequest(owner); }

        }

        // ── Manifest Handler ─────────────────────────────────────────────────

        private async Task ServeManifestAsync(
            HttpListenerResponse resp,
            ProxySession session,
            string sessionId,
            string remoteUrl,
            string? selectedVariant = null,
            CancellationToken token = default)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(_limits.ManifestDeadline);
            using var outResp = await SendSafeAsync(
                remoteUrl,
                session,
                HttpCompletionOption.ResponseHeadersRead, token: deadline.Token);

            if (!outResp.IsSuccessStatusCode)
            {
                await WriteError(resp, (int)outResp.StatusCode,
                    $"CDN returned {outResp.StatusCode} for manifest.");
                return;
            }

            string m3u8Text;
            try
            {
                byte[] manifestBytes = await ReadWithLimitAsync(outResp.Content, 5 * 1024 * 1024, deadline.Token);
                m3u8Text = Encoding.UTF8.GetString(manifestBytes);
            }
            catch (InvalidDataException ex)
            {
                await WriteError(resp, 502, ex.Message);
                return;
            }

            if (!m3u8Text.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                await WriteError(resp, 502, "The CDN response was not a valid HLS manifest.");
                return;
            }
            string effectiveUrl = outResp.RequestMessage?.RequestUri?.AbsoluteUri ?? remoteUrl;
            string baseUrl = GetBaseUrl(effectiveUrl);
            string rewritten;
            try
            {
                ValidateManifestShape(m3u8Text);
                if (!string.IsNullOrEmpty(selectedVariant))
                    m3u8Text = SelectVariantManifest(m3u8Text, baseUrl, selectedVariant);
                rewritten = RewriteManifest(m3u8Text, baseUrl, sessionId, session);
            }
            catch (InvalidDataException ex) { await WriteError(resp, 502, ex.Message); return; }

            byte[] bytes = Encoding.UTF8.GetBytes(rewritten);
            resp.StatusCode = 200;
            resp.ContentType = "application/x-mpegURL";
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, deadline.Token);
            resp.Close();
        }

        internal static string SelectVariantManifest(string manifest, string baseUrl, string selectedVariant)
        {
            var output = new StringBuilder();
            string? pending = null;
            bool selected = false;
            foreach (string raw in manifest.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                {
                    pending = raw;
                    continue;
                }
                if (pending != null && line.Length > 0 && !line.StartsWith('#'))
                {
                    if (new Uri(new Uri(baseUrl), line).AbsoluteUri == selectedVariant)
                    {
                        output.AppendLine(pending).AppendLine(raw);
                        selected = true;
                    }
                    pending = null;
                    continue;
                }
                output.AppendLine(raw);
            }
            if (!selected) throw new InvalidDataException("The requested quality is not advertised by this master playlist.");
            return output.ToString();
        }

        private async Task ServeSubtitleAsync(HttpListenerResponse resp, ProxySession session, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(_limits.SubtitleDeadline);
            if (session.SubtitleContent.Length > 0)
            {
                byte[] loaded = Encoding.UTF8.GetBytes(session.SubtitleContent);
                resp.StatusCode = 200;
                resp.ContentType = "text/vtt; charset=utf-8";
                resp.ContentLength64 = loaded.Length;
                await resp.OutputStream.WriteAsync(loaded, deadline.Token);
                resp.Close();
                return;
            }
            using var remote = await SendSafeAsync(session.RemoteM3U8Url, session,
                HttpCompletionOption.ResponseHeadersRead, token: deadline.Token);
            if (!remote.IsSuccessStatusCode)
            {
                await WriteError(resp, (int)remote.StatusCode, "Caption file is unavailable.");
                return;
            }
            byte[] bytes = await ReadWithLimitAsync(remote.Content, 4 * 1024 * 1024, deadline.Token);
            if (!IsSubtitlePayload(bytes))
            {
                await WriteError(resp, 502, "The caption response was not a supported timed-text file.");
                return;
            }
            resp.StatusCode = 200;
            resp.ContentType = "text/plain";
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, deadline.Token);
            resp.Close();
        }

        internal static bool IsSubtitlePayload(ReadOnlySpan<byte> data)
        {
            string text = Encoding.UTF8.GetString(data[..Math.Min(data.Length, 4096)]).TrimStart('\ufeff', ' ', '\r', '\n', '\t');
            if (text.StartsWith('<') || text.StartsWith('{') || text.StartsWith('[') &&
                !text.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase)) return false;
            return text.StartsWith("WEBVTT", StringComparison.Ordinal) ||
                text.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(text, @"\d{1,2}:\d{2}:\d{2}[.,]\d{3}\s+-->\s+\d{1,2}:\d{2}:\d{2}[.,]\d{3}");
        }

        // ── Segment Handler ──────────────────────────────────────────────────

        private async Task ServeSegmentAsync(
            HttpListenerResponse resp,
            ProxySession session,
            string remoteUrl,
            HttpListenerRequest clientReq,
            CancellationToken token,
            bool directMedia = false)
        {
            // Forward Range header faithfully for seek support
            string? rangeHeader = clientReq.Headers["Range"];
            if (rangeHeader != null && (!RangeHeaderValue.TryParse(rangeHeader, out var range) ||
                range.Unit != "bytes" || range.Ranges.Count != 1))
            { await WriteError(resp, 400, "A single media byte range is required."); return; }
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(token);
            overall.CancelAfter(directMedia ? _limits.DirectMediaDeadline : _limits.MediaDeadline);
            long maximumBytes = directMedia ? _limits.MaximumDirectMediaBytes : _limits.MaximumSegmentBytes;
            using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            headersDeadline.CancelAfter(_limits.HeaderDeadline);
            using var outResp = await SendSafeAsync(
                remoteUrl,
                session,
                HttpCompletionOption.ResponseHeadersRead,
                rangeHeader, headersDeadline.Token);
            headersDeadline.CancelAfter(Timeout.InfiniteTimeSpan);

            if (!outResp.IsSuccessStatusCode)
            { await WriteError(resp, (int)outResp.StatusCode, "The media resource is unavailable."); return; }
            if (outResp.Content.Headers.ContentLength > maximumBytes)
            { await WriteError(resp, 502, "The media resource exceeded its size limit."); return; }

            string mediaType = outResp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            await using var stream = await outResp.Content.ReadAsStreamAsync(overall.Token);
            byte[] prefix = new byte[16 * 1024];
            bool inspected = RequiresSegmentInspection(mediaType);
            using var prefixDeadline = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            prefixDeadline.CancelAfter(_limits.MediaIdleDeadline);
            int prefixLength = await stream.ReadAtLeastAsync(prefix, inspected ? 1024 : 1,
                throwOnEndOfStream: false, prefixDeadline.Token);
            if (prefixLength == 0)
            { await WriteError(resp, 502, "The CDN returned an empty media resource."); return; }
            if (inspected)
            {
                // Some CDNs label actual TS as images, HTML or JavaScript. Check packet
                // headers before overriding that label, and replay every byte.
                if (!IsSupportedSegmentPayload(mediaType, prefix.AsSpan(0, prefixLength)))
                {
                    await WriteError(resp, 502, "The CDN returned a non-video decoy response for this segment.");
                    return;
                }
            }

            resp.StatusCode = (int)outResp.StatusCode;
            resp.ContentType = inspected ? "video/MP2T"
                : outResp.Content.Headers.ContentType?.ToString() ?? "video/MP2T";

            if (outResp.Content.Headers.ContentLength.HasValue)
                resp.ContentLength64 = outResp.Content.Headers.ContentLength.Value;

            // Content-Range is a content header in HttpClient, not a response header.
            if (outResp.Content.Headers.ContentRange != null)
                resp.Headers["Content-Range"] = outResp.Content.Headers.ContentRange.ToString();
            if (outResp.Headers.AcceptRanges.Count > 0)
                resp.Headers["Accept-Ranges"] = string.Join(",", outResp.Headers.AcceptRanges);

            await CopyMediaBodyAsync(stream, resp.OutputStream, prefix.AsMemory(0, prefixLength),
                maximumBytes, outResp.Content.Headers.ContentLength,
                _limits.MediaIdleDeadline, overall.Token);
            resp.Close();
        }

        private static bool RequiresSegmentInspection(string mediaType) =>
            mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase);

        internal static bool IsSupportedSegmentPayload(string mediaType, ReadOnlySpan<byte> prefix) =>
            !RequiresSegmentInspection(mediaType) || IsMpegTsPayload(prefix);

        internal static bool IsMpegTsPayload(ReadOnlySpan<byte> data)
        {
            if (data.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47 }) ||
                data.StartsWith("GIF8"u8) || data.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
                return false;
            for (int offset = 0; offset < 188 && offset + 188 * 3 + 3 < data.Length; offset++)
            {
                bool valid = true;
                for (int packet = 0; packet < 4; packet++)
                {
                    int start = offset + 188 * packet;
                    if (data[start] != 0x47 || (data[start + 1] & 0x80) != 0 || (data[start + 3] & 0x30) == 0)
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid) return true;
            }
            return false;
        }

        // ── AES Key Handler ──────────────────────────────────────────────────

        private async Task ServeKeyAsync(
            HttpListenerResponse resp,
            ProxySession session,
            string remoteUrl,
            CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(_limits.KeyDeadline);
            using var outResp = await SendSafeAsync(
                remoteUrl,
                session,
                HttpCompletionOption.ResponseHeadersRead, token: deadline.Token);

            if (!outResp.IsSuccessStatusCode)
            { await WriteError(resp, (int)outResp.StatusCode, "The media key is unavailable."); return; }

            byte[] keyBytes;
            try
            {
                keyBytes = await ReadWithLimitAsync(outResp.Content, 16, deadline.Token);
                if (keyBytes.Length != 16) throw new InvalidDataException("The AES-128 key must contain exactly 16 bytes.");
            }
            catch (InvalidDataException ex)
            {
                await WriteError(resp, 502, ex.Message);
                return;
            }
            resp.StatusCode = (int)outResp.StatusCode;
            resp.ContentType = "application/octet-stream";
            resp.ContentLength64 = keyBytes.Length;
            await resp.OutputStream.WriteAsync(keyBytes, deadline.Token);
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
            ValidateManifestShape(m3u8Text);
            var lines = m3u8Text.Split('\n');
            const int maximumRewrittenCharacters = 16 * 1024 * 1024;
            var sb = new StringBuilder(Math.Min(m3u8Text.Length + 4096, maximumRewrittenCharacters));
            bool nextUriIsPlaylist = false;
            int resources = 0;
            long rewrittenUriCharacters = 0;
            void TrackResource(string localUrl)
            {
                if (++resources > 20_000 || (rewrittenUriCharacters += localUrl.Length) > maximumRewrittenCharacters)
                    throw new InvalidDataException("The HLS manifest exceeded its resource/rewrite limit.");
            }

            foreach (var rawLine in lines)
            {
                if (sb.Length + rawLine.Length > maximumRewrittenCharacters)
                    throw new InvalidDataException("The rewritten HLS manifest exceeded its size limit.");
                string line = rawLine.TrimEnd('\r');

                // Tags such as EXT-X-KEY, EXT-X-MAP, EXT-X-MEDIA, and
                // EXT-X-I-FRAME-STREAM-INF can hide CDN URLs inside URI=.
                if (line.StartsWith("#", StringComparison.Ordinal)
                    && line.Contains("URI=", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(RewriteUriTag(line, baseUrl, sessionId, session, TrackResource));
                    nextUriIsPlaylist = false;
                    continue;
                }

                if (IsKeyTag(line) && !string.IsNullOrWhiteSpace(session.KeyUrl))
                {
                    string absoluteKey = ResolveAbsoluteUri(session.KeyUrl!, baseUrl);
                    string localKey = CreateLocalUrl("key", sessionId, absoluteKey);
                    TrackResource(localKey);
                    sb.AppendLine($"{line},URI=\"{localKey}\"");
                    continue;
                }

                // All other # tags and blank lines pass through unchanged
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendLine(line);
                    if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                    {
                        nextUriIsPlaylist = true;
                    }
                    continue;
                }

                // Any non-comment line is a URI (segment, variant playlist, init segment, etc.)
                string absolute = ResolveAbsoluteUri(line, baseUrl);
                bool isPlaylist = nextUriIsPlaylist || LooksLikePlaylistUri(absolute);
                string endpoint = isPlaylist ? "stream" : "seg";
                nextUriIsPlaylist = false;

                string local = CreateLocalUrl(endpoint, sessionId, absolute);
                TrackResource(local);
                sb.AppendLine(local);
            }

            if (sb.Length > maximumRewrittenCharacters)
                throw new InvalidDataException("The rewritten HLS manifest exceeded its size limit.");
            return sb.ToString();
        }

        private static void ValidateManifestShape(string text)
        {
            int lines = 1, length = 0;
            foreach (char character in text)
            {
                if (character == '\n') { if (++lines > 100_000) throw new InvalidDataException("The HLS manifest has too many lines."); length = 0; }
                else if (++length > 64 * 1024) throw new InvalidDataException("The HLS manifest has an excessive line.");
            }
        }

        private string RewriteUriTag(
            string tagLine,
            string baseUrl,
            string sessionId,
            ProxySession session,
            Action<string> trackResource)
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
                    string endpoint = GetUriTagEndpoint(tagLine, absolute, isKeyTag);

                    string localUrl = CreateLocalUrl(endpoint, sessionId, absolute);
                    trackResource(localUrl);
                    return $"URI=\"{localUrl}\"";
                },
                RegexOptions.IgnoreCase);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static bool IsKeyTag(string line) =>
            line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("#EXT-X-SESSION-KEY", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikePlaylistUri(string uri) =>
            uri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || uri.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);

        private static string GetUriTagEndpoint(string tagLine, string absoluteUri, bool isKeyTag)
        {
            if (isKeyTag)
                return "key";
            if (tagLine.StartsWith("#EXT-X-MEDIA", StringComparison.OrdinalIgnoreCase)
                || tagLine.StartsWith("#EXT-X-I-FRAME-STREAM-INF", StringComparison.OrdinalIgnoreCase)
                || tagLine.StartsWith("#EXT-X-RENDITION-REPORT", StringComparison.OrdinalIgnoreCase))
                return "stream";
            return LooksLikePlaylistUri(absoluteUri) ? "stream" : "seg";
        }

        private async Task<HttpResponseMessage> SendSafeAsync(
            string url,
            ProxySession session,
            HttpCompletionOption completionOption,
            string? rangeHeader = null,
            CancellationToken token = default)
        {
            string currentUrl = url;
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                if (!await IsPublicRemoteUriAsync(currentUrl, token))
                {
                    throw new HttpRequestException("The remote media URL resolved to a local or non-public address.");
                }

                var currentUri = new Uri(currentUrl, UriKind.Absolute);
                bool replayCapturedHeaders = ShouldReplayCapturedHeaders(session, currentUri);
                using var request = BuildRequest(
                    HttpMethod.Get,
                    currentUrl,
                    session,
                    replayCapturedHeaders);
                if (!string.IsNullOrWhiteSpace(rangeHeader))
                {
                    request.Headers.TryAddWithoutValidation("Range", rangeHeader);
                }

                var response = await _remoteHttp.SendAsync(request, completionOption, token);
                if (!IsRedirect(response.StatusCode))
                {
                    return response;
                }

                Uri? location = response.Headers.Location;
                if (location == null)
                {
                    return response;
                }

                string nextUrl = location.IsAbsoluteUri
                    ? location.AbsoluteUri
                    : new Uri(new Uri(currentUrl), location).AbsoluteUri;
                response.Dispose();
                currentUrl = nextUrl;
            }

            throw new HttpRequestException("The remote media URL exceeded the redirect limit.");
        }

        private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

        private static async Task<byte[]> ReadWithLimitAsync(
            HttpContent content,
            int maximumBytes,
            CancellationToken token = default)
        {
            if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maximumBytes)
            {
                throw new InvalidDataException($"The CDN response exceeded its {maximumBytes}-byte limit.");
            }

            await using var input = await content.ReadAsStreamAsync(token);
            using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
            byte[] buffer = new byte[16 * 1024];
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0)
                {
                    break;
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidDataException($"The CDN response exceeded its {maximumBytes}-byte limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }

            if (content.Headers.ContentLength is { } expected && output.Length != expected)
                throw new EndOfStreamException("The CDN response ended before its declared length.");
            return output.ToArray();
        }

        private static async Task<bool> IsPublicRemoteUriAsync(string value, CancellationToken token)
        {
            if (!IsAllowedRemoteUri(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (IPAddress.TryParse(uri.Host, out var literal))
            {
                return IsPublicAddress(literal);
            }

            string host = uri.DnsSafeHost.ToLowerInvariant();
            if (DnsSafetyCache.TryGetValue(host, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            {
                return cached.IsPublic;
            }

            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, token);
                bool isPublic = addresses.Length > 0 && addresses.All(IsPublicAddress);
                DnsSafetyCache[host] = (
                    isPublic,
                    DateTime.UtcNow.Add(isPublic ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30)));
                return isPublic;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                DnsSafetyCache[host] = (false, DateTime.UtcNow.AddSeconds(30));
                return false;
            }
        }

        internal static bool IsAllowedRemoteUri(string value)
        {
            if (value.Length > 16 * 1024 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            string host = uri.DnsSafeHost.TrimEnd('.');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !IPAddress.TryParse(host, out var address) || IsPublicAddress(address);
        }

        private static bool IsPublicAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address) ||
                address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.IPv6Any) ||
                address.Equals(IPAddress.None) ||
                address.Equals(IPAddress.IPv6None))
            {
                return false;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte first = bytes[0];
                byte second = bytes[1];
                return first is not 0 and not 10 and not 127 &&
                       !(first == 100 && second is >= 64 and <= 127) &&
                       !(first == 169 && second == 254) &&
                       !(first == 172 && second is >= 16 and <= 31) &&
                       !(first == 192 && second == 168) &&
                       !(first == 198 && second is 18 or 19) &&
                       first < 224;
            }

            return !address.IsIPv6LinkLocal &&
                   !address.IsIPv6Multicast &&
                   !address.IsIPv6SiteLocal &&
                   (bytes[0] & 0xFE) != 0xFC &&
                   !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
        }

        internal static HttpRequestMessage BuildRequest(
            HttpMethod method,
            string url,
            ProxySession session,
            bool replayCapturedHeaders = true)
        {
            var req = new HttpRequestMessage(method, url);

            if (replayCapturedHeaders && session.RequestHeaders != null)
            {
                foreach (var header in session.RequestHeaders)
                {
                    if (ShouldSkipReplayHeader(header.Key) || string.IsNullOrWhiteSpace(header.Value))
                        continue;

                    req.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (!HasHeader(req, "User-Agent") && !string.IsNullOrEmpty(session.UserAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", session.UserAgent);
            else if (!HasHeader(req, "User-Agent"))
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (replayCapturedHeaders &&
                !HasHeader(req, "Cookie") &&
                !string.IsNullOrEmpty(session.Cookie))
                req.Headers.TryAddWithoutValidation("Cookie", session.Cookie);

            if (replayCapturedHeaders && !string.IsNullOrEmpty(session.Referer))
            {
                if (!HasHeader(req, "Referer"))
                    req.Headers.TryAddWithoutValidation("Referer", session.Referer);
                if (Uri.TryCreate(session.Referer, UriKind.Absolute, out var refererUri))
                {
                    if (!HasHeader(req, "Origin"))
                        req.Headers.TryAddWithoutValidation("Origin", $"{refererUri.Scheme}://{refererUri.Authority}");
                }
            }

            if (!replayCapturedHeaders)
            {
                // Browsers retain public page context across CDN requests, but
                // credentials and URL query/fragment tokens stay on their host.
                string? referer = session.Referer;
                string? origin = null;
                if (session.RequestHeaders != null)
                    foreach (var header in session.RequestHeaders)
                    {
                        if (header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) referer = header.Value;
                        if (header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = header.Value;
                    }
                string publicReferer = GetPublicOrigin(referer);
                string publicOrigin = GetPublicOrigin(origin ?? referer);
                if (publicReferer.Length > 0) req.Headers.Referrer = new Uri(publicReferer + "/");
                if (publicOrigin.Length > 0) req.Headers.TryAddWithoutValidation("Origin", publicOrigin);
            }

            if (!HasHeader(req, "Accept"))
                req.Headers.Accept.ParseAdd("*/*");
            req.Headers.Remove("Accept-Encoding");
            req.Headers.AcceptEncoding.ParseAdd("identity"); // prevent gzip on TS chunks
            return req;
        }

        private static string GetPublicOrigin(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
            string.IsNullOrEmpty(uri.UserInfo) ? uri.GetLeftPart(UriPartial.Authority) : string.Empty;

        internal static bool IsSameOrigin(Uri first, Uri second) =>
            first.Scheme.Equals(second.Scheme, StringComparison.OrdinalIgnoreCase) &&
            first.DnsSafeHost.Equals(second.DnsSafeHost, StringComparison.OrdinalIgnoreCase) &&
            first.Port == second.Port;

        internal static bool ShouldReplayCapturedHeaders(ProxySession session, Uri targetUri) =>
            Uri.TryCreate(session.RemoteM3U8Url, UriKind.Absolute, out Uri? sessionRoot) &&
            IsSameOrigin(sessionRoot, targetUri);

        private static bool HasHeader(HttpRequestMessage req, string name) =>
            req.Headers.Contains(name) || (req.Content?.Headers.Contains(name) ?? false);

        private static bool ShouldSkipReplayHeader(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;

            return name.StartsWith(":", StringComparison.Ordinal)
                || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Range", StringComparison.OrdinalIgnoreCase);
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
            lock (_lifecycleLock)
            {
                if (_disposed) return;
                _disposed = true;
                _gcTimer.Dispose();
                StopCore();
            }
        }
    }
}
