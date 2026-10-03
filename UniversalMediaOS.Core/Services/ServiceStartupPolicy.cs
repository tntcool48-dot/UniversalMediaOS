namespace UniversalMediaOS.Core.Services;

public static class ServiceStartupPolicy
{
    public static bool AutoManageEnabled(string? setting) =>
        !string.Equals(setting?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
}
