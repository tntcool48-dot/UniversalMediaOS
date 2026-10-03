using System;
using System.IO;

namespace UniversalMediaOS.Core.Helpers;

/// <summary>Resolves app storage without changing the operating system's folders.</summary>
public static class AppDataPaths
{
    public const string DataRootEnvironmentVariable = "UNIVERSAL_MEDIA_OS_DATA_ROOT";

    public static string RoamingBaseDirectory => ResolveBaseDirectory(false);
    public static string LocalBaseDirectory => ResolveBaseDirectory(true);

    private static string ResolveBaseDirectory(bool local)
    {
        string? dataRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            if (!Path.IsPathFullyQualified(dataRoot))
                throw new InvalidOperationException($"{DataRootEnvironmentVariable} must be an absolute path.");

            return Path.Combine(Path.GetFullPath(dataRoot), local ? "Local" : "Roaming");
        }

        string? roaming = Environment.GetEnvironmentVariable("APPDATA");
        return !local && !string.IsNullOrWhiteSpace(roaming)
            ? roaming
            : Environment.GetFolderPath(local
                ? Environment.SpecialFolder.LocalApplicationData
                : Environment.SpecialFolder.ApplicationData);
    }
}
