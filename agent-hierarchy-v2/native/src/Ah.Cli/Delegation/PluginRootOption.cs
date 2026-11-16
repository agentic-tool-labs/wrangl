using System.Text.Json;

namespace Ah.Cli.Delegation;

/// <summary>
/// The leading <c>--plugin-root &lt;dir&gt;</c> option a shim hands the binary: the plugin root the shim found from its own resolved
/// location, which the environment cannot set. It is honoured in every mode, and the directory must hold
/// <c>.claude-plugin/plugin.json</c> whose <c>name</c> is this build's plugin name; without the option the root comes from
/// <see cref="PluginResolver"/>.
/// </summary>
internal static class PluginRootOption
{
    internal const string Name = "--plugin-root";

    /// <summary>The value of the leading option of this process's arguments, or null when there is none.</summary>
    internal static string? Requested { get; } = Split(Environment.GetCommandLineArgs().Skip(1).ToArray()).Root;

    /// <summary>The option's value (null when absent) and the arguments without it.</summary>
    internal static (string? Root, string[] Remaining) Split(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length >= 2 && args[0] == Name ? (args[1], args[2..]) : (null, args);
    }

    /// <summary>Why <paramref name="root"/> is not this plugin's directory, or null when it is.</summary>
    internal static string? Problem(string root, string pluginName)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(pluginName);
        if (root.Length == 0 || root[0] != '/')
        {
            return "is not an absolute path";
        }

        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(root, ".claude-plugin", "plugin.json"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "has no .claude-plugin/plugin.json";
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() == pluginName
                    ? null
                    : $"is not the {pluginName} plugin";
        }
        catch (JsonException)
        {
            return "has a .claude-plugin/plugin.json that is not JSON";
        }
    }
}
