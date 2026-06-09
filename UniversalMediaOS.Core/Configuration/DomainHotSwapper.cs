using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;

namespace UniversalMediaOS.Core.Configuration
{
    public class CustomSource
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    public class DomainHotSwapper
    {
        private readonly string _configPath;
        private Dictionary<string, string> _domainMap = new Dictionary<string, string>();

        public DomainHotSwapper(string configPath)
        {
            _configPath = configPath;
            LoadConfig();
        }

        public void LoadConfig()
        {
            if (!File.Exists(_configPath))
            {
                _domainMap = BuildDefaults();
                SaveConfig();
            }
            else
            {
                try
                {
                    string json = File.ReadAllText(_configPath);
                    _domainMap = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

                    var defaults = BuildDefaults();
                    foreach (var kvp in defaults)
                    {
                        if (!_domainMap.ContainsKey(kvp.Key))
                        {
                            _domainMap[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load config: {ex.Message}");
                    _domainMap = BuildDefaults();
                }
            }
        }

        public void SaveConfig()
        {
            try
            {
                string json = JsonSerializer.Serialize(_domainMap, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save config: {ex.Message}");
            }
        }

        public string GetDomain(string key)
        {
            return _domainMap.TryGetValue(key, out var val) ? val : string.Empty;
        }

        public void UpdateDomain(string providerName, string newDomain)
        {
            if (_domainMap.ContainsKey(providerName))
            {
                _domainMap[providerName] = newDomain;
            }
            else
            {
                _domainMap.Add(providerName, newDomain);
            }
            SaveConfig();
        }

        /// <summary>
        /// Returns provider domain entries based on dynamic CustomSources list.
        /// Keeps backward compatibility for existing callers.
        /// </summary>
        public Dictionary<string, string> GetAllDomains()
        {
            var dict = new Dictionary<string, string>();
            dict["ConsumetApiBase"] = GetSetting("ConsumetApiBase");
            dict["PythonScraperBase"] = GetSetting("PythonScraperBase");

            var sources = GetCustomSources();
            foreach (var src in sources)
            {
                dict[src.Name + "Base"] = src.Url;
            }

            return dict;
        }

        public string GetSetting(string key)
        {
            return _domainMap.TryGetValue(key, out var val) ? val : string.Empty;
        }

        public void SetSetting(string key, string value)
        {
            _domainMap[key] = value;
            SaveConfig();
        }

        /// <summary>
        /// Gets the dynamic, deserealized list of CustomSources.
        /// </summary>
        public List<CustomSource> GetCustomSources()
        {
            string raw = GetSetting("CustomSources");
            if (string.IsNullOrEmpty(raw))
            {
                return new List<CustomSource>();
            }
            try
            {
                return JsonSerializer.Deserialize<List<CustomSource>>(raw) ?? new List<CustomSource>();
            }
            catch
            {
                return new List<CustomSource>();
            }
        }

        /// <summary>
        /// Saves a dynamic list of CustomSources back to config.json.
        /// </summary>
        public void SaveCustomSources(List<CustomSource> sources)
        {
            string serialized = JsonSerializer.Serialize(sources);
            SetSetting("CustomSources", serialized);
        }

        private static Dictionary<string, string> BuildDefaults()
        {
            var defaults = new Dictionary<string, string>
            {
                { "ConsumetApiBase", "http://localhost:3000" },
                { "PythonScraperBase", "http://localhost:8000" },
                
                // Serialized dynamic CustomSources
                { "CustomSources", "[{\"Name\":\"GogoAnime\",\"Url\":\"https://gogoanime3.co/search.html?keyword={query}\"},{\"Name\":\"AnimePahe\",\"Url\":\"https://animepahe.ru/anime/{query}\"},{\"Name\":\"Zoro\",\"Url\":\"https://hianime.to/search?keyword={query}\"}]" },

                // qBittorrent settings
                { "QBitPort", "8080" },
                { "QBitUsername", "admin" },
                { "QBitPassword", "adminadmin" },
                { "QBitPath", "" },

                // MAL integration
                { "MalOAuthToken", "" },

                // Playback / UI preferences
                { "DefaultAudioPref", "Sub" },
                { "AutoPlayAfterDownload", "true" },
                { "DownloadDirectory", "" }
            };
            return defaults;
        }
    }
}
