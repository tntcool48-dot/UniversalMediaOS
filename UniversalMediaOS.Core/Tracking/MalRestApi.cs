using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.Tracking
{
    public class MalRestApi
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private readonly string _accessToken;
        private readonly string _malApiUrl;

        public MalRestApi(string accessToken, DomainHotSwapper? config = null)
        {
            _accessToken = accessToken ?? string.Empty;
            _malApiUrl = config?.GetSetting("MalApiUrl") ?? "https://api.myanimelist.net";
        }

        public async Task<bool> UpdateProgressAsync(int animeId, int numWatchedEpisodes, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(_accessToken))
            {
                System.Diagnostics.Debug.WriteLine("MAL Update failed: Access token is null or empty.");
                return false;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, $"{_malApiUrl.TrimEnd('/')}/v2/anime/{animeId}/my_list_status");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

                using var content = new StringContent($"status=watching&num_watched_episodes={numWatchedEpisodes}", Encoding.UTF8, "application/x-www-form-urlencoded");
                request.Content = content;

                using var response = await _httpClient.SendAsync(request, token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MAL Update failed: {ex.Message}");
                return false;
            }
        }
    }
}
