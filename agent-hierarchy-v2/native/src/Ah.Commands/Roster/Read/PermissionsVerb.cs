using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.DaemonClient;
using Ah.Mesh;

namespace Ah.Commands;

/// <summary>
/// <c>permissions &lt;check|add|sync|remove&gt;</c>: the allow rules for the Orchestrator's lifecycle commands in the user's Claude Code
/// settings, written only when the user asks for them from their own terminal (<c>install.sh</c>). It refuses inside any agent
/// session, and <c>add</c> and <c>sync</c> refuse unless this build proves its caller by process ancestry and holds members that
/// have not released their work.
/// </summary>
internal static class PermissionsVerb
{
    [Verb("roster:permissions")]
    internal static int Permissions(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private const string Usage = "permissions needs one action: check, rules, add, sync, remove, diagnose <member> or show <member>";

    private static int Run(RosterContext c)
    {
        if (c.Opts.Positionals.Count > 0 && c.Opts.Positionals[0] == "diagnose")
        {
            return c.Opts.Positionals.Count == 2
                ? PermissionDiagnose.Run(c, c.Opts.Positionals[1])
                : throw new RosterFailure("permissions diagnose needs one member name");
        }

        if (c.Opts.Positionals.Count > 0 && c.Opts.Positionals[0] == "show")
        {
            return c.Opts.Positionals.Count == 2
                ? PermissionShow.Run(c, c.Opts.Positionals[1])
                : throw new RosterFailure("permissions show needs one member name");
        }

        var action = c.Opts.Positionals.Count == 1 ? c.Opts.Positionals[0] : null;
        if (action is not ("check" or "rules" or "add" or "sync" or "remove"))
        {
            throw new RosterFailure(Usage);
        }

        if (action == "rules")
        {
            return LifecyclePermissions.Rules(c);
        }

        if (LifecyclePermissions.SessionRefusal(c) is { } session)
        {
            if (action == "check")
            {
                c.Stdout.WriteLine("skip: " + session);
                return 0;
            }

            throw new RosterFailure($"permissions {action}: refused: {session}");
        }

        return action switch
        {
            "check" => LifecyclePermissions.Check(c),
            "remove" => LifecyclePermissions.Remove(c),
            _ => LifecyclePermissions.Add(c, action == "sync"),
        };
    }
}

/// <summary>The rules, the ledger of what ah added, the settings file, and the doctor's reading of all three.</summary>
internal static class LifecyclePermissions
{
    /// <summary>The Orchestrator's lifecycle verbs the rules cover. <c>snapshot</c> is not owner-gated by ah; it only writes a snapshot file.</summary>
    internal static readonly string[] Verbs = ["disband", "dismiss", "resume", "snapshot", "spawn-one", "stream-open", "stream-done", "progress", "create", "spawn-ad-hoc", "adopt", "untrack", "stream-report", "close-approve", "checkpoint", "wake", "overdue", "stall-diagnose", "checklist", "move"];

    private const string BackupSuffix = ".ah2-bak";

    internal static string ClaudeDir(IAhEnvironment env) => Ah.Core.CoreModule.ClaudeConfigDir(env.GetVariable("CLAUDE_CONFIG_DIR"), env.HomeDirectory);

    /// <summary>The plugin manager's install record, in the same Claude directory as the settings file.</summary>
    internal static string InstalledPluginsPath(IAhEnvironment env) => NodePath.Join(ClaudeDir(env), "plugins", "installed_plugins.json");

    internal static string SettingsPath(IAhEnvironment env) => NodePath.Join(ClaudeDir(env), "settings.json");

    /// <summary>The <c>meta</c> key of the ledger of rules ah added (b397 group 2; was <c>&lt;AH_HOME&gt;/permissions.json</c>).</summary>
    internal const string LedgerKey = "home.permissions";

