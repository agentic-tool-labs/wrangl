using System.Globalization;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Roster;

/// <summary>What the files show about a member that the auto-mode classifier stopped: its launch file, its sandbox and the member surface.</summary>
/// <param name="Launch">The launch file's state: <c>absent</c>, <c>stale (...)</c> or <c>ok (...)</c>.</param>
/// <param name="Sandbox">The member's sandbox setting: <c>on</c>, <c>off</c> or <c>absent</c>.</param>
/// <param name="Surface">The member surface line, with the entry that matches the denied command when there is one.</param>
/// <param name="SurfaceMissing">Whether the member-surface file could not be read.</param>
/// <param name="Tool">The name of the in-process tool (not a shell command) the classifier refused, or null when the denial was a shell command.</param>
/// <param name="ToolAllowed">Whether the member's launch file has an allow rule naming <paramref name="Tool"/>.</param>
public sealed record PermissionFacts(string Launch, string Sandbox, string Surface, bool SurfaceMissing, string? Tool = null, bool ToolAllowed = false);

/// <summary>The first cause that holds for a denial, in the order <c>permissions diagnose</c> reports them.</summary>
/// <param name="N">The cause's rank (1 first).</param>
/// <param name="Name">The cause's name: launch-file, no-consent, sandbox, tool-not-allowed or classifier-decision.</param>
/// <param name="Fix">The one-line remedy.</param>
public sealed record PermissionVerdict(int N, string Name, string Fix);

/// <summary>
/// The one implementation of why a member was stopped by the classifier. <c>permissions diagnose</c> prints its result and the
/// classifier-denied alarm embeds its one-line form.
/// </summary>
internal static partial class PermissionCause
{
    /// <summary>The facts about <paramref name="member"/> read from <paramref name="teamHome"/>'s launch directory and the plugin's member surface.</summary>
    internal static PermissionFacts ReadFacts(IFileReader files, string? pluginRoot, string teamHome, string member, string? deniedCommand)
    {
        ArgumentNullException.ThrowIfNull(files);
        var launchDir = NodePath.Join(teamHome, "launch");
        var settings = ReadJson(files, NodePath.Join(launchDir, member + ".settings.json"));
        var meta = ReadJson(files, NodePath.Join(launchDir, member + ".meta.json"));
        var surface = pluginRoot is null ? null : ReadJson(files, NodePath.Join(pluginRoot, "policy/member-surface.json"));
        var surfaceVersion = surface?.Member("version") is { IsNumber: true } sv ? sv.AsNumber() : (double?)null;
        var metaVersion = meta?.Member("surface_version") is { IsNumber: true } mv ? mv.AsNumber() : (double?)null;

        string launch;
        if (settings is null)
        {
            launch = "absent";
        }
        else if (surfaceVersion is { } current && (metaVersion is not { } built || built < current))
        {
            launch = $"stale (surface v{Fmt(metaVersion)} < v{Fmt(surfaceVersion)})";
        }
        else
        {
            launch = $"ok (v{Fmt(metaVersion)}, {(meta?.Member("generated_at") is { IsText: true } g ? g.AsString() : "unknown time")})";
        }

        // A generated launch file has no sandbox key (MemberPermissionsGenerator), so no key is not "off" and is not a cause.
        var sandbox = settings?.Member("sandbox")?.Member("enabled") is { IsBool: true } on ? (on.AsBool() ? "on" : "off") : "absent";

        string surfaceText;
        var tool = deniedCommand is null ? null : ToolNameOf(deniedCommand);
        if (surface is null)
        {
            surfaceText = "absent";
        }
        else if (tool is not null)
        {
            surfaceText = $"v{Fmt(surfaceVersion)}: the last denial is the {tool} tool, which the surface does not list";
        }
        else
        {
            var entry = deniedCommand is null ? null : MemberSurface.Match(surface.Value, deniedCommand);
            surfaceText = deniedCommand is null
                ? $"v{Fmt(surfaceVersion)}"
                : entry is null ? $"v{Fmt(surfaceVersion)}: no entry matches" : $"v{Fmt(surfaceVersion)}: last command matches {entry.Id} ({entry.Kind})";
        }

        var toolAllowed = tool is not null && settings?.Member("permissions")?.Member("allow") is { IsArray: true } allow
            && allow.AsArray().Any(rule => rule.IsText && rule.AsString() == tool);
        return new PermissionFacts(launch, sandbox, surfaceText, surface is null, tool, toolAllowed);
    }

