using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Configuration
{
    public class CustomSource
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    public class SettingChangedEventArgs : EventArgs
    {
        public string Key { get; }
        public string Value { get; }

        public SettingChangedEventArgs(string key, string value)
        {
            Key = key;
            Value = value;
        }
    }

    public class DomainHotSwapper
    {
        private readonly string _configPath;
        private ConcurrentDictionary<string, string> _domainMap = new ConcurrentDictionary<string, string>();

        public event EventHandler<SettingChangedEventArgs>? SettingChanged;

        public DomainHotSwapper(string configPath)
        {
            _configPath = configPath;
            LoadConfig();
        }

        protected virtual void OnSettingChanged(string key, string value)
        {
            SettingChanged?.Invoke(this, new SettingChangedEventArgs(key, value));
        }

        public void LoadConfig()
        {
            if (!File.Exists(_configPath))
            {
                _domainMap = new ConcurrentDictionary<string, string>(BuildDefaults());
                SaveConfig();
            }
            else
            {
                try
                {
                    string json = File.ReadAllText(_configPath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                    _domainMap = new ConcurrentDictionary<string, string>(dict);

                    var defaults = BuildDefaults();
                    foreach (var kvp in defaults)
                    {
                        _domainMap.TryAdd(kvp.Key, kvp.Value);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Failed to load config synchronously: {ex.Message}", "ERROR");
                    System.Diagnostics.Debug.WriteLine($"Failed to load config: {ex.Message}");
                    _domainMap = new ConcurrentDictionary<string, string>(BuildDefaults());
                }
            }
        }

        public async Task LoadConfigAsync()
        {
            if (!File.Exists(_configPath))
            {
                _domainMap = new ConcurrentDictionary<string, string>(BuildDefaults());
                await SaveConfigAsync();
            }
            else
            {
                try
                {
                    string json = await File.ReadAllTextAsync(_configPath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                    _domainMap = new ConcurrentDictionary<string, string>(dict);

                    var defaults = BuildDefaults();
                    foreach (var kvp in defaults)
                    {
                        _domainMap.TryAdd(kvp.Key, kvp.Value);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Failed to load config asynchronously: {ex.Message}", "ERROR");
                    _domainMap = new ConcurrentDictionary<string, string>(BuildDefaults());
                }
            }
        }

        public bool SaveConfig()
        {
            try
            {
                string json = JsonSerializer.Serialize(_domainMap, new JsonSerializerOptions { WriteIndented = true });
                SaveConfigAtomic(json);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to save config synchronously: {ex.Message}", "ERROR");
                System.Diagnostics.Debug.WriteLine($"Failed to save config: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SaveConfigAsync()
        {
            try
            {
                string json = JsonSerializer.Serialize(_domainMap, new JsonSerializerOptions { WriteIndented = true });
                await SaveConfigAtomicAsync(json);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to save config asynchronously: {ex.Message}", "ERROR");
                return false;
            }
        }

        private void SaveConfigAtomic(string json)
        {
            string tempPath = _configPath + ".tmp";
            string? dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _configPath, overwrite: true);
        }

        private async Task SaveConfigAtomicAsync(string json)
        {
            string tempPath = _configPath + ".tmp";
            string? dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _configPath, overwrite: true);
        }

        public string GetSetting(string key)
        {
            if (_domainMap.TryGetValue(key, out var val))
            {
                if (key == "QBitPassword" || key == "MalOAuthToken")
                {
                    if (string.IsNullOrEmpty(val) || val == "adminadmin") return val;
                    try
                    {
                        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                        {
                            var decrypted = ProtectedData.Unprotect(Convert.FromBase64String(val), null, DataProtectionScope.CurrentUser);
                            return Encoding.UTF8.GetString(decrypted);
                        }
                        else
                        {
                            return val; // Fallback plain text on non-Windows
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"Decryption failed for key '{key}': {ex.Message}", "ERROR");
                        return string.Empty;
                    }
                }
                return val;
            }
            return string.Empty;
        }

        public void SetSetting(string key, string value)
        {
            string storedValue = value;
            if (key == "QBitPassword" || key == "MalOAuthToken")
            {
                if (!string.IsNullOrEmpty(value) && value != "adminadmin")
                {
                    try
                    {
                        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                        {
                            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                            storedValue = Convert.ToBase64String(encrypted);
                        }
                        else
                        {
                            AppLogger.Log("DPAPI is not supported on this platform. Saving credentials unencrypted.", "WARNING");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"Encryption failed for key '{key}': {ex.Message}. Credentials not updated.", "ERROR");
                        throw;
                    }
                }
            }
            
            _domainMap[key] = storedValue;
            SaveConfig();
            OnSettingChanged(key, value);
        }

        public async Task SetSettingAsync(string key, string value)
        {
            string storedValue = value;
            if (key == "QBitPassword" || key == "MalOAuthToken")
            {
                if (!string.IsNullOrEmpty(value) && value != "adminadmin")
                {
                    try
                    {
                        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                        {
                            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                            storedValue = Convert.ToBase64String(encrypted);
                        }
                        else
                        {
                            AppLogger.Log("DPAPI is not supported on this platform. Saving credentials unencrypted.", "WARNING");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log($"Encryption failed for key '{key}': {ex.Message}. Credentials not updated.", "ERROR");
                        throw;
                    }
                }
            }
            
            _domainMap[key] = storedValue;
            await SaveConfigAsync();
            OnSettingChanged(key, value);
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
            SetSetting( "CustomSources", serialized);
        }

        public async Task SaveCustomSourcesAsync(List<CustomSource> sources)
        {
            string serialized = JsonSerializer.Serialize(sources);
            await SetSettingAsync("CustomSources", serialized);
        }

        private static Dictionary<string, string> BuildDefaults()
        {
            var defaults = new Dictionary<string, string>
            {
                { "PythonScraperBase", "http://localhost:8000" },
                
                // Serialized dynamic CustomSources
                { "CustomSources", "[{\"Name\":\"GogoAnime\",\"Url\":\"https://gogoanime3.co/search.html?keyword={query}\"},{\"Name\":\"AnimePahe\",\"Url\":\"https://animepahe.ru/anime/{query}\"},{\"Name\":\"Zoro\",\"Url\":\"https://hianime.to/search?keyword={query}\"}]" },

                // qBittorrent settings
                { "QBitHost", "localhost" },
                { "QBitPort", "8080" },
                { "QBitUsername", "admin" },
                { "QBitPassword", "adminadmin" },

                // MAL integration
                { "MalOAuthToken", "" },

                // Playback / UI preferences
                { "DefaultAudioPref", "Sub" },
                { "AutoPlayAfterDownload", "true" },
                { "AutoManageServices", "false" },
                { "DownloadDirectory", "" },

                // Redirectable API URLs
                { "AniListUrl", "https://graphql.anilist.co" },
                { "AniSkipUrl", "https://api.aniskip.com" },
                { "MalApiUrl", "https://api.myanimelist.net" },
                { "NyaaUrl", "https://nyaa.si/?page=rss&c=1_2&f=0&q=" },
                { "AnimeToshoUrl", "https://feed.animetosho.org/rss2?q=" },
                { "MangaDexUrl", "https://api.mangadex.org" },
                { "MangaDexCoversUrl", "https://uploads.mangadex.org" },
                { "DatabasePath", "" },
                { "TmdbApiKey", "" },
                { "JikanApiUrl", "https://api.jikan.moe/v4" }
            };
            return defaults;
        }
    }
}
