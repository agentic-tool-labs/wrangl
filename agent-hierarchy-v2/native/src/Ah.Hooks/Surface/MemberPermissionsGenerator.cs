using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Hooks.Surface;

/// <summary>
/// The one implementation of a member's launch-only rules (ah-simplify section C): a <c>permissions</c> object with an <c>allow</c> array only,
/// one list for every role, and the answer to "which rule would allow this command". Spawn, resume, <c>permissions show</c> and the
/// classifier-denied alarm all call it. It never writes a deny, ask or sandbox entry.
/// </summary>
internal static class MemberPermissionsGenerator
{
    /// <summary>Programs allowed with any arguments: git, the toolchain, and the shell building blocks.</summary>
    internal static readonly string[] Programs =
    [
        "git", "git -C",
        "dotnet", "node", "npm", "npx", "python3", "sh", "bash", "make",
        "ls", "cat", "head", "tail", "wc", "grep", "rg", "find", "sed", "awk", "sort", "uniq", "diff", "jq", "pwd", "echo", "printf", "test",
        "which", "env", "date", "mktemp", "cd",
        "cut", "tr", "mkdir", "cp", "mv", "touch", "true", "basename", "dirname",
        "kill", "pkill",
        "sleep", "ps", "pgrep",
    ];

    /// <summary>Cleanup of ah's own scratch: ahprobe sandboxes and temp dirs. A <c>*</c> matches any text, spaces included.</summary>
    internal static readonly string[] Cleanup =
    [
        "Bash(rm -rf /private/tmp/ahprobe.*)", "Bash(rm -rf /tmp/ahprobe.*)",
        "Bash(chmod -R u+w /private/tmp/ahprobe.*)", "Bash(chmod -R u+w /tmp/ahprobe.*)",
        "Bash(find /private/tmp -maxdepth 1 -name 'ahprobe*' *)",
        "Bash(rm -rf $TMPDIR/*)", "Bash(rm -rf /private/tmp/claude-*)", "Bash(rm -rf /var/folders/*)",
    ];

    internal static readonly string[] Tools = ["Read", "Edit", "Write", "Glob", "Grep", "NotebookEdit", "SendMessage"];

    /// <summary>The file tools that get a rule naming the member's own directories.</summary>
    internal static readonly string[] PathTools = ["Read", "Edit", "Write", "NotebookEdit"];

    internal static MemberPermissionsResult Generate(MemberPermissionsRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var bin = r.AhPath[..(r.AhPath.LastIndexOf('/') + 1)];
        var root = r.PluginRoot.TrimEnd('/');
        string[] ah = [r.AhPath, "ah", bin + "ahd", "ahd", root + "/bin/ah", root + "/tests/with-slot.sh"];
        // ah2-70: a member has full rights in the work tree it was given and in its own scratch directory. The bare tool
        // names above do not settle a path Claude Code treats as special (a work tree under .claude/, a scratch directory
        // under ~/.claude/): without a rule that names the path, auto mode sends each such call to its classifier, which
        // costs tens of seconds a call. The path rules name both directories outright.
        string[] owned = new[] { r.WorkRoot, r.MemberTmp ?? string.Empty }
            .Where(d => d.StartsWith('/') && d.Length > 1)
            .Select(d => d.TrimEnd('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var paths = owned.SelectMany(d => PathTools.Select(tool => $"{tool}(/{d}/**)"));
        var allow = ah.Concat(Programs).Select(p => $"Bash({p}:*)")
            .Concat(Cleanup)
            .Append("Bash(*)")
            .Concat(Tools)
            .Concat(paths)
            .Concat(r.UserAllow ?? [])
            .Distinct(StringComparer.Ordinal);
        var notes = (r.UserSettingsProblems ?? []).Select(p => $"settings file not read: {p}").ToList();
        var permissions = new JsonObject { ["allow"] = Strings(allow) };
        if (owned.Length > 0)
        {
            permissions["additionalDirectories"] = Strings(owned);
        }

        return new MemberPermissionsResult(permissions, notes);
    }

    /// <summary>The allow rule <see cref="Generate"/> renders that covers <paramref name="command"/>, or why none does.</summary>
    internal static MemberRuleAnswer RuleFor(MemberPermissionsRequest r, string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var argv = command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length == 0)
        {
            return new MemberRuleAnswer(null, "empty command");
        }

        var allow = Generate(r).Permissions["allow"].AsArray().Select(a => a.AsString());
        return allow.FirstOrDefault(rule => rule != "Bash(*)" && Covers(rule, argv)) is { } hit
            ? new MemberRuleAnswer(hit, null)
            : new MemberRuleAnswer(null, "no allow entry covers it");
    }

    internal static bool GlobMatches(string pattern, string word) =>
        System.Text.RegularExpressions.Regex.IsMatch(word, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>Whether a <c>Bash(prefix:*)</c> rule's prefix is a word-wise prefix of the command (the way Claude Code matches it).</summary>
    private static bool Covers(string rule, string[] argv)
    {
        if (!rule.StartsWith("Bash(", StringComparison.Ordinal) || !rule.EndsWith(":*)", StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = rule["Bash(".Length..^":*)".Length].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return prefix.Length <= argv.Length && prefix.SequenceEqual(argv.Take(prefix.Length), StringComparer.Ordinal);
    }

    private static JsonArray Strings(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }
}
