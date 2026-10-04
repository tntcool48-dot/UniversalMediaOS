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

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name) ? Url : Name;
        }
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
        private const string DpapiProtectedValuePrefix = "dpapi:v1:";

        private readonly string _configPath;
        private ConcurrentDictionary<string, string> _domainMap = new ConcurrentDictionary<string, string>();
        private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

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

        private static bool IsProtectedSetting(string key)
        {
            return key is "QBitPassword" or
                "MalOAuthToken" or
                "MalOAuthRefreshToken" or
                "MalClientSecret" or
                "TmdbApiKey" or
                "GoogleBooksApiKey";
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

                    if (MigrateLegacyProtectedSettings())
                    {
                        SaveConfig();
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

                    if (MigrateLegacyProtectedSettings())
                    {
                        await SaveConfigAsync();
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
            _saveLock.Wait();
            try
            {
                return SaveConfigCore();
            }
            finally
            {
                _saveLock.Release();
            }
        }

        public async Task<bool> SaveConfigAsync()
        {
            await _saveLock.WaitAsync();
            try
            {
                return await SaveConfigCoreAsync();
            }
            finally
            {
                _saveLock.Release();
            }
        }

        private bool SaveConfigCore()
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

        private async Task<bool> SaveConfigCoreAsync()
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
                if (IsProtectedSetting(key))
                {
                    if (string.IsNullOrEmpty(val) || val == "adminadmin") return val;

                    if (val.StartsWith(DpapiProtectedValuePrefix, StringComparison.Ordinal))
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            AppLogger.Log($"DPAPI-protected setting '{key}' cannot be decrypted on this platform.", "ERROR");
                            return string.Empty;
                        }

                        if (TryUnprotectDpapiValue(val[DpapiProtectedValuePrefix.Length..], out string decrypted))
                        {
                            return decrypted;
                        }

                        AppLogger.Log($"Decryption failed for key '{key}'.", "ERROR");
                        return string.Empty;
                    }

                    // Values written before the marker was introduced may be
                    // either raw DPAPI Base64 or plaintext. Preserve both forms.
                    if (OperatingSystem.IsWindows() && TryUnprotectDpapiValue(val, out string legacyDecrypted))
                    {
                        return legacyDecrypted;
                    }

                    return val;
                }
                return val;
            }
            return string.Empty;
        }

        public void SetSetting(string key, string value)
        {
            string storedValue = ProtectSettingValue(key, value);
            bool saved;
            _saveLock.Wait();
            try
            {
                bool hadPreviousValue = _domainMap.TryGetValue(key, out string? previousValue);
                _domainMap[key] = storedValue;
                saved = SaveConfigCore();
                if (!saved)
                {
                    RestoreSetting(key, hadPreviousValue, previousValue);
                }
            }
            finally
            {
                _saveLock.Release();
            }

            if (saved)
            {
                OnSettingChanged(key, value);
            }
        }

        public bool SetSettings(IReadOnlyDictionary<string, string> settings)
        {
            if (settings == null || settings.Count == 0)
            {
                return true;
            }

            var protectedSettings = settings.ToDictionary(
                setting => setting.Key,
                setting => ProtectSettingValue(setting.Key, setting.Value));
            bool saved;
            _saveLock.Wait();
            try
            {
                var previousSettings = protectedSettings.Keys.ToDictionary(
                    key => key,
                    key => _domainMap.TryGetValue(key, out string? previousValue)
                        ? (Exists: true, Value: previousValue)
                        : (Exists: false, Value: (string?)null));

                foreach (var setting in protectedSettings)
                {
                    _domainMap[setting.Key] = setting.Value;
                }

                saved = SaveConfigCore();
                if (!saved)
                {
                    foreach (var previous in previousSettings)
                    {
                        RestoreSetting(previous.Key, previous.Value.Exists, previous.Value.Value);
                    }
                }
            }
            finally
            {
                _saveLock.Release();
            }

            if (saved)
            {
                foreach (var setting in settings)
                {
                    OnSettingChanged(setting.Key, setting.Value);
                }
            }

            return saved;
        }

        public async Task SetSettingAsync(string key, string value)
        {
            string storedValue = ProtectSettingValue(key, value);
            bool saved;
            await _saveLock.WaitAsync();
            try
            {
                bool hadPreviousValue = _domainMap.TryGetValue(key, out string? previousValue);
                _domainMap[key] = storedValue;
                saved = await SaveConfigCoreAsync();
                if (!saved)
                {
                    RestoreSetting(key, hadPreviousValue, previousValue);
                }
            }
            finally
            {
                _saveLock.Release();
            }
            if (saved)
            {
                OnSettingChanged(key, value);
            }
        }

        private void RestoreSetting(string key, bool existed, string? value)
        {
            if (existed)
            {
                _domainMap[key] = value ?? string.Empty;
            }
            else
            {
                _domainMap.TryRemove(key, out _);
            }
        }

        private static string ProtectSettingValue(string key, string value)
        {
            string storedValue = value;
            if (IsProtectedSetting(key) && !string.IsNullOrEmpty(value) && value != "adminadmin")
            {
                try
                {
                    if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                    {
                        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                        storedValue = DpapiProtectedValuePrefix + Convert.ToBase64String(encrypted);
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

            return storedValue;
        }

        private bool MigrateLegacyProtectedSettings()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            bool changed = false;
            foreach (string key in _domainMap.Keys.Where(IsProtectedSetting))
            {
                if (!_domainMap.TryGetValue(key, out string? storedValue) ||
                    string.IsNullOrEmpty(storedValue) ||
                    storedValue == "adminadmin" ||
                    storedValue.StartsWith(DpapiProtectedValuePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string plaintext = TryUnprotectDpapiValue(storedValue, out string legacyDecrypted)
                    ? legacyDecrypted
                    : storedValue;

                try
                {
                    _domainMap[key] = ProtectSettingValue(key, plaintext);
                    changed = true;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Could not migrate protected setting '{key}': {ex.Message}", "WARNING");
                }
            }

            return changed;
        }

        private static bool TryUnprotectDpapiValue(string base64Value, out string plaintext)
        {
            plaintext = string.Empty;
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                byte[] decrypted = ProtectedData.Unprotect(
                    Convert.FromBase64String(base64Value),
                    null,
                    DataProtectionScope.CurrentUser);
                plaintext = Encoding.UTF8.GetString(decrypted);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                return false;
            }
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
                // Legacy custom URLs are preserved when present, but new profiles
                // discover anime sources from indexes rather than seeded domains.
                { "CustomSources", "[]" },

                // qBittorrent settings
                { "QBitHost", "localhost" },
                { "QBitPort", "8080" },
                { "QBitUsername", "admin" },
                { "QBitPassword", "adminadmin" },

                // MAL integration
                { "MalOAuthToken", "" },
                { "MalOAuthRefreshToken", "" },
                { "MalOAuthExpiresAtUtc", "" },
                { "MalOAuthUsername", "" },
                { "MalClientId", "" },
                { "MalClientSecret", "" },
                { "MalOAuthRedirectPort", "8765" },

                // Playback / UI preferences
                { "DefaultAudioPref", "Sub" },
                { "AutoPlayAfterDownload", "true" },
                { "NewEpisodeAlerts", "true" },
                { "AutoSyncMal", "false" },
                { "EnableDebugLogging", "true" },
                { "ShowAdultContent", "false" },
                { "AutoManageServices", "true" },
                { "ScraperSiteAttemptLimit", "6" },
                { "UiScalePercent", "100" },
                { "IsDarkMode", "true" },
                { "AccentColor", "Teal" },
                { "SelectedLanguage", "English" },
                { "UiDensity", "Comfortable" },
                { "PosterFit", "Contain" },
                { "ShowServiceBar", "true" },
                { "ReduceMotion", "false" },
                { "StartMaximized", "true" },
                { "StartupMonitor", "Primary" },
                { "CornerRadiusPreview", "8" },
                { "DownloadDirectory", "" },

                // Redirectable API URLs
                { "AniListUrl", "https://graphql.anilist.co" },
                { "AniSkipUrl", "https://api.aniskip.com" },
                { "MalApiUrl", "https://api.myanimelist.net" },
                { "NyaaUrl", "https://nyaa.si/?page=rss&c=1_2&f=0&q=" },
                { "AnimeToshoUrl", "https://feed.animetosho.org/rss2?q=" },
                { "MangaDexUrl", "https://api.mangadex.org" },
                { "MangaDexCoversUrl", "https://uploads.mangadex.org" },
                { "DubAvailabilityProviders", "[{\"Name\":\"AniKoto\",\"SuggestUrlTemplate\":\"https://anikoto.cz/search?keyword={query}\",\"Enabled\":true,\"TimeoutSeconds\":8,\"ParserType\":\"AniKoto\",\"AdapterType\":\"AniKoto\"}]" },
                { "DatabasePath", "" },
                { "TmdbApiKey", "" },
                { "TmdbApiUrl", "https://api.themoviedb.org/3/" },
                { "TmdbLanguage", "en-US" },
                { "OtherMediaEnableInternetArchive", "true" },
                { "InternetArchiveUrl", "https://archive.org/" },
                { "OtherMediaProviderIndexes", "[]" },
                { "OtherMediaMaximumDownloadBytes", "26843545600" },
                { "GoogleBooksApiKey", "" },
                { "GoogleBooksApiUrl", "https://www.googleapis.com/books/v1/" },
                { "OpenLibraryApiUrl", "https://openlibrary.org/" },
                { "AnnasArchiveUrl", "https://annas-archive.gl" },
                { "OtherMediaScraperUrl", "https://vidsrc.to" },
                { "OtherMediaCustomSources", "[]" },
                { "BookCacheDirectory", "" },
                { "JikanApiUrl", "https://api.jikan.moe/v4" },
                { "VaDetectDefaultMode", "Dub" },
                { "VaDetectEnableBackgroundPrefetch", "false" },
                { "VaDetectPublicMalFallbackUsername", "" },
                { "WatchTogetherPort", "8000" },
                { "WatchTogetherLastServerUrl", "localhost" },
                { "WatchTogetherLastRoomId", "room-" + Guid.NewGuid().ToString("N")[..12] },
                { "WatchTogetherOffsetSeconds", "0" }
            };
            return defaults;
        }
    }
}
