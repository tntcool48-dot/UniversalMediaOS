using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public sealed class MalOAuthConnectResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
    }

    public sealed class MalOAuthService : IDisposable
    {
        private const string AuthorizeUrl = "https://myanimelist.net/v1/oauth2/authorize";
        private const string TokenUrl = "https://myanimelist.net/v1/oauth2/token";
        private const int DefaultRedirectPort = 8765;
        private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

        private readonly DomainHotSwapper _config;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        public MalOAuthService(DomainHotSwapper config)
            : this(config, new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
        }

        internal MalOAuthService(DomainHotSwapper config, HttpClient httpClient)
        {
            _config = config;
            _httpClient = httpClient;
        }

        public string ClientId => _config.GetSetting("MalClientId").Trim();
        public string ClientSecret => _config.GetSetting("MalClientSecret").Trim();
        public string Username => _config.GetSetting("MalOAuthUsername").Trim();
        public bool HasRefreshToken => !string.IsNullOrWhiteSpace(_config.GetSetting("MalOAuthRefreshToken"));
        public bool HasAnyToken => HasRefreshToken || !string.IsNullOrWhiteSpace(_config.GetSetting("MalOAuthToken"));
        public string RedirectUri => BuildRedirectUri(ResolveRedirectPort());

        public async Task<MalOAuthConnectResult> ConnectAsync(Func<Uri, bool> openBrowser, CancellationToken token = default)
        {
            if (openBrowser == null)
            {
                throw new ArgumentNullException(nameof(openBrowser));
            }

            string clientId = ClientId;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return new MalOAuthConnectResult
                {
                    Success = false,
                    Message = "Add a MyAnimeList Client ID before connecting."
                };
            }

            string redirectUri = RedirectUri;
            string verifier = CreateCodeVerifier();
            string state = CreateCodeVerifier();
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);

            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"MAL OAuth callback listener failed: {ex.Message}", "WARNING");
                return new MalOAuthConnectResult
                {
                    Success = false,
                    Message = $"Could not start local MAL callback listener on {redirectUri}. Is the port already in use?"
                };
            }

            Uri authorizeUri = BuildAuthorizeUri(clientId, redirectUri, verifier, state);
            if (!openBrowser(authorizeUri))
            {
                return new MalOAuthConnectResult
                {
                    Success = false,
                    Message = "Could not open the MAL authorization page."
                };
            }

            using var registration = token.Register(() =>
            {
                try { listener.Stop(); } catch { }
            });

            try
            {
                HttpListenerContext context = await listener.GetContextAsync().WaitAsync(token);
                string returnedState = context.Request.QueryString["state"] ?? string.Empty;
                string code = context.Request.QueryString["code"] ?? string.Empty;
                string error = context.Request.QueryString["error"] ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(error))
                {
                    await WriteBrowserResponseAsync(context.Response, "MyAnimeList connection was cancelled or denied.");
                    return new MalOAuthConnectResult
                    {
                        Success = false,
                        Message = $"MAL authorization failed: {error}."
                    };
                }

                if (!returnedState.Equals(state, StringComparison.Ordinal))
                {
                    await WriteBrowserResponseAsync(context.Response, "MyAnimeList connection failed because the state token did not match.");
                    return new MalOAuthConnectResult
                    {
                        Success = false,
                        Message = "MAL authorization failed state validation."
                    };
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    await WriteBrowserResponseAsync(context.Response, "MyAnimeList did not return an authorization code.");
                    return new MalOAuthConnectResult
                    {
                        Success = false,
                        Message = "MAL did not return an authorization code."
                    };
                }

                var tokenResponse = await ExchangeAuthorizationCodeAsync(code, verifier, redirectUri, token);
                SaveTokenResponse(tokenResponse);
                string username = await RefreshConnectedUserAsync(token);
                await WriteBrowserResponseAsync(context.Response, "MyAnimeList connected. You can close this tab.");

                return new MalOAuthConnectResult
                {
                    Success = true,
                    Message = string.IsNullOrWhiteSpace(username)
                        ? "MyAnimeList connected."
                        : $"Connected to MyAnimeList as {username}.",
                    Username = username
                };
            }
            catch (OperationCanceledException)
            {
                return new MalOAuthConnectResult
                {
                    Success = false,
                    Message = "MAL connection was cancelled."
                };
            }
            catch (Exception ex)
            {
                AppLogger.Log($"MAL OAuth connect failed: {ex.Message}", "WARNING");
                return new MalOAuthConnectResult
                {
                    Success = false,
                    Message = $"MAL connection failed: {ex.Message}"
                };
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        public async Task<string> GetValidAccessTokenAsync(bool forceRefresh = false, CancellationToken token = default)
        {
            string accessToken = _config.GetSetting("MalOAuthToken");
            string refreshToken = _config.GetSetting("MalOAuthRefreshToken");
            if (!forceRefresh &&
                !string.IsNullOrWhiteSpace(accessToken) &&
                !IsAccessTokenNearExpiry())
            {
                return accessToken;
            }

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return accessToken;
            }

            await _refreshLock.WaitAsync(token);
            try
            {
                accessToken = _config.GetSetting("MalOAuthToken");
                if (!forceRefresh &&
                    !string.IsNullOrWhiteSpace(accessToken) &&
                    !IsAccessTokenNearExpiry())
                {
                    return accessToken;
                }

                var tokenResponse = await RefreshTokenAsync(refreshToken, token);
                SaveTokenResponse(tokenResponse);
                return tokenResponse.AccessToken;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        public async Task<string> RefreshConnectedUserAsync(CancellationToken token = default)
        {
            string accessToken = await GetValidAccessTokenAsync(token: token);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return string.Empty;
            }

            try
            {
                string apiRoot = _config.GetSetting("MalApiUrl");
                if (string.IsNullOrWhiteSpace(apiRoot))
                {
                    apiRoot = "https://api.myanimelist.net";
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiRoot.TrimEnd('/')}/v2/users/@me");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await _httpClient.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    return Username;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                string username = TryGetString(doc.RootElement, "name");
                if (!string.IsNullOrWhiteSpace(username))
                {
                    _config.SetSetting("MalOAuthUsername", username);
                }

                return username;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"MAL username refresh failed: {ex.Message}", "WARNING");
                return Username;
            }
        }

        public void Disconnect()
        {
            _config.SetSetting("MalOAuthToken", string.Empty);
            _config.SetSetting("MalOAuthRefreshToken", string.Empty);
            _config.SetSetting("MalOAuthExpiresAtUtc", string.Empty);
            _config.SetSetting("MalOAuthUsername", string.Empty);
        }

        private async Task<MalTokenResponse> ExchangeAuthorizationCodeAsync(
            string code,
            string codeVerifier,
            string redirectUri,
            CancellationToken token)
        {
            var form = BuildClientCredentialsForm();
            form.Add(new KeyValuePair<string, string>("grant_type", "authorization_code"));
            form.Add(new KeyValuePair<string, string>("code", code));
            form.Add(new KeyValuePair<string, string>("redirect_uri", redirectUri));
            form.Add(new KeyValuePair<string, string>("code_verifier", codeVerifier));

            return await SendTokenRequestAsync(form, token);
        }

        private async Task<MalTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken token)
        {
            var form = BuildClientCredentialsForm();
            form.Add(new KeyValuePair<string, string>("grant_type", "refresh_token"));
            form.Add(new KeyValuePair<string, string>("refresh_token", refreshToken));
            return await SendTokenRequestAsync(form, token);
        }

        private async Task<MalTokenResponse> SendTokenRequestAsync(
            IReadOnlyList<KeyValuePair<string, string>> form,
            CancellationToken token)
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _httpClient.PostAsync(TokenUrl, content, token);
            string payload = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"MAL token request failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var tokenResponse = JsonSerializer.Deserialize<MalTokenResponse>(payload);
            if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                throw new InvalidOperationException("MAL token response did not include an access token.");
            }

            return tokenResponse;
        }

        private List<KeyValuePair<string, string>> BuildClientCredentialsForm()
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("client_id", ClientId)
            };

            string secret = ClientSecret;
            if (!string.IsNullOrWhiteSpace(secret))
            {
                form.Add(new KeyValuePair<string, string>("client_secret", secret));
            }

            return form;
        }

        private void SaveTokenResponse(MalTokenResponse tokenResponse)
        {
            _config.SetSetting("MalOAuthToken", tokenResponse.AccessToken);
            if (!string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
            {
                _config.SetSetting("MalOAuthRefreshToken", tokenResponse.RefreshToken);
            }

            int expiresIn = tokenResponse.ExpiresIn <= 0 ? 0 : tokenResponse.ExpiresIn;
            if (expiresIn > 0)
            {
                DateTime expiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn);
                _config.SetSetting("MalOAuthExpiresAtUtc", expiresAtUtc.ToString("O", CultureInfo.InvariantCulture));
            }
        }

        private bool IsAccessTokenNearExpiry()
        {
            string raw = _config.GetSetting("MalOAuthExpiresAtUtc");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            return !DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var expiresAtUtc) ||
                   expiresAtUtc <= DateTime.UtcNow.Add(RefreshSkew);
        }

        private int ResolveRedirectPort()
        {
            return int.TryParse(_config.GetSetting("MalOAuthRedirectPort"), out int port)
                ? Math.Clamp(port, 1, 65535)
                : DefaultRedirectPort;
        }

        private static string BuildRedirectUri(int port)
        {
            return $"http://127.0.0.1:{port}/mal/callback/";
        }

        private static Uri BuildAuthorizeUri(string clientId, string redirectUri, string verifier, string state)
        {
            string query =
                "response_type=code" +
                $"&client_id={Uri.EscapeDataString(clientId)}" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&code_challenge={Uri.EscapeDataString(verifier)}" +
                "&code_challenge_method=plain";
            return new Uri($"{AuthorizeUrl}?{query}");
        }

        private static string CreateCodeVerifier()
        {
            Span<byte> bytes = stackalloc byte[64];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static async Task WriteBrowserResponseAsync(HttpListenerResponse response, string message)
        {
            string html = $"""
                <!doctype html>
                <html>
                <head><meta charset="utf-8"><title>UniversalMediaOS</title></head>
                <body style="font-family:Segoe UI,Arial,sans-serif;background:#0f172a;color:#e2e8f0;padding:40px">
                  <h1>UniversalMediaOS</h1>
                  <p>{WebUtility.HtmlEncode(message)}</p>
                </body>
                </html>
                """;
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private static string TryGetString(JsonElement element, string propertyName)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        public void Dispose()
        {
            _refreshLock.Dispose();
            _httpClient.Dispose();
        }

        private sealed class MalTokenResponse
        {
            [JsonPropertyName("token_type")]
            public string TokenType { get; set; } = string.Empty;

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }

            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = string.Empty;

            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; } = string.Empty;
        }
    }
}
