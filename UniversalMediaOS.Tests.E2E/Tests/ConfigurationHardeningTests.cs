using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using UniversalMediaOS.Core.Configuration;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests
{
    public sealed class ConfigurationHardeningTests
    {
        [Fact]
        public void SetSettings_WritesOneConsistentSnapshot_AndProtectsSecrets()
        {
            string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.ConfigTests", Guid.NewGuid().ToString("N"));
            string configPath = Path.Combine(root, "config.json");
            try
            {
                var config = new DomainHotSwapper(configPath);
                bool saved = config.SetSettings(new Dictionary<string, string>
                {
                    ["QBitHost"] = "127.0.0.1",
                    ["QBitPort"] = "9191",
                    ["QBitPassword"] = "not-a-real-password",
                    ["TmdbApiKey"] = "not-a-real-tmdb-key",
                    ["GoogleBooksApiKey"] = "not-a-real-books-key",
                    ["NewEpisodeAlerts"] = "false"
                });

                Assert.True(saved);
                Assert.Equal("9191", config.GetSetting("QBitPort"));
                Assert.Equal("not-a-real-password", config.GetSetting("QBitPassword"));
                Assert.Equal("not-a-real-tmdb-key", config.GetSetting("TmdbApiKey"));
                Assert.Equal("not-a-real-books-key", config.GetSetting("GoogleBooksApiKey"));

                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                Assert.Equal("9191", document.RootElement.GetProperty("QBitPort").GetString());
                if (OperatingSystem.IsWindows())
                {
                    Assert.NotEqual(
                        "not-a-real-password",
                        document.RootElement.GetProperty("QBitPassword").GetString());
                    Assert.NotEqual(
                        "not-a-real-tmdb-key",
                        document.RootElement.GetProperty("TmdbApiKey").GetString());
                    Assert.NotEqual(
                        "not-a-real-books-key",
                        document.RootElement.GetProperty("GoogleBooksApiKey").GetString());
                }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task FailedWrites_RollBackMemory_AndDoNotPublishChanges()
        {
            string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.ConfigRollbackTests", Guid.NewGuid().ToString("N"));
            string configPath = Path.Combine(root, "config.json");
            try
            {
                var config = new DomainHotSwapper(configPath);
                config.SetSetting("QBitPort", "8081");
                int notificationCount = 0;
                config.SettingChanged += (_, _) => notificationCount++;

                File.Delete(configPath);
                Directory.CreateDirectory(configPath);

                config.SetSetting("QBitPort", "9191");
                Assert.Equal("8081", config.GetSetting("QBitPort"));
                Assert.Equal(0, notificationCount);

                bool saved = config.SetSettings(new Dictionary<string, string>
                {
                    ["QBitPort"] = "9292",
                    ["RollbackOnlyKey"] = "temporary"
                });
                Assert.False(saved);
                Assert.Equal("8081", config.GetSetting("QBitPort"));
                Assert.Equal(string.Empty, config.GetSetting("RollbackOnlyKey"));
                Assert.Equal(0, notificationCount);

                await config.SetSettingAsync("QBitPort", "9393");
                Assert.Equal("8081", config.GetSetting("QBitPort"));
                Assert.Equal(0, notificationCount);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
