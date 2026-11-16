using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>The behavioural contract text a chain role is given: <c>contracts/common.md</c> plus the file of its class.</summary>
public interface IRoleContracts
{
    /// <summary>
    /// <c>contracts/common.md</c> plus the file of class <paramref name="cls"/>, placeholders substituted (<paramref name="routes"/>
    /// is the text of an alternative role's <c>routes</c>, else null; <paramref name="peerRoute"/> drops the <c>~</c> lines the
    /// role-session notice already carries). Null when a contract file is missing; that is logged once.
    /// </summary>
    string? ContractText(string role, JsonObject? resolved, string cls, string? routes, bool peerRoute = false);
}

internal sealed class RoleContracts(IConfigCore config, IFileReader files, IAhEnvironment environment, IHookErrorLog? errors) : IRoleContracts
{
    private bool _logged;

    private string? ReadContractFile(string name)
    {
        var root = environment.Plugin?.Root ?? throw new InvalidOperationException("the plugin root is unknown");
        try
        {
            return files.ReadText(NodePath.Join(root, "contracts", name + ".md"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_logged)
            {
                _logged = true;
                errors?.Log("contracts", e.Message);
            }

            return null;
        }
    }

    private static string Text(JsonValue value) => value.IsText ? value.AsString() : JsonText.Stringify(value, 0);

    public string? ContractText(string role, JsonObject? resolved, string cls, string? routes, bool peerRoute = false)
    {
        var common = ReadContractFile("common");
        var specific = ReadContractFile(cls);
        if (common is null || specific is null)
        {
            return null;
        }

        var builtin = config.ClassBuiltin(cls);
        var builtinLabel = builtin is not null && config.RoleLabels.Get(builtin) is { } label ? Text(label) : ",";
        var lines = new List<string>();
        foreach (var line in common.Split('\n'))
        {
            if (JsText.Trim(line).Length == 0)
            {
                continue;
            }

            if (line.StartsWith("?routes ", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(routes))
                {
                    lines.Add(line["?routes ".Length..]);
                }

                continue;
            }

            if (line.StartsWith("~ ", StringComparison.Ordinal))
            {
                if (!peerRoute)
                {
                    lines.Add(line[2..]);
                }

                continue;
            }

            lines.Add(line);
        }

        var all = string.Join('\n', ["HIERARCHY CONTRACT —", .. lines, JsText.Trim(specific)]);
        return all
            .Replace("<Label>", Text(config.RoleLabel(role, resolved)), StringComparison.Ordinal)
            .Replace("<class>", cls, StringComparison.Ordinal)
            .Replace("<BuiltinLabel>", builtinLabel, StringComparison.Ordinal)
            .Replace("<routes>", routes ?? string.Empty, StringComparison.Ordinal)
            .Replace("node \"<msg-cli>\"", config.CliCommand(CliTool.Msg, alwaysQuote: true), StringComparison.Ordinal)
            .Replace("<msg-cli>", config.CliCommand(CliTool.Msg), StringComparison.Ordinal);
    }
}
