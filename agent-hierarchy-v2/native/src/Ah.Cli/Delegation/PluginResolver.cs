using System.Text.Json;
using Ah.Abstractions;
using Ah.DaemonClient;

namespace Ah.Cli.Delegation;

internal sealed record ResolvedPlugin(string Root, string Source);

internal static class PluginResolver
{
    internal const string Marker = "bin/ah-hook";

    /// <summary>
    /// The <c>--plugin-root</c> option when it names this plugin's directory (any mode), then AH_PLUGIN_ROOT (in test mode only),
    /// then the highest user-scope install record, then any other scope, then the stamp's root. Every candidate other than the
    /// option must contain <c>bin/ah-hook</c>.
    /// </summary>
    internal static ResolvedPlugin? Resolve(IAhEnvironment env, string pluginName, string? stampPluginRoot, string? requestedRoot = null)
    {
        if (requestedRoot is not null && PluginRootOption.Problem(requestedRoot, pluginName) is null)
        {
            return new ResolvedPlugin(requestedRoot, "option");
        }

        if (env.GetTestOnly("AH_PLUGIN_ROOT") is { Length: > 0 } fromEnv && HasMarker(fromEnv))
        {
            return new ResolvedPlugin(fromEnv, "env");
        }

        if (FromInstalled(env, pluginName) is { } installed)
        {
            return new ResolvedPlugin(installed, "installed");
        }

        return stampPluginRoot is not null && HasMarker(stampPluginRoot)
            ? new ResolvedPlugin(stampPluginRoot, "stamp")
            : null;
    }

    /// <summary>
    /// The <c>plugin_root</c> the install stamp, <c>&lt;AH_HOME&gt;/install.json</c>, records: the directory the installer ran from,
    /// so a daemon started from a terminal, with no hook or option to hand it a root, still finds the plugin. Null when there is no
    /// stamp, it is not readable JSON, or the recorded directory is not this plugin's.
    /// </summary>
    internal static string? StampRoot(IAhEnvironment env, string pluginName)
    {
        ArgumentNullException.ThrowIfNull(env);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(env.AhHome, "install.json")));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && Text(document.RootElement, "plugin_root") is { } root
                && PluginRootOption.Problem(root, pluginName) is null
                    ? root
                    : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static bool HasMarker(string root) => File.Exists(Path.Combine(root, "bin", "ah-hook"));

    private static string? FromInstalled(IAhEnvironment env, string pluginName)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(env.HomeDirectory, ".claude", "plugins", "installed_plugins.json"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var candidates = new List<(string Path, string? Version, bool User)>();
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var entry in plugins.EnumerateObject())
            {
                var at = entry.Name.IndexOf('@');
                if ((at == -1 ? entry.Name : entry.Name[..at]) != pluginName || entry.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var record in entry.Value.EnumerateArray())
                {
                    if (record.ValueKind != JsonValueKind.Object || Text(record, "installPath") is not { } path || !HasMarker(path))
                    {
                        continue;
                    }

                    candidates.Add((path, Text(record, "version"), Text(record, "scope") == "user"));
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        var pool = candidates.Any(c => c.User) ? candidates.Where(c => c.User).ToList() : candidates;
        string? best = null;
        string? bestVersion = null;
        foreach (var (path, version, _) in pool)
        {
            if (best is null || DaemonClientModule.CompareVersions(version ?? "0", bestVersion ?? "0") > 0)
            {
                best = path;
                bestVersion = version;
            }
        }

        return best;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
