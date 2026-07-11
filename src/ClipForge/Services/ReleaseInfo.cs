using System.Reflection;

namespace ClipForge.Services;

public static class ReleaseInfo
{
    private static readonly Assembly Assembly = typeof(ReleaseInfo).Assembly;

    public static string Version { get; } = GetProductVersion();

    public static string? UpdateUrl { get; } = Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute =>
            string.Equals(attribute.Key, "ClipForgeUpdateUrl", StringComparison.Ordinal))
        ?.Value?
        .Trim() is { Length: > 0 } value
            ? value
            : null;

    private static string GetProductVersion()
    {
        var informationalVersion = Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        return Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
