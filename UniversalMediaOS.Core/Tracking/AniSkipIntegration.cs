using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.Tracking
{
    public class AniSkipIntegration
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly string _aniSkipUrl;

        public AniSkipIntegration(DomainHotSwapper? config = null)
        {
            _aniSkipUrl = config?.GetSetting("AniSkipUrl") ?? "https://api.aniskip.com";
        }

        public async Task<SkipTimes?> GetSkipTimesAsync(int malId, int episodeNumber, CancellationToken token = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{_aniSkipUrl.TrimEnd('/')}/v2/skip-times/{malId}/{episodeNumber}?types[]=op&types[]=ed&episodeLength=0", token);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(token);
                    using var doc = JsonDocument.Parse(content);
                    
                    if (doc.RootElement.TryGetProperty("found", out var foundProp) && foundProp.ValueKind == JsonValueKind.True)
                    {
                        if (doc.RootElement.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
                        {
                            var skipTimes = new SkipTimes();
                            
                            foreach (var item in resultsProp.EnumerateArray())
                            {
                                if (item.TryGetProperty("skipType", out var typeProp) && 
                                    item.TryGetProperty("interval", out var intervalProp))
                                {
                                    string type = typeProp.GetString() ?? "";
                                    double start = 0;
                                    double end = 0;
                                    
                                    if (intervalProp.TryGetProperty("startTime", out var startProp))
                                    {
                                        start = startProp.GetDouble();
                                    }
                                    if (intervalProp.TryGetProperty("endTime", out var endProp))
                                    {
                                        end = endProp.GetDouble();
                                    }
                                    
                                    if (type == "op") skipTimes.Intro = new SkipInterval { Start = start, End = end };
                                    if (type == "ed") skipTimes.Outro = new SkipInterval { Start = start, End = end };
                                }
                            }
                            return skipTimes;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AniSkip failed: {ex.Message}");
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
