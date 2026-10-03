using System.IO;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class AppDataPathsTests
{
    [Fact]
    public void OverrideSeparatesLocalAndRoamingAndRedirectsScrapers()
    {
        string root = Path.Combine(Path.GetTempPath(), "UniversalMediaOS.Tests", Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, root);
            Assert.Equal(Path.Combine(root, "Roaming"), AppDataPaths.RoamingBaseDirectory);
            Assert.Equal(Path.Combine(root, "Local"), AppDataPaths.LocalBaseDirectory);
            using var bootstrapper = new PythonBootstrapper();
            Assert.Equal(Path.Combine(root, "Local", "UniversalMediaOS", "Services", "scraper.py"), bootstrapper.GetScraperPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DefaultPreservesExistingStorageLocations()
    {
        string? previous = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, null);
            string? roaming = Environment.GetEnvironmentVariable("APPDATA");
            Assert.Equal(string.IsNullOrWhiteSpace(roaming) ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : roaming, AppDataPaths.RoamingBaseDirectory);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppDataPaths.LocalBaseDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void RelativeOverrideFailsInsteadOfWritingBesideExecutable()
    {
        string? previous = Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, "relative-data");
            Assert.Throws<InvalidOperationException>(() => AppDataPaths.LocalBaseDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable, previous);
        }
    }
}
