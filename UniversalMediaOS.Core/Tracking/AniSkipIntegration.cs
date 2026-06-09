using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Tracking
{
    public class AniSkipIntegration
    {
        public async Task<SkipTimes?> GetSkipTimesAsync(int malId, int episodeNumber)
        {
            try
            {
                using var client = new HttpClient();
                var response = await client.GetAsync($"https://api.aniskip.com/v2/skip-times/{malId}/{episodeNumber}?types[]=op&types[]=ed&episodeLength=0");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.GetProperty("found").GetBoolean())
                    {
                        var results = doc.RootElement.GetProperty("results");
                        var skipTimes = new SkipTimes();
                        
                        foreach (var item in results.EnumerateArray())
                        {
                            string type = item.GetProperty("skipType").GetString() ?? "";
                            double start = item.GetProperty("interval").GetProperty("startTime").GetDouble();
                            double end = item.GetProperty("interval").GetProperty("endTime").GetDouble();
                            
                            if (type == "op") skipTimes.Intro = new SkipInterval { Start = start, End = end };
                            if (type == "ed") skipTimes.Outro = new SkipInterval { Start = start, End = end };
                        }
                        return skipTimes;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AniSkip failed: {ex.Message}");
            }
            return null;
        }
    }

    public class SkipTimes
    {
        public SkipInterval? Intro { get; set; }
        public SkipInterval? Outro { get; set; }
    }

    public class SkipInterval
    {
        public double Start { get; set; }
        public double End { get; set; }
    }
}
