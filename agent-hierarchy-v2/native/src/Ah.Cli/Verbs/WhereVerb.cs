using System.Globalization;
using System.Text;
using System.Text.Json;
using Ah.Cli.Delegation;
using Ah.Cli.Login;
using Ah.Core.Json;

namespace Ah.Cli.Verbs;

internal static class WhereVerb
{
    /// <summary>
    /// <c>where [--plain]</c>: the binary, the plugin it would run, the login state and the browser bridge (JSON only), as one JSON object. Exit 0 whenever the binary runs. Nothing is written and no daemon is contacted.
    /// </summary>
    internal static int Run(CliContext context, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var plain = false;
        foreach (var argument in rest)
        {
            if (argument == "--plain")
            {
                plain = true;
                continue;
            }

            stderr.WriteLine($"ahd: where does not accept {argument}");
            return 2;
        }

        var plugin = PluginResolver.Resolve(context.Env, BuildInfo.PluginName, PluginResolver.StampRoot(context.Env, BuildInfo.PluginName), PluginRootOption.Requested);
        var pluginVersion = plugin is null ? null : ReadField(Path.Combine(plugin.Root, ".claude-plugin", "plugin.json"), "version");
        var pluginContract = plugin is null ? null : ReadField(Path.Combine(plugin.Root, "native", "cutover.json"), "contract");
        var compatible = plugin is not null && pluginContract is not null && pluginContract == BuildInfo.Contract;
        var login = LoginState.Detect(context);
        var binaryPath = CliContext.RealPath(context.ProcessPath);

        if (plain)
        {
            var sb = new StringBuilder();
            sb.Append("binary.path: ").Append(binaryPath).Append('\n');
            sb.Append("binary.version: ").Append(BuildInfo.Version).Append('\n');
            sb.Append("binary.contract: ").Append(BuildInfo.Contract).Append('\n');
            sb.Append("binary.plugin_name: ").Append(BuildInfo.PluginName).Append('\n');
            sb.Append("plugin: ").Append(plugin is null ? "none" : $"{plugin.Root} ({plugin.Source}, version {pluginVersion ?? "unknown"}, contract {pluginContract ?? "unknown"})").Append('\n');
            sb.Append("compatible: ").Append(compatible ? "yes" : "no").Append('\n');
            sb.Append("login: ").Append(login).Append('\n');
            stdout.Write(sb.ToString());
            return 0;
        }

        var binary = new JsonObject();
        binary["path"] = binaryPath;
        binary["version"] = BuildInfo.Version;
        binary["contract"] = ContractValue(BuildInfo.Contract);
        binary["plugin_name"] = BuildInfo.PluginName;

        var report = new JsonObject();
        report["binary"] = binary;
        report["plugin"] = PluginValue(plugin, pluginVersion, pluginContract);
        report["compatible"] = compatible;
        report["login"] = login;
        report["ui"] = UiValue(Path.Combine(context.Env.AhHome, "run"));
        stdout.Write(JsonText.Stringify(report, 0) + "\n");
        return 0;
    }

    /// <summary>
    /// The loopback bridge as ahd's files describe it: <c>live</c> while <c>run/ui.json</c> names a port, and <c>port</c> that one, else the
    /// one <c>run/ui-port</c> remembers, else null. Files only: no daemon.
    /// </summary>
    private static JsonObject UiValue(string runDir)
    {
        var live = PortOf(ReadField(Path.Combine(runDir, "ui.json"), "port"));
        var port = live ?? PortOf(ReadText(Path.Combine(runDir, "ui-port")));
        return new JsonObject { ["port"] = port is { } p ? p : JsonValue.Null, ["live"] = live is not null };
    }

    private static int? PortOf(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonValue ContractValue(string? contract) =>
        contract is null ? JsonValue.Null
        : contract.Length > 0 && contract.All(char.IsAsciiDigit) && (contract.Length == 1 || contract[0] != '0')
            ? JsonValue.FromNumber(double.Parse(contract, CultureInfo.InvariantCulture))
            : JsonValue.FromString(contract);

    private static JsonValue PluginValue(ResolvedPlugin? plugin, string? version, string? contract)
    {
        if (plugin is null)
        {
            return JsonValue.Null;
        }

        var value = new JsonObject();
        value["root"] = plugin.Root;
        value["version"] = version is null ? JsonValue.Null : version;
        value["contract"] = ContractValue(contract);
        value["source"] = plugin.Source;
        return value;
    }

    /// <summary>A top-level string or number field of a JSON file as text, or null when absent or unreadable.</summary>
    private static string? ReadField(string path, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