    /// <summary>
    /// The tool a denial names when it was not a shell command. The alarm records a shell command as its text and any other tool as
    /// its name, so a first word that is a Claude Code tool name in <see cref="ToolNames"/> or an <c>mcp__</c> tool is a tool call; any other
    /// first word (<c>Rscript</c>, <c>MSBuild</c>) is a program.
    /// </summary>
    internal static string? ToolNameOf(string deniedCommand)
    {
        var first = deniedCommand.AsSpan().Trim();
        var space = first.IndexOfAny(' ', '\t');
        var word = (space < 0 ? first : first[..space]).ToString();
        return ToolNames.Contains(word) || McpTool().IsMatch(word) ? word : null;
    }

    /// <summary>The Claude Code tools a member's denial can name; a closed list so a capitalised program is not taken for one.</summary>
    private static readonly HashSet<string> ToolNames = new(StringComparer.Ordinal)
    {
        "Agent", "Task", "SendMessage", "ListAgents", "Read", "Write", "Edit", "MultiEdit", "NotebookEdit", "Glob", "Grep", "WebFetch",
        "WebSearch", "TodoWrite", "Skill", "ToolSearch", "Monitor", "TaskStop", "AskUserQuestion", "PushNotification", "SendUserFile",
        "EnterPlanMode", "ExitPlanMode", "EnterWorktree", "ExitWorktree", "CronCreate", "CronDelete", "CronList", "RemoteTrigger",
        "ScheduleWakeup", "Workflow", "BashOutput", "KillShell",
    };

    [System.Text.RegularExpressions.GeneratedRegex("^mcp__[A-Za-z0-9_-]+\\z")]
    private static partial System.Text.RegularExpressions.Regex McpTool();

    /// <summary>The first cause that holds for a denial given <paramref name="facts"/> and the member-launch consent.</summary>
    internal static PermissionVerdict CauseOf(PermissionFacts facts, MemberLaunchVerdict consent)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(consent);
        if (facts.Launch == "absent" && !consent.Effective)
        {
            return new PermissionVerdict(2, "no-consent", $"{consent.Reason}: " + (consent.Reason!.StartsWith("disabled", StringComparison.Ordinal)
                ? "`ah allow on` turns member launch rules back on"
                : "it stays declined until the user runs `ah allow on`"));
        }

        if (facts.Launch == "absent" || facts.Launch.StartsWith("stale", StringComparison.Ordinal))
        {
            return new PermissionVerdict(1, "launch-file", facts.Launch == "absent" && facts.SurfaceMissing
                ? "absent: phase 1b not installed"
                : "restart or resume the member (launch files regenerate at start)");
        }

        if (facts.Sandbox == "off")
        {
            return new PermissionVerdict(3, "sandbox", "the member runs without the sandbox (unavailable on the host, or off); restart it once the sandbox is available");
        }

        if (facts.Tool is { } tool)
        {
            return facts.ToolAllowed
                ? new PermissionVerdict(5, "classifier-decision", $"{tool} has an allow rule in the member's generated settings, and the auto-mode classifier stopped it anyway; no ah setting changes that")
                : new PermissionVerdict(4, "tool-not-allowed", $"{tool} has no allow rule in the member's generated settings");
        }

