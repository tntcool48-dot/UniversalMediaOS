using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Routing
{
    public class NetworkTopologyTester
    {
        private readonly QBitLogicGate _qbit;

        public NetworkTopologyTester(QBitLogicGate qbit)
        {
            _qbit = qbit ?? throw new ArgumentNullException(nameof(qbit));
        }

        public async Task<bool> IsSwarmAliveAsync(string infoHash, int waitSeconds = 5)
        {
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds));

            if (string.IsNullOrEmpty(_qbit.Cookie))
                return false;

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Cookie", _qbit.Cookie);
                client.Timeout = TimeSpan.FromSeconds(10);

                var response = await client.GetAsync($"http://localhost:8080/api/v2/torrents/info?hashes={infoHash}");
                if (!response.IsSuccessStatusCode) return false;

                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);

                if (doc.RootElement.GetArrayLength() > 0)
                {
                    var torrent = doc.RootElement[0];
                    int numSeeds = torrent.GetProperty("num_seeds").GetInt32();
                    long dlSpeed = torrent.GetProperty("dlspeed").GetInt64();

                    // If connected seeds > 0 or downloading > 0 bytes/s, swarm is alive
                    if (numSeeds > 0 || dlSpeed > 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Topology test failed: {ex.Message}");
            }

            return false; // Dead swarm or NAT blocked
        }
    }
}