    /// <summary>The prefix every rule carries: the exact command the Orchestrator is told to run for the roster CLI.</summary>
    internal static string Prefix(RosterContext c)
    {
        var root = c.Services.Config.PluginRoot;
        var real = RealPathOrNull(c, root);
        if (c.Services.Config.StableAhPluginDir() is not null || real is null || real == root)
        {
            return c.Services.Config.CliCommand(CliTool.Roster);
        }

        var exe = c.Services.Config.AhCommandPath(root: real);
        return (exe.All(IsPlainPathChar) ? exe : $"\"{exe}\"") + " roster";
    }

    private static bool IsPlainPathChar(char ch) => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '/' or '@' or ':' or '+' or '-';

    private static string? RealPathOrNull(RosterContext c, string path)
    {
        try
        {
            return c.Services.Files.RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private const uint GroupOrOtherWritable = 0x12;

    /// <summary>
    /// Set when the plugin root is a link: the rules carry the real path, but the Orchestrator is told to run the CLI by the root as
    /// configured, so the rules do not match what it runs.
    /// </summary>
    internal static string? LinkWarning(RosterContext c)
    {
        var root = c.Services.Config.PluginRoot;
        return RealPathOrNull(c, root) is { } real && real != root
            ? $"the plugin root {root} is a link to {real}: the rules name {real}, so they do not match the CLI path the Orchestrator is told to run ({root})"
            : null;
    }

    /// <summary>
    /// Why this plugin root may not be named in an allow rule, or null when it may. The rule auto-approves everything under
    /// <c>&lt;root&gt;/bin/ah</c>, so the root must be a version the plugin manager installed, spelled without characters a rule
    /// reads as syntax, owned like the user's home, writable by nobody else, and outside any git work tree.
    /// </summary>
    /// <param name="c">The roster context.</param>
    /// <param name="action">The permissions action the refusal tells the user to run from the installed copy.</param>
    internal static string? RootRefusal(RosterContext c, string action)
    {
        var files = c.Services.Files;
        var root = c.Services.Config.PluginRoot;
        if (RealPathOrNull(c, root) is not { } real)
        {
            return $"{root} cannot be resolved";
        }

        if (real.IndexOfAny(['*', '(', ')', '\n', '\r']) >= 0)
        {
            return $"{real} has a character an allow rule would read as syntax (* ( ) or a newline)";
        }

        var installed = InstalledCopies(c);
        if (!installed.Any(i => i.Root == real))
        {
            var notInstalled = $"{real} is not an installed version of {c.Services.Config.PluginName} in the plugin manager's record";
            if (Newest(installed) is not { } newest)
            {
                return $"{notInstalled}, and no version of {c.Services.Config.PluginName} is installed";
            }

            var exe = c.Services.Config.AhCommandPath(root: newest);
            return $"{notInstalled}; the installed copy is {newest}: run {(exe.All(IsPlainPathChar) ? exe : $"\"{exe}\"")} roster permissions {action}";
        }

        var cache = RealPathOrNull(c, NodePath.Join(ClaudeDir(c.Services.Environment), "plugins", "cache"));
        if (cache is null || !real.StartsWith(cache + "/", StringComparison.Ordinal))
        {
            return $"{real} is not under the plugin cache directory {cache ?? "(missing)"}";
        }

        if (c.Services.CurrentUserId is not { } me)
        {
            return "the user running this command cannot be determined, so ownership of the plugin directories cannot be checked";
        }

        string? Untrusted(string path) =>
            files.Stat(path) is not { } stat ? $"{path} does not exist"
            : stat.Uid != me ? $"{path} is not owned by you"
            : (stat.Mode & GroupOrOtherWritable) != 0 ? $"{path} is writable by its group or others"
            : null;

        foreach (var path in new[] { real, real + "/bin", real + "/bin/ah" })
        {
            if (Untrusted(path) is { } why)
            {
                return why;
            }
        }

        for (var dir = Path.GetDirectoryName(real); dir is not null && dir.Length >= cache.Length; dir = Path.GetDirectoryName(dir))
        {
            if (Untrusted(dir) is { } why)
            {
                return why;
            }
        }

        for (var dir = real; dir.Length > 1; dir = Path.GetDirectoryName(dir) ?? "/")
        {
            if (files.Stat(dir + "/.git") is not null)
            {
                return $"{real} is inside a git work tree ({dir})";
            }
        }

        return null;
    }

