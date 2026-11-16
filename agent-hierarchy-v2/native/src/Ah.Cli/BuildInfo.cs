using System.Reflection;

namespace Ah.Cli;

/// <summary>The identity the build embedded: the plugin version, the hook contract and the plugin name.</summary>
internal static class BuildInfo
{
    internal static string Version => Get("AhVersion");

    internal static string Contract => Get("AhContract");

    internal static string PluginName => Get("AhPluginName");

    private static string Get(string key) =>
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? string.Empty;
}
