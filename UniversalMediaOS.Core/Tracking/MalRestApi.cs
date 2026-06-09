using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Tracking
{
    public class MalRestApi
    {
        private readonly string _accessToken;

        public MalRestApi(string accessToken)
        {
            _accessToken = accessToken;
        }

        public async Task<bool> UpdateProgressAsync(int animeId, int numWatchedEpisodes)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

                var content = new StringContent($"num_watched_episodes={numWatchedEpisodes}", Encoding.UTF8, "application/x-www-form-urlencoded");
                var response = await client.PutAsync($"https://api.myanimelist.net/v2/anime/{animeId}/my_list_status", content);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MAL Update failed: {ex.Message}");
                return false;
            }
        }
    }
}
