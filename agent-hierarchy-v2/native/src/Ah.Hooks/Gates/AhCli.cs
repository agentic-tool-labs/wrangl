using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hooks.Gates;

/// <summary>One parsed <c>node &lt;abs&gt;/hooks/{roster,msg,ahd-ctl}.mjs</c> command. A bare flag has a null value.</summary>
internal sealed record AhCommand(
    string Script,
    string ScriptPath,
    string? Verb,
    IReadOnlyList<string> Positional,
    IReadOnlyList<KeyValuePair<string, string?>> Flags,
    bool Repeated = false)
{
    /// <summary><c>flags[name] === true</c>.</summary>
    public bool FlagIsTrue(string name) => Find(name, out var value) && value is null;

    /// <summary>The flag's text when it was given a value, else null (<c>typeof flags[name] === "string"</c>).</summary>
    public string? FlagText(string name) => Find(name, out var value) ? value : null;

    /// <summary><c>flags[name] !== undefined</c>.</summary>
    public bool HasFlag(string name) => Find(name, out _);

    private bool Find(string name, out string? value)
    {
        foreach (var flag in Flags)
        {
            if (flag.Key == name)
            {
                value = flag.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}

/// <summary>
/// The grammar for ah CLI invocations under the Bash tool. It is fail-closed by construction: anything it is not certain
/// about is null, which makes every consumer silent. It never throws and never touches the filesystem;
/// <see cref="ScriptUnderRoot"/> is the one filesystem check.
/// </summary>
internal static class AhCli
{
    public static readonly IReadOnlyList<string> RosterBoolFlags =
    [
        "plain", "json", "plan", "commit", "partial", "manual", "next", "apply", "kill", "keep-sessions", "spawn", "dry-run",
        "new-tab", "new-workspace", "allow-global", "cancel", "clear", "close", "confirm", "also-config", "no-spawn",
        "allow-roster-edit", "no-legwork-handoff", "wait-only", "no-worktree", "yes",
    ];

    public static readonly IReadOnlyList<string> MsgBoolFlags = ["plain", "json", "open", "closed", "all", "summary", "overdue"];

    public static readonly IReadOnlyList<string> AhdBoolFlags = ["plain", "pending", "acked", "all", "require", "once", "hook", "follow"];

    public static readonly IReadOnlyList<string> CloseVerbs = ["dismiss", "disband"];

    private const string Roster = "roster";
    private const string Msg = "msg";
    private const string AhdCtl = "ahd-ctl";
    private const string Rules = "rules";

    private static string? ScriptOf(string basename) => basename switch
    {
        "roster.mjs" => Roster,
        "msg.mjs" => Msg,
        "ahd-ctl.mjs" => AhdCtl,
        _ => null,
    };

    private static string? GroupOf(string word) => word switch
    {
        Roster => Roster,
        Msg => Msg,
        AhdCtl => AhdCtl,
        Rules => Rules,
        _ => null,
    };

    /// <summary>An absolute path to a file named <c>ah</c> in a directory named <c>bin</c>.</summary>
    public static bool IsAhBinary(string? text) =>
        text is not null && text.StartsWith('/') && NodePath.Basename(text) == "ah" && NodePath.Basename(NodePath.Dirname(text)) == "bin";

    /// <summary>An absolute path to a file named <c>ahd</c> in a directory named <c>bin</c>: the daemon CLI's own command.</summary>
    public static bool IsAhdBinary(string? text) =>
        text is not null && text.StartsWith('/') && NodePath.Basename(text) == "ahd" && NodePath.Basename(NodePath.Dirname(text)) == "bin";

    // An unquoted `#` can start a shell comment, which drops the rest of the command from what runs.
    private static bool IsMeta(char c) => c is ';' or '&' or '|' or '<' or '>' or '(' or ')' or '`' or '$' or '\\' or '\n' or '\r' or '#';

    // The shell expands these before node sees the command, so one outside quotes makes the text parsed here and the
    // command executed two different things.
    private static bool IsExpansion(char c) => c is '{' or '}' or '*' or '?' or '[' or ']' or '~';

    private enum Quoting
    {
        None,
        Whole,
        Mixed,
    }

    private sealed record Token(string Text, Quoting Quoted);

    private static List<Token>? Tokenize(string command)
    {
        var tokens = new List<Token>();
        StringBuilder? text = null;
        var parts = 0;
        var quotedParts = 0;

        void Push()
        {
            if (text is not null)
            {
                tokens.Add(new Token(text.ToString(), quotedParts == 0 ? Quoting.None : parts == 1 ? Quoting.Whole : Quoting.Mixed));
            }

            text = null;
            parts = 0;
            quotedParts = 0;
        }

        void Add(string s, bool quoted)
        {
            text ??= new StringBuilder();
            text.Append(s);
            parts++;
            if (quoted)
            {
                quotedParts++;
            }
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c is ' ' or '\t')
            {
                Push();
                continue;
            }

            if (c == '\'')
            {
                var end = command.IndexOf('\'', i + 1);
                if (end == -1)
                {
                    return null;
                }

                var body = command.Substring(i + 1, end - i - 1);
                if (body.Contains('\n') || body.Contains('\r'))
                {
                    return null;
                }

                Add(body, true);
                i = end;
                continue;
            }

            if (c == '"')
            {
                var end = command.IndexOf('"', i + 1);
                if (end == -1)
                {
                    return null;
                }

                var body = command.Substring(i + 1, end - i - 1);
                // Inside double quotes `#` is literal to the shell, so only an unquoted one ends the parse.
                foreach (var ch in body)
                {
                    if (IsMeta(ch) && ch != '#')
                    {
                        return null;
                    }
                }

                Add(body, true);
                i = end;
                continue;
            }

            if (IsMeta(c) || IsExpansion(c))
            {
                return null;
            }

            Add(c.ToString(), false);
        }

        Push();
        return tokens;
    }

    /// <summary>The command as a parsed ah invocation, or null when it is outside the grammar.</summary>
    public static AhCommand? Parse(string? command) => Parse(command, false);

    /// <summary>
    /// The same grammar, also reading <c>&lt;root&gt;/bin/ah progress …</c> as the roster verb <c>progress</c>, which is how the
    /// verb runs. Only the sub-orchestrator exemption uses it; <see cref="Parse(string?)"/> leaves that form unparsed.
    /// </summary>
    public static AhCommand? ParseWithProgress(string? command) => Parse(command, true);

    private static AhCommand? Parse(string? command, bool progressForm)
    {
        if (command is null || JsText.Trim(command).Length == 0)
        {
            return null;
        }

        var tokens = Tokenize(command);
        if (tokens is null || tokens.Count < 2)
        {
            return null;
        }

        string? script;
        string scriptPath;
        var argvStart = 2;
        if (tokens[0].Quoted == Quoting.None && tokens[0].Text == "node")
        {
            var scriptTok = tokens[1];
            if (scriptTok.Quoted == Quoting.Mixed)
            {
                return null;
            }

            scriptPath = scriptTok.Text;
            if (!scriptPath.StartsWith('/'))
            {
                return null;
            }

            script = ScriptOf(NodePath.Basename(scriptPath));
            if (script is null)
            {
                return null;
            }

            if (NodePath.Basename(NodePath.Dirname(scriptPath)) != "hooks")
            {
                return null;
            }
        }
        else if (progressForm && tokens[0].Quoted != Quoting.Mixed && IsAhBinary(tokens[0].Text) && tokens[1].Quoted == Quoting.None
                 && tokens[1].Text == "progress")
        {
            scriptPath = tokens[0].Text;
            script = Roster;
            argvStart = 1;
        }
        else if (tokens[0].Quoted != Quoting.Mixed && IsAhBinary(tokens[0].Text) && tokens[1].Quoted == Quoting.None
                 && GroupOf(tokens[1].Text) is { } group)
        {
            // `<root>/bin/ah roster|msg|ahd-ctl …`: the same grammar from the same position, with the group word for the script.
            scriptPath = tokens[0].Text;
            script = group;
        }
        else if (tokens[0].Quoted != Quoting.Mixed && IsAhdBinary(tokens[0].Text))
        {
            // `<root>/bin/ahd <verb> …`: the daemon CLI's verb follows the binary directly, with no group word.
            scriptPath = tokens[0].Text;
            script = AhdCtl;
            argvStart = 1;
        }
        else
        {
            return null;
        }

        var bools = script == Roster ? RosterBoolFlags : script == Msg ? MsgBoolFlags : script == Rules ? [] : AhdBoolFlags;
        var argv = tokens.Skip(argvStart).Select(t => t.Text).ToList();
        var positional = new List<string>();
        var flags = new List<KeyValuePair<string, string?>>();
        var repeated = false;
        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            // ahd-ctl.mjs spells the line count `-n`, as tail does.
            if (a.StartsWith("--", StringComparison.Ordinal) || (script == AhdCtl && a == "-n"))
            {
                var key = a.TrimStart('-');
                var hasNext = i + 1 < argv.Count;
                if (!bools.Contains(key) && hasNext && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    repeated |= Assign(flags, key, argv[i + 1]);
                    i++;
                }
                else
                {
                    repeated |= Assign(flags, key, null);
                }
            }
            else
            {
                positional.Add(a);
            }
        }

        var verb = positional.Count > 0 ? positional[0] : null;
        return new AhCommand(script, scriptPath, verb, positional.Skip(1).ToList(), flags, repeated);
    }

    /// <summary>
    /// <c>flags[key] = value</c> on a plain object. A repeated key keeps its first position and takes the last value, and
    /// assigning a primitive to <c>__proto__</c> changes nothing. Returns whether the key was already there.
    /// </summary>
    private static bool Assign(List<KeyValuePair<string, string?>> flags, string key, string? value)
    {
        if (key == "__proto__")
        {
            return false;
        }

        var at = flags.FindIndex(f => f.Key == key);
        if (at >= 0)
        {
            flags[at] = new KeyValuePair<string, string?>(key, value);
            return true;
        }

        flags.Add(new KeyValuePair<string, string?>(key, value));
        return false;
    }

    /// <summary>
    /// The script must really be this plugin's own: under the hook's own installed root or a sibling directory of it (the
    /// plugin-cache parent two versions share).
    /// </summary>
    public static bool ScriptUnderRoot(string? scriptPath, string? ownRoot, IFileReader files)
    {
        if (scriptPath is null || ownRoot is null)
        {
            return false;
        }

        string real;
        string root;
        try
        {
            real = files.RealPath(scriptPath);
            root = files.RealPath(ownRoot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        var name = NodePath.Basename(real);
        var isAh = IsAhBinary(real);
        var isAhd = IsAhdBinary(real);
        if (!isAh && !isAhd && ScriptOf(name) is null)
        {
            return false;
        }

        var realRoot = NodePath.Dirname(NodePath.Dirname(real));
        var expected = isAh ? NodePath.Join(realRoot, "bin", "ah") : isAhd ? NodePath.Join(realRoot, "bin", "ahd") : NodePath.Join(realRoot, "hooks", name);
        if (expected != real)
        {
            return false;
        }

        return realRoot == root || IsSiblingOfSamePlugin(realRoot, root, files);
    }

    /// <summary>
    /// Another directory of this same plugin: next to the own root, a directory, and holding a plugin manifest whose
    /// <c>name</c> equals, byte for byte, the own root's. Any failure to read or parse either manifest is a refusal.
    /// </summary>
    private static bool IsSiblingOfSamePlugin(string candidate, string ownRoot, IFileReader files)
    {
        if (NodePath.Dirname(candidate) != NodePath.Dirname(ownRoot))
        {
            return false;
        }

        try
        {
            if (files.Stat(candidate)?.Kind != FileKind.Directory)
            {
                return false;
            }

            var own = ManifestName(ownRoot, files);
            var other = ManifestName(candidate, files);
            return own is not null && other is not null && string.Equals(own, other, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string? ManifestName(string root, IFileReader files)
    {
        try
        {
            var manifest = JsonText.Parse(files.ReadText(NodePath.Join(root, ".claude-plugin", "plugin.json")));
            return manifest.Member("name") is { IsText: true } name ? name.AsString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A dismiss or disband with <c>--close</c>, or with <c>--ignore-release</c>, whose named form closes a member the release check holds, or with
    /// any flag given twice, which the verb and this grammar fold differently: a plan call is not one of these.
    /// </summary>
    public static bool IsCloseCommand(AhCommand? parsed) =>
        parsed is { Script: Roster } p && p.Verb is { } v && CloseVerbs.Contains(v) && (p.FlagIsTrue("close") || p.HasFlag("ignore-release") || p.Repeated);
}
