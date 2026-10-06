using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using Xunit;
using Xunit.Abstractions;

namespace UniversalMediaOS.Tests.E2E;

public sealed class CopiedProfileCompatibilityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CopiedSqliteSettingsAndLibrarySurviveMigrationRestartAndSnapshotRollback()
    {
        string? previousRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        string tempParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests"));
        string root = Path.Combine(tempParent, "copied-profile-" + Guid.NewGuid().ToString("N"));
        string? ownConnectionString = null;
        Directory.CreateDirectory(root);
        try
        {
            string? supplied = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_QA_PROFILE_SNAPSHOT");
            string snapshot = string.IsNullOrWhiteSpace(supplied) ? await CreateFixture(root) : Path.GetFullPath(supplied);
            var sourceFiles = new[] { "media_os.db", "Roaming/UniversalMediaOS/config.json",
                "Local/UniversalMediaOS/OtherMedia/audiovisual-library.json" };
            var sourceDigests = sourceFiles.ToDictionary(file => file, file => Digest(File.ReadAllBytes(Path.Combine(snapshot, file))));
            var expectedTables = Tables(Path.Combine(snapshot, "media_os.db"));
            string legacy = Path.Combine(root, "legacy.db");
            Backup(Path.Combine(snapshot, "media_os.db"), legacy);
            string destination = Path.Combine(root, "active", "media_os.db");
            Directory.CreateDirectory(destination);
            Assert.False(DatabaseContext.TryMigrateLegacyDatabase(legacy, destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.migration-*.tmp"));
            AssertTablesEqual(expectedTables, Tables(legacy));
            Directory.Delete(destination); // This empty directory is owned by this fixture.
            Assert.True(DatabaseContext.TryMigrateLegacyDatabase(legacy, destination));
            AssertTablesEqual(expectedTables, Tables(destination));
            string migratedDigest = Digest(File.ReadAllBytes(destination));
            Assert.True(DatabaseContext.TryMigrateLegacyDatabase(legacy, destination));
            Assert.True(migratedDigest == Digest(File.ReadAllBytes(destination)), "An existing migrated database must remain unchanged.");

            string active = Path.Combine(root, "active");
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, active);
            string configPath = Path.Combine(active, "Roaming", "UniversalMediaOS", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllBytes(Path.Combine(snapshot, sourceFiles[1])))!;
            settings["DatabasePath"] = destination; // Redirect only this copied configuration.
            await File.WriteAllBytesAsync(configPath, JsonSerializer.SerializeToUtf8Bytes(settings));
            var config = new DomainHotSwapper(configPath);
            var protectedKeys = new HashSet<string>(["QBitPassword", "MalOAuthToken", "MalOAuthRefreshToken",
                "MalClientSecret", "TmdbApiKey", "GoogleBooksApiKey"], StringComparer.Ordinal);
            foreach (var (key, value) in settings)
            {
                string expected = value;
                if (protectedKeys.Contains(key) && !string.IsNullOrEmpty(value) && value != "adminadmin")
                {
                    string encrypted = value.StartsWith("dpapi:v1:", StringComparison.Ordinal) ? value[9..] : value;
                    try { expected = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted),
                        null, DataProtectionScope.CurrentUser)); }
                    catch (Exception ex) when (ex is FormatException or CryptographicException)
                    {
                        Assert.False(value.StartsWith("dpapi:v1:", StringComparison.Ordinal), "An existing protected credential must be readable by this user.");
                    }
                }
                Assert.True(expected == config.GetSetting(key), $"Copied setting {key} changed while loading.");
            }
            var expectedSettings = settings.Keys.ToDictionary(key => key, config.GetSetting);
            Assert.True(await config.SaveConfigAsync());
            var restartedConfig = new DomainHotSwapper(configPath);
            foreach (var (key, value) in expectedSettings)
                Assert.True(value == restartedConfig.GetSetting(key), $"Copied setting {key} changed during save/reload.");

            string libraryPath = Path.Combine(active, sourceFiles[2]);
            Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
            File.Copy(Path.Combine(snapshot, sourceFiles[2]), libraryPath);
            var library = new AudiovisualLibraryService(libraryPath);
            var entries = await library.GetAllAsync();
            string openedDigest = Digest(File.ReadAllBytes(libraryPath));
            var restartedEntries = await new AudiovisualLibraryService(libraryPath).GetAllAsync();
            Assert.True(JsonSerializer.Serialize(entries) == JsonSerializer.Serialize(restartedEntries), "Restart must preserve library identities, summaries and provenance.");
            Assert.True(openedDigest == Digest(File.ReadAllBytes(libraryPath)), "A second library read must be idempotent.");
            foreach (string backup in Directory.GetFiles(Path.GetDirectoryName(libraryPath)!, "*.bak"))
                Assert.True(Digest(File.ReadAllBytes(backup)) == sourceDigests[sourceFiles[2]], "Migration backup must retain the exact original library bytes.");

            // Add progress only to the migrated copy. Rollback deliberately restores
            // the earlier snapshot, so this post-snapshot observation is absent.
            using (var database = new DatabaseContext())
            {
                database.Database.EnsureCreated();
                ownConnectionString = database.Database.GetConnectionString();
                database.SaveResumeState("qa:post-snapshot", "episode:1", 123);
            }
            using (var restarted = new DatabaseContext()) Assert.Equal(123, restarted.GetResumeState("qa:post-snapshot", "episode:1"));
            string rollback = Path.Combine(root, "rollback", "media_os.db");
            Assert.True(DatabaseContext.TryMigrateLegacyDatabase(legacy, rollback));
            AssertTablesEqual(expectedTables, Tables(rollback));
            foreach (string file in sourceFiles)
                Assert.True(sourceDigests[file] == Digest(File.ReadAllBytes(Path.Combine(snapshot, file))), "Borrowed snapshot bytes must remain unchanged.");
            output.WriteLine($"Input: {(string.IsNullOrWhiteSpace(supplied) ? "controlled fixture" : "explicit read-only profile snapshot")}; " +
                $"library entries={entries.Count}; preserved tables=" + string.Join(", ", expectedTables.Select(t => $"{t.Key}:{t.Value.Rows}")));
            output.WriteLine("Migration failure/retry, idempotence, settings save/reload, exact table retention, new progress restart and pre-snapshot rollback passed. Post-snapshot progress is intentionally absent from rollback.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previousRoot);
            if (ownConnectionString != null)
            {
                using var connection = new SqliteConnection(ownConnectionString);
                SqliteConnection.ClearPool(connection);
            }
            var ownedDirectory = new DirectoryInfo(root);
            Assert.StartsWith(tempParent + Path.DirectorySeparatorChar, ownedDirectory.FullName, StringComparison.OrdinalIgnoreCase);
            Assert.Null(ownedDirectory.LinkTarget);
            if (ownedDirectory.Exists) ownedDirectory.Delete(recursive: true);
        }
    }

    private static async Task<string> CreateFixture(string root)
    {
        string snapshot = Path.Combine(root, "snapshot");
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, snapshot);
        string databasePath;
        using (var database = new DatabaseContext())
        {
            database.Database.EnsureCreated();
            database.SaveResumeState("legacy:preserved", "1", 90);
            database.SaveResumeState("av:fixture:show", "season:2:episode:1", 180);
            databasePath = database.Database.GetDbConnection().DataSource;
        }
        using (var pool = new SqliteConnection($"Data Source={databasePath};Cache=Shared;")) SqliteConnection.ClearPool(pool);
        Backup(databasePath, Path.Combine(snapshot, "media_os.db"));
        string configPath = Path.Combine(snapshot, "Roaming", "UniversalMediaOS", "config.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["DatabasePath"] = databasePath, ["OtherMediaEnableInternetArchive"] = "false",
            ["AutoManageServices"] = "false", ["Language"] = "Arabic", ["CustomPreserve"] = "opaque",
            ["MalOAuthToken"] = "controlled-test-token"
        }));
        string libraryPath = Path.Combine(snapshot, "Local", "UniversalMediaOS", "OtherMedia", "audiovisual-library.json");
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        await File.WriteAllTextAsync(libraryPath, JsonSerializer.Serialize(new[]
        {
            new AudiovisualLibraryEntry { Key = AudiovisualLibraryKey.Create(AudiovisualMediaKind.Movie, 42, "Preserved film", 2020,
                AudiovisualContentForm.Feature), Title = "Preserved film", IsFavorite = true, PositionSeconds = 125, DurationSeconds = 600 }
        }));
        return snapshot;
    }

    private static void Backup(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source,
            Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination,
            Pooling = false }.ToString());
        input.Open(); output.Open(); input.BackupDatabase(output);
    }

    private static Dictionary<string, (int Rows, string Digest)> Tables(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
            Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", integrity.ExecuteScalar());
        using var names = connection.CreateCommand();
        names.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = names.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read()) tables.Add(reader.GetString(0));
        reader.Close();
        var result = new Dictionary<string, (int, string)>();
        foreach (string table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\" ORDER BY rowid";
            using var rows = command.ExecuteReader();
            var content = new List<object[]>();
            while (rows.Read()) { var row = new object[rows.FieldCount]; rows.GetValues(row); content.Add(row); }
            result[table] = (content.Count, Digest(JsonSerializer.SerializeToUtf8Bytes(content)));
        }
        return result;
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void AssertTablesEqual(Dictionary<string, (int Rows, string Digest)> expected,
        Dictionary<string, (int Rows, string Digest)> actual) =>
        Assert.True(expected.Count == actual.Count && expected.All(row => actual.TryGetValue(row.Key, out var value) && value == row.Value),
            "Every table and original row must survive with identical values.");
}