    /// <summary>
    /// The installed copy to name in a refusal: the newest by the record's <c>lastUpdated</c>, else by the highest
    /// <c>version</c>; null when nothing is installed.
    /// </summary>
    private static string? Newest(List<(string Root, DateTimeOffset? LastUpdated, string? Version)> installed) =>
        installed.Count == 0 ? null
        : installed.Aggregate((best, next) =>
            (next.LastUpdated, best.LastUpdated) switch
            {
                ({ } n, { } b) => n > b ? next : best,
                ({ }, null) => next,
                (null, { }) => best,
                _ => DaemonClientModule.CompareVersions(next.Version ?? "0", best.Version ?? "0") > 0 ? next : best,
            }).Root;

    /// <summary>
    /// The real path of every <c>installPath</c> the plugin manager records for this plugin, with its <c>lastUpdated</c> and
    /// <c>version</c> when present; empty when the record is missing or unreadable.
    /// </summary>
    private static List<(string Root, DateTimeOffset? LastUpdated, string? Version)> InstalledCopies(RosterContext c)
    {
        var roots = new List<(string Root, DateTimeOffset? LastUpdated, string? Version)>();
        var record = InstalledPluginsPath(c.Services.Environment);
        try
        {
            if (!JsonText.TryParse(Encoding.UTF8.GetBytes(c.Services.Files.ReadText(record)), out var doc)
                || !(doc.IsMap && doc.Member("plugins") is { IsMap: true } plugins))
            {
                return roots;
            }

            var prefix = c.Services.Config.PluginName + "@";
            foreach (var key in plugins.AsObject().Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var value = plugins.AsObject()[key];
                var entries = value.IsArray ? value.AsArray().ToList() : [value];
                foreach (var entry in entries)
                {
                    if (entry.IsMap && entry.Member("installPath") is { IsText: true } install && RealPathOrNull(c, install.AsString()) is { } real)
                    {
                        roots.Add((
                            real,
                            entry.Member("lastUpdated") is { IsText: true } updated
                                && DateTimeOffset.TryParse(updated.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null,
                            entry.Member("version") is { IsText: true } version ? version.AsString() : null));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return roots;
        }

        return roots;
    }

    internal static string Rule(string prefix, string verb) => $"Bash({prefix} {verb}:*)";

    private const string AllAhKey = "*ah";
    private const string SendMessageRule = "SendMessage";
    private const string CdKey = "cd";
    private const string CdRule = "Bash(cd:*)";

    /// <summary>
    /// Every rule <c>add</c> writes, keyed so <c>sync</c> can replace an older path's rule: one per lifecycle verb, one for all of ah
    /// (the user's call, 2026-10-28: the Orchestrator's ah calls skip the auto-mode classifier), and SendMessage.
    /// </summary>
    internal static Dictionary<string, string> CurrentRules(RosterContext c)
    {
        var prefix = Prefix(c);
        var rules = Verbs.ToDictionary(v => v, v => Rule(prefix, v), StringComparer.Ordinal);
        rules[AllAhKey] = $"Bash({prefix[..^" roster".Length]}:*)";
        rules[SendMessageRule] = SendMessageRule;
        rules[CdKey] = CdRule;
        return rules;
    }

    private static string? KeyOf(string rule) =>
        rule == SendMessageRule ? SendMessageRule
        : rule == CdRule ? CdKey
        : VerbOf(rule) is { } verb ? verb
        : rule.StartsWith("Bash(", StringComparison.Ordinal) && (rule.EndsWith("/ah:*)", StringComparison.Ordinal) || rule.EndsWith("/ah\":*)", StringComparison.Ordinal)) ? AllAhKey
        : null;

    /// <summary>
    /// Why this process may not touch the settings: the environment says an agent session started it, or the process ancestry holds
    /// a harness session or cannot be read. Null when it may. A raised bar, not a security boundary: an agent that can run shell
    /// commands can edit the settings file itself.
    /// </summary>
    internal static string? SessionRefusal(RosterContext c)
    {
        var env = c.Services.Environment;
        if (env.GetVariable("AH_SPAWN_DEPTH") is not null)
        {
            return "this command was started by an ah agent session";
        }

        if (env.TestMode)
        {
            return null;
        }

        const string PlainTerminal = "run install.sh --apply --allow-lifecycle from a plain terminal, or add the rules by hand";
        if (env.GetVariable("CLAUDECODE") is not null || env.GetVariable("CLAUDE_CODE_SESSION_ID") is not null)
        {
            return "this command was started from a Claude Code session; " + PlainTerminal;
        }

        return DoctorVerb.AncestorKind(c) switch
        {
            NearestKind.None => null,
            null or NearestKind.Unavailable => "the process ancestry cannot be read, so a harness session cannot be ruled out; " + PlainTerminal,
            _ => "a Claude Code session is among this command's parent processes; " + PlainTerminal,
        };
    }

    /// <summary>
    /// Why the rules may not be offered: the caller is not proved by process ancestry, or the build has no release handshake
    /// row. A missing row is an older build, and the rules are not offered there. Null when both hold.
    /// </summary>
    internal static string? PreconditionRefusal(DoctorVerb.Check ownerProof, DoctorVerb.Check? releaseHandshake)
    {
        if (ownerProof is not { Status: "ok", Detail: "ancestry" })
        {
            return $"the owner-proof doctor row is {ownerProof.Status} ({ownerProof.Detail}), not ok (ancestry)";
        }

        return releaseHandshake switch
        {
            null => "this build has no release-handshake doctor row",
            { Status: "ok" } => null,
            _ => $"the release-handshake doctor row is {releaseHandshake.Status} ({releaseHandshake.Detail}), not ok",
        };
    }

    private static string? PreconditionRefusal(RosterContext c, string action) =>
        PreconditionRefusal(DoctorVerb.OwnerProofRow(c), DoctorVerb.ReleaseHandshakeRow()) ?? RootRefusal(c, action);

    // ---------------------------------------------------------------- the ledger

    private sealed record Ledger(string Answer, string PluginRoot, List<string> Added);

    private static Ledger? ReadLedger(RosterContext c, out string? problem)
    {
        problem = null;
        string? text = null;
        if (c.Services.HomeDocs is { } docs && !docs.TryRead(LedgerKey, out text))
        {
            // fail closed: a write built on a ledger read as absent would forget the rules ah added before
            problem = $"the {LedgerKey} ledger cannot be read from the state database";
            return null;
        }

        if (text is null)
        {
            return null;
        }

        if (!JsonText.TryParse(Encoding.UTF8.GetBytes(text), out var doc) || !doc.IsMap)
        {
            problem = $"the {LedgerKey} ledger is not a JSON object";
            return null;
        }

        var added = doc.Member("added") is { IsArray: true } list ? list.AsArray().Where(v => v.IsText).Select(v => v.AsString()).ToList() : [];
        return new Ledger(
            doc.Member("answer") is { IsText: true } answer ? answer.AsString() : string.Empty,
            doc.Member("plugin_root") is { IsText: true } root ? root.AsString() : string.Empty,
            added);
    }

    private static void WriteLedger(RosterContext c, string answer, IEnumerable<string> added)
    {
        var rules = new JsonArray();
        rules.AddRange(added.Select(r => (JsonValue)r));
        var ledger = new JsonObject
        {
            ["v"] = 1,
            ["added"] = rules,
            ["plugin_root"] = c.Services.Config.PluginRoot,
            ["at"] = c.Services.Clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["answer"] = answer,
        };
        var docs = c.Services.HomeDocs ?? throw new RosterFailure("permissions: there is no state database to record the ledger in");
        try
        {
            docs.Write(LedgerKey, JsonText.Stringify(ledger, 0));
        }
        catch (InvalidOperationException e)
        {
            throw new RosterFailure($"permissions: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- the settings file

    private sealed class SettingsFile(string path, JsonObject root, List<JsonValue> allow, bool existed)
    {
        internal string Path { get; } = path;

        internal JsonObject Root { get; } = root;

        internal List<JsonValue> Allow { get; } = allow;

        internal bool Existed { get; } = existed;

        internal bool Has(string rule) => Allow.Any(v => v.IsText && v.AsString() == rule);

        internal int IndexOf(string rule) => Allow.FindIndex(v => v.IsText && v.AsString() == rule);
    }

    /// <summary>Reads the settings file: a missing one is empty; one that is not a JSON object, or whose <c>permissions.allow</c> is not a list, is a problem and is never rewritten.</summary>
    private static SettingsFile? ReadSettings(RosterContext c, out string? problem)
    {
        problem = null;
        var path = SettingsPath(c.Services.Environment);
        if (c.Services.Files.Stat(path, followSymlinks: false) is { Kind: FileKind.Symlink })
        {
            var target = RealPathOrNull(c, path);
            if (target is null || c.Services.Files.Stat(target) is not { Kind: FileKind.File })
            {
                problem = $"{path} is a link to something that is not a readable file; nothing was changed";
                return null;
            }

            path = target;
        }

        if (!c.Services.Files.Exists(path))
        {
            return new SettingsFile(path, new JsonObject(), [], existed: false);
        }

        try
        {
            if (!JsonText.TryParse(Encoding.UTF8.GetBytes(c.Services.Files.ReadText(path)), out var doc) || !doc.IsMap)
            {
                problem = $"{path} is not a JSON object (a file with comments is not read)";
                return null;
            }

            var root = doc.AsObject();
            var allow = new List<JsonValue>();
            if (root.Get("permissions") is { } permissions)
            {
                if (!permissions.IsMap)
                {
                    problem = $"{path}: \"permissions\" is not an object";
                    return null;
                }

                if (permissions.Member("allow") is { } list)
                {
                    if (!list.IsArray)
                    {
                        problem = $"{path}: \"permissions.allow\" is not a list";
                        return null;
                    }

                    allow.AddRange(list.AsArray());
                }
            }

            return new SettingsFile(path, root, allow, existed: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = $"{path} cannot be read: {e.Message}";
            return null;
        }
    }

    /// <summary>Writes the settings back as canonical JSON, keeping the previous file as <c>settings.json.ah2-bak</c>. Returns the backup path, or null when there was nothing to back up.</summary>
    private static string? WriteSettings(SettingsFile file)
    {
        string? backup = null;
        if (file.Existed)
        {
            backup = file.Path + BackupSuffix;
            File.Copy(file.Path, backup, overwrite: true);
        }

        var list = new JsonArray();
        list.AddRange(file.Allow);
        var permissions = file.Root.Get("permissions") is { IsMap: true } existing ? existing.AsObject() : new JsonObject();
        permissions["allow"] = list;
        file.Root["permissions"] = permissions;
        LevelFiles.WriteAtomically(file.Path, JsonText.Stringify(file.Root, 2) + "\n");
        return backup;
    }

    private static string? VerbOf(string rule) => Verbs.FirstOrDefault(v => rule.EndsWith($" {v}:*)", StringComparison.Ordinal));

    // ---------------------------------------------------------------- the actions

    /// <summary>Prints the rules <c>add</c> would write for this version, one per line, exactly as they would appear in the settings file.</summary>
    internal static int Rules(RosterContext c)
    {
        foreach (var rule in CurrentRules(c).Values)
        {
            c.Stdout.WriteLine(rule);
        }

        return 0;
    }

    /// <summary>
    /// Prints one word for <c>install.sh</c>: <c>ask</c> (rules may be offered and the user has not answered), <c>sync</c> (the user
    /// said yes before; refresh the rules for this version), or <c>skip: &lt;why&gt;</c>.
    /// </summary>
    internal static int Check(RosterContext c)
    {
        if (PreconditionRefusal(c, "sync") is { } refusal)
        {
            c.Stdout.WriteLine("skip: " + refusal);
            return 0;
        }

        var ledger = ReadLedger(c, out var problem);
        if (problem is not null)
        {
            c.Stdout.WriteLine("skip: " + problem);
            return 0;
        }

        c.Stdout.WriteLine(ledger?.Answer switch
        {
            "yes" => "sync",
            "no" => "skip: the user answered no",
            _ => "ask",
        });
        return 0;
    }

    /// <summary>
    /// <c>add</c>: the user said yes; append every rule that is missing. <c>sync</c>: refresh the rules a previous yes recorded for
    /// this version's path, adding none. A recorded rule that is no longer in the file exactly as recorded is left alone and listed.
    /// </summary>
    internal static int Add(RosterContext c, bool syncOnly)
    {
        var name = syncOnly ? "sync" : "add";
        if (PreconditionRefusal(c, name) is { } refusal)
        {
            throw new RosterFailure($"permissions {name}: refused: {refusal}");
        }

        var ledger = ReadLedger(c, out var ledgerProblem);
        if (ledgerProblem is not null)
        {
            throw new RosterFailure($"permissions {name}: {ledgerProblem}");
        }

        if (syncOnly && ledger is not { Answer: "yes" })
        {
            return Report(c, name, [], [], [], null, changed: false);
        }

        var file = ReadSettings(c, out var problem) ?? throw new RosterFailure($"permissions {name}: {problem}; nothing was changed");
        var rules = CurrentRules(c);
        var added = new List<string>();
        var removed = new List<string>();
        var left = new List<string>();
        var owned = new List<string>();
        foreach (var old in ledger is { Answer: "yes" } ? ledger.Added : [])
        {
            var key = KeyOf(old);
            var index = file.IndexOf(old);
            if (key is null || index < 0)
            {
                left.Add(old);
                continue;
            }

            var current = rules[key];
            if (old == current)
            {
                owned.Add(old);
                continue;
            }

            if (file.Has(current))
            {
                file.Allow.RemoveAt(index);
            }
            else
            {
                file.Allow[index] = current;
                added.Add(current);
                owned.Add(current);
            }

            removed.Add(old);
        }

        if (!syncOnly)
        {
            foreach (var rule in rules.Values)
            {
                if (!file.Has(rule))
                {
                    file.Allow.Add(rule);
                    added.Add(rule);
                    owned.Add(rule);
                }
            }
        }

        var changed = added.Count > 0 || removed.Count > 0;
        var backup = changed ? WriteSettings(file) : null;
        WriteLedger(c, "yes", owned.Distinct(StringComparer.Ordinal));
        var unused = syncOnly ? rules.Where(kv => !file.Has(kv.Value)).Select(kv => kv.Key == AllAhKey ? "all of ah" : kv.Key).ToList() : [];
        return Report(c, name, added, removed, left, backup, changed, unused.Count == 0 ? [] : [NewRulesNotice(unused)]);
    }

    /// <summary>Removes exactly the rules the ledger says ah added, if they are still in the file, and records that the user said no.</summary>
    internal static int Remove(RosterContext c)
    {
        var ledger = ReadLedger(c, out var ledgerProblem);
        if (ledgerProblem is not null)
        {
            throw new RosterFailure($"permissions remove: {ledgerProblem}");
        }

        var file = ReadSettings(c, out var problem) ?? throw new RosterFailure($"permissions remove: {problem}; nothing was changed");
        var removed = new List<string>();
        foreach (var rule in ledger?.Added ?? [])
        {
            if (file.IndexOf(rule) is var index and >= 0)
            {
                file.Allow.RemoveAt(index);
                removed.Add(rule);
            }
        }

        var backup = removed.Count > 0 ? WriteSettings(file) : null;
        WriteLedger(c, "no", []);
        return Report(c, "remove", [], removed, [], backup, removed.Count > 0);
    }

    private static string NewRulesNotice(List<string> verbs) =>
        $"{verbs.Count} new lifecycle rules available ({string.Join(", ", verbs)}); add them with ah roster permissions add or install.sh --allow-lifecycle";

    private static int Report(RosterContext c, string action, List<string> added, List<string> removed, List<string> left, string? backup, bool changed, List<string>? notices = null)
    {
        JsonArray Texts(IEnumerable<string> items)
        {
            var array = new JsonArray();
            array.AddRange(items.Select(i => (JsonValue)i));
            return array;
        }

        c.Out(new JsonObject
        {
            ["action"] = action,
            ["changed"] = changed,
            ["settings"] = SettingsPath(c.Services.Environment),
            ["ledger"] = LedgerKey,
            ["added"] = Texts(added),
            ["removed"] = Texts(removed),
            ["left"] = Texts(left),
            ["backup"] = backup is null ? JsonValue.Null : backup,
            ["warnings"] = Texts(action == "remove" || LinkWarning(c) is not { } warning ? [] : [warning]),
            ["notices"] = Texts(notices ?? []),
        });
        return 0;
    }

    // ---------------------------------------------------------------- doctor

    /// <summary>
    /// <c>ok</c> when the user was never asked, said no, or every recorded rule is present for this version's path; <c>warn</c> when a
    /// recorded rule is missing, the rules are for an older path, or rules for this path exist that the ledger does not list.
    /// Reads only; it never removes anything.
    /// </summary>
    internal static DoctorVerb.Check DoctorRow(RosterContext c)
    {
        var ledger = ReadLedger(c, out var ledgerProblem);
        if (ledgerProblem is not null)
        {
            return new DoctorVerb.Check("warn", ledgerProblem);
        }

        if (ledger is not { Answer: "yes" })
        {
            return new DoctorVerb.Check("ok", ledger is null ? "not asked" : $"answer {ledger.Answer}");
        }

        var file = ReadSettings(c, out var problem);
        if (file is null)
        {
            return new DoctorVerb.Check("warn", problem ?? "settings unreadable");
        }

        const string Fix = "run install.sh --apply --allow-lifecycle";
        var root = c.Services.Config.PluginRoot;
        if (ledger.PluginRoot != root)
        {
            return new DoctorVerb.Check("warn", $"the rules are for {ledger.PluginRoot}, not this version's {root}: {Fix}");
        }

        var problems = new List<string>();
        if (LinkWarning(c) is { } link)
        {
            problems.Add(link);
        }

        var missing = ledger.Added.Where(r => !file.Has(r)).ToList();
        if (missing.Count > 0)
        {
            problems.Add($"missing from settings: {string.Join(", ", missing)}: {Fix}");
        }

        var rules = CurrentRules(c).Values.ToList();
        var extra = rules.Where(r => file.Has(r) && !ledger.Added.Contains(r)).ToList();
        if (extra.Count > 0)
        {
            problems.Add($"rules for this version that ah did not add (left alone): {string.Join(", ", extra)}");
        }

        var present = rules.Count(file.Has);
        var count = $"lifecycle rules: {present}/{rules.Count}";
        return problems.Count > 0
            ? new DoctorVerb.Check("warn", count + "; " + string.Join("; ", problems))
            : new DoctorVerb.Check("ok", $"{count}; {ledger.Added.Count} rule(s) present");
    }
}