        return new PermissionVerdict(5, "classifier-decision", "the member's generated settings allow every shell command, and the auto-mode classifier stopped this one anyway; no ah setting changes that");
    }

    /// <summary>
    /// The cause of a denial of <paramref name="deniedCommand"/> for <paramref name="member"/>, reading the facts and the consent from the files.
    /// </summary>
    internal static PermissionVerdict Diagnose(
        IFileReader files, string? pluginRoot, string teamHome, string asksPath, bool memberAllowEnabled, string member, string deniedCommand, out PermissionFacts facts, string? memberAllowOnAt = null)
    {
        facts = ReadFacts(files, pluginRoot, teamHome, member, deniedCommand);
        return CauseOf(facts, ConfigModule.EvaluateMemberLaunchConsent(files, asksPath, memberAllowEnabled, memberAllowOnAt));
    }

    /// <summary>
    /// <c>member_allow.enabled</c> over the given config files: false when any of them sets it to false, so a later file can only narrow
    /// it; true otherwise.
    /// </summary>
    internal static bool MemberAllowEnabled(IFileReader files, IEnumerable<string?> configPaths)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(configPaths);
        foreach (var path in configPaths)
        {
            if (path is not null && ReadJson(files, path) is { } config && config.Member("member_allow")?.Member("enabled") is { IsBool: true } flag && !flag.AsBool())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>member_allow.reask_at</c> of the user's config file: when the user last ran <c>ah allow on</c> over a decline; null when none.
    /// Only the user's file counts, because that is where <c>ah allow on</c> writes it; a project file can only narrow.
    /// </summary>
    internal static string? MemberAllowOnAt(IFileReader files, string? userConfigPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        return userConfigPath is not null && ReadJson(files, userConfigPath) is { } config && config.Member("member_allow")?.Member("reask_at") is { IsText: true } at
            ? at.AsString()
            : null;
    }

    private static string Fmt(double? v) => v is { } n ? n.ToString("0", CultureInfo.InvariantCulture) : "?";

    private static JsonValue? ReadJson(IFileReader files, string path)
    {
        try
        {
            return files.Exists(path) && JsonText.Parse(files.ReadText(path)) is { IsMap: true } parsed ? parsed : (JsonValue?)null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }
}

/// <summary>The member surface's entry-match rule, over a command line.</summary>
internal static class MemberSurface
{
    internal sealed record Entry(string Id, string Kind);

    private const string Untokenizable = "|&;<>()$`\"'\\*?{}~#\n";

    /// <summary>
    /// The first entry, in file order, that matches <paramref name="command"/>. A command with any shell syntax cannot be tokenized and
    /// matches no entry. <c>argv0</c> is compared by basename; for git <c>sub</c> is the first word after git's global options
    /// (<c>-C &lt;path&gt;</c>, <c>-c k=v</c>); <c>prefix</c> matches the leading tokens literally.
    /// </summary>
    internal static Entry? Match(JsonValue surface, string command)
    {
        if (command.Any(static ch => Untokenizable.Contains(ch, StringComparison.Ordinal)) || surface.Member("entries") is not { IsArray: true } entries)
        {
            return null;
        }

        var tokens = command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var argv0 = tokens[0][(tokens[0].LastIndexOf('/') + 1)..];
        var sub = argv0 == "git" ? GitSub(tokens) : null;
        foreach (var entry in entries.AsArray())
        {
            if (!entry.IsMap || entry.Member("match") is not { IsMap: true } m || entry.Member("id") is not { IsText: true } id || entry.Member("kind") is not { IsText: true } kind)
            {
                continue;
            }

            if (m.Member("argv0") is { IsText: true } want && want.AsString() != argv0)
            {
                continue;
            }

            if (m.Member("sub") is { IsArray: true } subs && (sub is null || !subs.AsArray().Any(x => x.IsText && x.AsString() == sub)))
            {
                continue;
            }

            if (m.Member("prefix") is { IsArray: true } prefix && !PrefixHolds(prefix.AsArray(), tokens))
            {
                continue;
            }

            return new Entry(id.AsString(), kind.AsString());
        }

        return null;
    }

    private static bool PrefixHolds(JsonArray prefix, string[] tokens)
    {
        var words = prefix.Where(static p => p.IsText).Select(static p => p.AsString()).ToList();
        return words.Count <= tokens.Length && words.Select((w, i) => tokens[i] == w).All(static ok => ok);
    }

    private static string? GitSub(string[] tokens)
    {
        for (var i = 1; i < tokens.Length; i++)
        {
            if (tokens[i] is "-C" or "-c")
            {
                i++;
            }
            else if (!tokens[i].StartsWith('-'))
            {
                return tokens[i];
            }
        }

        return null;
    }
}
