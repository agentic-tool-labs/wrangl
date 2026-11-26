using System.Security.Cryptography;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>roster kit export|import</c>: one named roster packed into a single JSON file (spec roster-kit.md, adr-43): its block, the rows
/// and agent texts of its custom roles, and references to its pack roles.
/// </summary>
internal static partial class RosterKitVerb
{
    /// <summary>A plugin, marketplace or role name safe to print inside a shell command: no metacharacter, quote or space.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PlainName();

    /// <summary>K1: a kit is at most 1 MiB.</summary>
    internal const int MaxBytes = 1 << 20;

    private const string Usage =
        "usage: ah roster kit export <roster> --out <path> [--level L] [--force] | kit import <file> [--level global|repo|repo-user] [--agents user|repo] [--as <roster>] (--dry-run | --pin sha256:…)  (all with --cwd <abs cwd>)";

    private static readonly Dictionary<string, string[]> SubFlags = new(StringComparer.Ordinal)
    {
        ["export"] = ["out", "level", "force", "cwd"],
        ["import"] = ["level", "agents", "as", "dry-run", "pin", "cwd"],
    };

    // K1 and K1a: every key a kit may hold. Any other key is a finding.
    private static readonly string[] TopKeys = ["kit", "roster", "roles", "rules"];
    private static readonly string[] RosterKeys = ["name", "block"];
    private static readonly string[] BlockKeys = ["route", "members", "max_lanes", "orchestrator_model"];
    private static readonly string[] MemberKeys = ["role", "model", "effort", "route", "onMissing"];
    private static readonly string[] EntryKeys = ["row", "agent"];
    private static readonly string[] AgentKeys = ["file", "text"];
    private static readonly string[] RowKeys = ["class", "agent", "label", "description", "routes", "model", "dispatch", "shell"];
    private static readonly string[] FromRowKeys = ["from", "pin", "label", "description", "routes", "model", "dispatch"];
    private static readonly string[] RuleKeys = ["key", "text"];
    private static readonly string[] FromParts = ["plugin", "marketplace", "role"];

    private static readonly Dictionary<string, string> RefusedMemberKeys = new(StringComparer.Ordinal)
    {
        ["command"] = "a raw shell line sent to a tmux pane",
        ["args"] = "appended verbatim to the harness launch",
        ["kind"] = "a non-claude harness, outside a kit's scope",
        ["harness"] = "a non-claude harness, outside a kit's scope",
        ["adapter"] = "a non-claude harness, outside a kit's scope",
        ["autoMode"] = "becomes --permission-mode, a permission setting",
        ["auto_mode"] = "becomes --permission-mode, a permission setting",
    };

    private static readonly Dictionary<string, string> RefusedRowKeys = new(StringComparer.Ordinal)
    {
        ["peer"] = "a peer row is a link the user makes locally",
    };

    [Verb("roster:kit")]
    internal static int Kit(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        var words = c.Opts.Positionals;
        var sub = words.Count > 0 ? words[0] : null;
        if (sub is null || !SubFlags.TryGetValue(sub, out var allowed))
        {
            throw new RosterFailure(Usage);
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!allowed.Contains(key))
            {
                throw new RosterFailure($"kit {sub}: unrecognized flag --{key} (use {string.Join(", ", allowed.Select(k => "--" + k))})");
            }
        }

        var files = new LevelFiles(c);
        var arg = words.Count > 1 ? words[1] : null;
        return sub == "export" ? Export(c, files, arg) : Import(c, files, arg);
    }

    private static string T(JsonValue? v) => MemberPlanning.JsConvertText(v);

    private static JsonObject? Obj(JsonValue? v) => v is { IsMap: true } m ? m.AsObject() : null;

    /// <summary>A value with every object's keys sorted, so two rows compare equal whatever order their keys were written in.</summary>
    private static string Canonical(JsonValue v)
    {
        if (v.IsMap)
        {
            var o = v.AsObject();
            return "{" + string.Join(",", o.Keys.Order(StringComparer.Ordinal).Select(k => JsonText.Quote(k) + ":" + Canonical(o[k]))) + "}";
        }

        return v.IsArray ? "[" + string.Join(",", v.AsArray().Select(Canonical)) + "]" : JsonText.Stringify(v, 0);
    }

    private static bool Same(JsonObject a, JsonObject b) => Canonical(JsonValue.FromObject(a)) == Canonical(JsonValue.FromObject(b));

    private static JsonObject Without(JsonObject row, string key)
    {
        var copy = row.Clone();
        copy.Remove(key);
        return copy;
    }

    internal static string PinOf(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ---------------------------------------------------------------- export (K2)

    private static int Export(RosterContext c, LevelFiles files, string? name)
    {
        var config = c.Services.Config;
        var outArg = c.Opts.Text("out");
        if (string.IsNullOrEmpty(name) || outArg is null)
        {
            throw new RosterFailure(Usage);
        }

        var key = name == config.DefaultRoster ? null : name;
        if (key is not null && !(config.ValidateTeamAlias(JsonValue.FromString(key)).Get("ok") is { IsTruthy: true }))
        {
            throw new RosterFailure($"kit export: {LevelFiles.Quote(name)} is not a roster name");
        }

        var level = c.Opts.Has("level") ? files.RequireLevel(c.Opts.Text("level") ?? "true") : null;
        var defs = NamedRostersVerb.Definitions(c, key);
        var win = NamedRostersVerb.Winning(level is null ? defs : defs.Where(d => d.Level == level).ToList());
        if (win is null || !win.Block.IsMap)
        {
            throw new RosterFailure($"kit export: roster \"{name}\" is not defined {(level is null ? "at any level" : $"at level \"{level}\"")}");
        }

        var roles = new JsonObject();
        foreach (var member in win.Members)
        {
            if (member.Member("role") is not { IsText: true } r || roles.ContainsKey(r.AsString()))
            {
                continue;
            }

            if (KitRole(c, files, r.AsString()) is { } entry)
            {
                roles[r.AsString()] = entry;
            }
        }

        var kit = new JsonObject
        {
            ["kit"] = 1,
            ["roster"] = new JsonObject { ["name"] = name, ["block"] = win.Block.AsObject().Clone() },
            ["roles"] = roles,
        };
        var bytes = new System.Text.UTF8Encoding(false).GetBytes(JsonText.Stringify(JsonValue.FromObject(kit), 2) + "\n");
        if (bytes.Length > MaxBytes)
        {
            throw new RosterFailure($"kit export: the kit is {bytes.Length} bytes, over the 1 MiB limit — nothing was written");
        }

        var path = Path.GetFullPath(outArg, c.Cwd);
        if (c.Opts.IsTrue("force"))
        {
            LevelFiles.WriteAtomically(path, bytes);
        }
        else if (!LevelFiles.WriteNew(path, System.Text.Encoding.UTF8.GetString(bytes)))
        {
            throw new RosterFailure($"kit export: {path} exists — pass --force to replace it; nothing was written");
        }

        c.Stdout.Write($"wrote {path} (roster \"{name}\" from level \"{win.Level}\")\n");
        c.Stdout.Write(Preview(c, files, kit, PinOf(bytes), "global", "user", null).Text);
        return 0;
    }

    // ---------------------------------------------------------------- import (K3 dry run, K4, K5, K6)

    private static int Import(RosterContext c, LevelFiles files, string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            throw new RosterFailure(Usage);
        }

        foreach (var k in new[] { "as", "agents", "pin" })
        {
            if (c.Opts.Has(k) && c.Opts.Text(k) is null)
            {
                throw new RosterFailure($"kit import: --{k} needs a value");
            }
        }

        var dry = c.Opts.IsTrue("dry-run");
        var pinArg = c.Opts.Text("pin");
        if (!dry && pinArg is null)
        {
            throw new RosterFailure("kit import: run it with --dry-run first, then commit with --pin <the pin the dry run printed>; nothing was written");
        }

        // K3: a commit is refused where a trust commit is, and by the owner guard, before anything is read or written.
        if (!dry)
        {
            RoleVerbs.RefuseTrustCommitHere(c, "kit import");
            OwnerGuard.RefuseAgentRoleWrite(c, "import", "kit");
        }

        var level = c.Opts.Has("level") ? files.RequireLevel(c.Opts.Text("level") ?? "true") : "global";
        var agents = c.Opts.Text("agents") ?? "user";
        if (agents is not ("user" or "repo"))
        {
            throw new RosterFailure($"kit import: --agents must be user or repo, got {LevelFiles.Quote(agents)}");
        }

        var path = Path.GetFullPath(file, c.Cwd);
        if (c.Services.Files.Stat(path) is not { Kind: FileKind.File } stat)
        {
            throw new RosterFailure($"kit import: {path} is not a regular file");
        }

        if (stat.Size > MaxBytes)
        {
            throw new RosterFailure($"kit import: {path} is over the 1 MiB limit");
        }

        // The read stops one byte past the limit, so a file that grows after the stat is still refused, not read whole.
        var buffer = new byte[MaxBytes + 1];
        var filled = 0;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            int n;
            while (filled < buffer.Length && (n = stream.Read(buffer, filled, buffer.Length - filled)) > 0)
            {
                filled += n;
            }
        }

        if (filled > MaxBytes)
        {
            throw new RosterFailure($"kit import: {path} is over the 1 MiB limit");
        }

        var bytes = buffer[..filled];

        JsonValue parsed;
        try
        {
            parsed = JsonText.Parse(bytes);
        }
        catch (JsonParseException e)
        {
            throw new RosterFailure($"kit import: {path} is not valid JSON ({e.Message})");
        }

        if (Obj(parsed) is not { } kit)
        {
            throw new RosterFailure($"kit import: {path} is not a JSON object");
        }

        var pin = PinOf(bytes);
        var plan = Preview(c, files, kit, pin, level, agents, c.Opts.Text("as"));
        if (dry)
        {
            c.Stdout.Write(plan.Text);
            return 0;
        }

        if (pinArg != pin)
        {
            throw new RosterFailure($"kit import: --pin {pinArg} doesn't match this kit ({pin}) — nothing was written. Run --dry-run again and review what changed");
        }

        if (plan.Findings + plan.Conflicts > 0)
        {
            c.Stdout.Write(plan.Text);
            throw new RosterFailure($"kit import: refused while {plan.Findings} finding(s) and {plan.Conflicts} conflict(s) stand (listed above) — nothing was written");
        }

        return Commit(c, files, plan);
    }

    /// <summary>What a dry run found: its listing, what stands against a commit, and the writes a commit makes (the items listed <c>new</c>).</summary>
    internal sealed record KitPlan(
        string Text,
        int Findings,
        int Conflicts,
        string LevelPath,
        string? RosterName,
        JsonObject? RosterBlock,
        List<(string Role, JsonObject Row)> Roles,
        List<(string Path, string Text)> Agents);

    /// <summary>
    /// K8 order and K9: every agent file first, each created through a temporary file and a rename that never replaces one; then the level
    /// file once, through the writer <c>role set</c> and <c>roster copy</c> use. Nothing else is written and nothing is run (K7). Only an IO
    /// failure or a key that appeared since the plan stops it, and then the agent files this run created are removed again (C2, C4).
    /// </summary>
    private static int Commit(RosterContext c, LevelFiles files, KitPlan plan)
    {
        var written = new List<string>();
        var created = new List<string>();
        try
        {
            foreach (var (path, text) in plan.Agents)
            {
                if (!LevelFiles.WriteNew(path, text))
                {
                    throw new IOException($"{path} appeared after the dry run's check");
                }

                created.Add(path);
                written.Add(path);
            }

            if (plan.RosterName is not null || plan.Roles.Count > 0)
            {
                var data = files.Read(plan.LevelPath);

                // C2: a planned new key that appeared since the plan refuses the commit; nothing is overwritten.
                var appeared = new List<string>();
                if (plan.RosterName is not null && Obj(data.Get("rosters"))?.ContainsKey(plan.RosterName) == true)
                {
                    appeared.Add($"rosters.{plan.RosterName}");
                }

                appeared.AddRange(plan.Roles.Where(r => Obj(data.Get("roles"))?.ContainsKey(r.Role) == true).Select(r => $"roles.{r.Role}"));
                if (appeared.Count > 0)
                {
                    throw new IOException($"{string.Join(", ", appeared)} appeared in {plan.LevelPath} since the dry run");
                }

                if (!(data.Get("version") is { IsTruthy: true }))
                {
                    data["version"] = c.Services.Config.ConfigVersion;
                }

                if (plan.RosterName is not null)
                {
                    files.Install(data, plan.RosterName, plan.RosterBlock!.Clone());
                }

                if (plan.Roles.Count > 0)
                {
                    var roles = Obj(data.Get("roles")) ?? new JsonObject();
                    foreach (var (role, row) in plan.Roles)
                    {
                        roles[role] = row.Clone();
                    }

                    data["roles"] = roles;
                }

                files.Write(plan.LevelPath, data);
                written.Add(plan.LevelPath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RosterFailure)
        {
            // C4: the level file was not written, so the agent files this run created (create-only, so nothing of the user's) are removed.
            var removed = new List<string>();
            var left = new List<string>();
            foreach (var path in created)
            {
                try
                {
                    File.Delete(path);
                    removed.Add(path);
                }
                catch (Exception de) when (de is IOException or UnauthorizedAccessException)
                {
                    left.Add(path);
                }
            }

            var undo = removed.Count == 0 ? string.Empty : $"; removed the agent file(s) this run had written: {string.Join(", ", removed)}";
            var stuck = left.Count == 0 ? string.Empty : $"; could not remove: {string.Join(", ", left)}";
            throw new RosterFailure($"kit import: {e.Message} — the level file was not written{undo}{stuck}");
        }

        c.Stdout.Write(plan.Text);
        c.Stdout.Write(written.Count == 0
            ? "nothing written: every item is identical\n"
            : string.Join(string.Empty, written.Select(w => $"wrote {w}\n")));
        return 0;
    }

    /// <summary>
    /// The K3 listing of what importing <paramref name="kit"/> would write: every write as <c>path · key · new|identical|conflict</c>, then every
    /// agent text with its findings and flagged tools, every rule, every pack reference with its adopt command, what stands against a commit,
    /// and the pin last. Reads only.
    /// </summary>
    internal static KitPlan Preview(RosterContext c, LevelFiles files, JsonObject kit, string pin, string level, string agentsKind, string? asName)
    {
        var config = c.Services.Config;
        string? newRoster = null;
        JsonObject? newBlock = null;
        var newRoles = new List<(string Role, JsonObject Row)>();
        var newAgents = new List<(string Path, string Text)>();
        string Esc(string s) => config.EscapeTerminal(JsonValue.FromString(s));
        string Show(JsonValue? v) => Esc(JsonText.Stringify(v ?? JsonValue.Null, 0));
        var findings = new List<string>();
        var conflicts = new List<string>();
        var flags = new List<string>();
        var writes = new List<string>();
        var notes = new List<string>();
        var agentSections = new List<string>();
        var packSections = new List<string>();

        void Keys(JsonObject o, string[] allowed, string where, Dictionary<string, string>? refused = null)
        {
            foreach (var k in o.Keys.Where(k => !allowed.Contains(k)))
            {
                findings.Add(refused is not null && refused.TryGetValue(k, out var why)
                    ? $"{where}: key {Show(k)} is refused ({why})"
                    : $"{where}: unknown key {Show(k)}");
            }
        }

        Keys(kit, TopKeys, "kit");
        if (!(kit.Get("kit") is { IsNumber: true } version && version.AsNumber() == 1))
        {
            findings.Add($"kit: \"kit\" must be 1, got {Show(kit.Get("kit"))}");
        }

        var path = files.PathOf(level);
        var (data, problem) = files.Load(path);
        if (problem is not null)
        {
            findings.Add($"{path} {problem}");
        }

        if (level == "repo")
        {
            notes.Add($"--level repo writes {path}, which is committed content");
        }

        var localRoles = Obj(data.Get("roles"));
        JsonObject? LocalRow(string role) => Obj(localRoles?.Get(role)) ?? RawRow(c, files, role);

        // ---- the roster block
        var memberRoles = new List<string>();
        var memberRows = new List<(string Where, JsonObject Member)>();
        if (Obj(kit.Get("roster")) is not { } roster)
        {
            findings.Add("kit: \"roster\" must be an object {name, block}");
        }
        else
        {
            Keys(roster, RosterKeys, "roster");
            var name = asName ?? (roster.Get("name") is { IsText: true } n ? n.AsString() : null);
            var nameOk = false;
            if (name is null)
            {
                findings.Add("roster: \"name\" must be text");
            }
            else if (name == config.DefaultRoster)
            {
                findings.Add($"roster: {Show(name)} means the unnamed `roster` block — import it under another name with --as <name>");
            }
            else if (config.ValidateTeamAlias(JsonValue.FromString(name)) is var check && !(check.Get("ok") is { IsTruthy: true }))
            {
                findings.Add($"roster: name {Show(name)}: {T(check.Get("why"))}");
            }
            else
            {
                nameOk = true;
            }

            if (Obj(roster.Get("block")) is not { } block)
            {
                findings.Add("roster: \"block\" must be an object");
            }
            else
            {
                Keys(block, BlockKeys, "roster block");

                // C1: the block's values pass the checks their own readers and verbs apply.
                if (block.Get("orchestrator_model") is { IsNull: false } om && !(om.IsText && om.AsString().Length == 0))
                {
                    if (!om.IsText)
                    {
                        findings.Add($"roster block: orchestrator_model must be text, got {Show(om)}");
                    }
                    else if (c.Services.Launch is null)
                    {
                        findings.Add("roster block: orchestrator_model can't be checked here (no launch rules in this process)");
                    }
                    else if (MemberTemplateVerbs.OrchestratorModelError(c, om.AsString()) is { } omWhy)
                    {
                        findings.Add($"roster block: {Esc(omWhy)}");
                    }
                }

                if (block.Get("max_lanes") is { IsNull: false } && Ah.Roster.RosterModule.MaxLanes(JsonValue.FromObject(block)).Rejected is { } rejected)
                {
                    findings.Add($"roster block: max_lanes {Esc(rejected)} is not a whole number from {Ah.Roster.RosterModule.LaneCapMin} to {Ah.Roster.RosterModule.LaneCapMax}");
                }
                if (block.Get("members") is not { IsArray: true } members)
                {
                    findings.Add("roster block: \"members\" must be a list");
                }
                else
                {
                    var i = 0;
                    foreach (var m in members.AsArray())
                    {
                        var where = $"member {i++}";
                        if (Obj(m) is not { } member)
                        {
                            findings.Add($"{where}: must be an object");
                            continue;
                        }

                        Keys(member, MemberKeys, where, RefusedMemberKeys);
                        if (member.Get("role") is { IsText: true } r)
                        {
                            memberRoles.Add(r.AsString());
                            memberRows.Add((where, member));
                        }
                        else
                        {
                            findings.Add($"{where}: \"role\" must be text");
                        }
                    }
                }

                if (nameOk)
                {
                    var existing = Obj(Obj(data.Get("rosters"))?.Get(name!));
                    var status = existing is null ? "new" : Same(existing, block) ? "identical" : "conflict";
                    writes.Add($"{path} · rosters.{Esc(name!)} · {status}");
                    if (status == "new")
                    {
                        newRoster = name;
                        newBlock = block;
                    }

                    if (status == "conflict")
                    {
                        conflicts.Add($"rosters.{Esc(name!)} is already defined differently in {path} — import it under another name with --as <name>");
                    }
                }
            }
        }

        // ---- the roles
        var kitRoles = kit.Get("roles") is { } rolesValue ? Obj(rolesValue) : new JsonObject();
        if (kitRoles is null)
        {
            findings.Add("kit: \"roles\" must be an object");
            kitRoles = new JsonObject();
        }

        string agentsDir;
        if (agentsKind == "repo")
        {
            var root = config.FindGitRoot(c.Cwd) ?? throw new RosterFailure("kit import: --agents repo needs a git repo");
            agentsDir = Path.Join(root, ".claude", "agents");
        }
        else
        {
            agentsDir = Path.Join(c.Services.Environment.HomeDirectory, ".claude", "agents");
        }

        var kitAgents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in kitRoles.Keys)
        {
            var who = $"role {Esc(role)}";
            if (Obj(kitRoles[role]) is not { } entry)
            {
                findings.Add($"{who}: must be an object {{row, agent}}");
                continue;
            }

            Keys(entry, EntryKeys, who);
            if (Obj(entry.Get("row")) is not { } row)
            {
                findings.Add($"{who}: \"row\" must be an object");
                continue;
            }

            var builtin = config.IsBuiltinRole(role);
            var local = LocalRow(role);
            if (config.IsFromRow(row))
            {
                Keys(row, FromRowKeys, $"{who} row", RefusedRowKeys);
                if (entry.Get("agent") is not null)
                {
                    findings.Add($"{who}: a pack role's agent comes from its pack, so the kit carries no agent text for it");
                }

                // The adopt command is printed for the user to run, so the role key and `from` must be valid names before it is shown.
                var parsedFrom = config.ParseFrom(row.Get("from"));
                if (!builtin && config.RoleNameError(JsonValue.FromString(role)) is { IsTruthy: true } packNameError)
                {
                    findings.Add($"{who}: {T(packNameError)}");
                    continue;
                }

                if (!parsedFrom.IsMap
                    || !FromParts.All(k => parsedFrom.Member(k) is { IsText: true } part && PlainName().IsMatch(part.AsString())))
                {
                    findings.Add($"{who}: from {Show(row.Get("from"))} must be <plugin>@<marketplace>:<role>");
                    continue;
                }

                var from = $"{T(parsedFrom.Member("plugin"))}@{T(parsedFrom.Member("marketplace"))}:{T(parsedFrom.Member("role"))}";
                var state = config.PackRoleStateJson(JsonValue.FromString(from), row.Get("pin"));
                var stateText = T(state.Get("status"));
                var shown = stateText switch
                {
                    "ok" => row.Get("pin") is { IsText: true } ? "trusted here" : "installed",
                    "pack-untrusted-here" => "installed",
                    "pack-changed" => "pin differs",
                    "pack-missing" => "not installed",
                    _ => $"pack role unavailable ({stateText})",
                };
                var adopt = $"ah roster role set {role} --from {from} --dry-run";
                var localStatus = local is null || !config.IsFromRow(local)
                    ? (local is null ? "not defined here" : "conflict")
                    : Same(Without(local, "pin"), Without(row, "pin")) ? "identical" : "conflict";
                packSections.Add($"  {Esc(role)} · from {Esc(from)} · {shown} · local row: {localStatus}\n    adopt: {Esc(adopt)}");
                if (local is null)
                {
                    findings.Add($"{who}: pack role not adopted here — adopt it first: {Esc(adopt)}");
                }
                else if (localStatus == "conflict")
                {
                    conflicts.Add($"roles.{Esc(role)} is already defined here by a different row");
                }

                continue;
            }

            // ah2-562: writes is carried on a custom review-class row only
            var reviewRow = !builtin && row.Get("class") is { IsText: true } rc && rc.AsString() == "review";
            Keys(row, reviewRow ? [.. RowKeys, "writes"] : RowKeys, $"{who} row", RefusedRowKeys);
            if (row.Get("shell") is { } shell && !(shell.IsText && shell.AsString() == config.ShellSelfCare))
            {
                findings.Add($"{who} row: \"shell\" may only be {config.ShellSelfCare}");
            }

            // The checks `role set` applies to the same row (RoleVerbs.Set), each reported as a finding.
            string cls;
            string? agent = null;
            var agentOk = false;
            if (builtin)
            {
                cls = config.RoleClass(role, null) ?? string.Empty;
                foreach (var k in RoleRowChecks.NotOnBuiltin.Where(row.ContainsKey))
                {
                    findings.Add($"{who} row: {RoleRowChecks.NotOnBuiltinError(k)}");
                }

                if (row.Get("model") is { } model && RoleRowChecks.ModelError(config, role, cls, model) is { } modelWhy)
                {
                    findings.Add($"{who} row: {Esc(modelWhy)}");
                }

                // A built-in row with no agent keeps its shipped agent; only a bare agent name is a file the kit can carry.
                if (row.Get("agent") is { } agentValueRow)
                {
                    var named = T(agentValueRow);
                    if (RoleRowChecks.BuiltinAgentError(config, role, agentValueRow) is { } refError)
                    {
                        findings.Add($"{who}: {Esc(refError)}");
                    }
                    else if (named == config.Qualified(role))
                    {
                        // The shipped agent named explicitly: nothing to write.
                    }
                    else if (named.Contains(':', StringComparison.Ordinal))
                    {
                        findings.Add($"{who}: agent {Show(named)} is a plugin agent, which only a pack role may name");
                    }
                    else if (config.RoleNameError(agentValueRow) is { IsTruthy: true } agentError)
                    {
                        findings.Add($"{who}: agent {Show(named)}: {T(agentError)}");
                    }
                    else
                    {
                        agent = named;
                        agentOk = true;
                    }
                }
            }
            else
            {
                cls = T(row.Get("class"));
                if (config.RoleNameError(JsonValue.FromString(role)) is { IsTruthy: true } nameError)
                {
                    findings.Add($"{who}: {T(nameError)}");
                }
                else if (config.CheckCustomRow(role, row).Get("error") is { IsTruthy: true } rowError)
                {
                    findings.Add($"{who}: {T(rowError)}");
                }

                // K1b: the agent file's path comes from the validated agent name, never from the kit's agent.file.
                agent = row.Get("agent") is { IsText: true } a ? a.AsString() : role;
                if (agent.Contains(':', StringComparison.Ordinal))
                {
                    findings.Add($"{who}: agent {Show(agent)} is a plugin agent, which only a pack role may name");
                }
                else if (config.RoleNameError(JsonValue.FromString(agent)) is { IsTruthy: true } agentError)
                {
                    findings.Add($"{who}: agent {Show(agent)}: {T(agentError)}");
                }
                else
                {
                    agentOk = true;
                }
            }

            if (RoleRowChecks.DispatchError(config, row) is { } dispatchWhy)
            {
                findings.Add($"{who} row: {dispatchWhy}");
            }
            else if (RoleRowChecks.DispatchModelError(row.Get("dispatch") is { IsText: true } dm ? dm.AsString() : null, cls) is { } dispatchModelWhy)
            {
                findings.Add($"{who} row: {dispatchModelWhy}");
            }

            // An agent maps to exactly one role: across the roles defined here (role set's check), and across the kit's own roles.
            var effectiveAgent = builtin ? config.RoleAgent(role, row) : agent!;
            if (RoleRowChecks.OwnerError(config, c.Registry, role, effectiveAgent) is { } ownerWhy)
            {
                findings.Add($"{who}: {Esc(ownerWhy)}");
            }

            if (kitAgents.TryGetValue(effectiveAgent, out var firstOwner))
            {
                findings.Add($"{who}: agent {Show(effectiveAgent)} is also role {Esc(firstOwner)}'s agent in this kit — an agent maps to exactly one role");
                agentOk = false;
            }
            else
            {
                kitAgents[effectiveAgent] = role;
            }

            var rowStatus = local is null ? "new" : Same(local, row) ? "identical" : "conflict";
            writes.Add($"{path} · roles.{Esc(role)} · {rowStatus}");
            if (reviewRow && row.Get("writes") is { } roleWrites)
            {
                if (config.WritesError(roleWrites) is { } writesWhy)
                {
                    findings.Add($"{who} row: {Esc(writesWhy)}");
                }
                else
                {
                    var globs = roleWrites.AsArray().Select(g => g.AsString()).ToList();
                    writes.Add($"  may write: {Esc(string.Join(", ", globs))}");
                    if (globs.FirstOrDefault(config.WriteGlobMatchesAll) is { } all)
                    {
                        notes.Add($"{Esc(role)}: writes \"{Esc(all)}\" matches every path: it can then write anywhere in the checkout");
                    }
                }
            }
            if (rowStatus == "new")
            {
                newRoles.Add((role, row));
            }

            if (rowStatus == "conflict")
            {
                conflicts.Add($"roles.{Esc(role)} is already defined here by a different row");
            }

            if (entry.Get("agent") is not { } agentValue)
            {
                continue;
            }

            if (Obj(agentValue) is not { } agentEntry || agentEntry.Get("text") is not { IsText: true } textValue)
            {
                findings.Add($"{who}: \"agent\" must be an object {{file, text}} with text");
                continue;
            }

            Keys(agentEntry, AgentKeys, $"{who} agent");
            if (!agentOk || agent is null)
            {
                if (agent is null)
                {
                    findings.Add($"{who}: the kit carries agent text, but the row names no bare agent to write it as");
                }

                continue;
            }

            var text = textValue.AsString();
            var file = Path.Join(agentsDir, agent + ".md");
            var fileStatus = !File.Exists(file) ? "new" : File.ReadAllText(file) == text ? "identical" : "conflict";
            writes.Add($"{file} · agent {Esc(agent)} · {fileStatus}");
            if (fileStatus == "new")
            {
                newAgents.Add((file, text));
            }

            if (fileStatus == "conflict")
            {
                conflicts.Add($"{file} exists with different text");
            }

            // K4: the pack agent rules, so an agent file can't carry hooks, MCP server settings or permissionMode.
            var agentFindings = new JsonArray();
            if (config.HiddenCharAt(JsonValue.FromString(text)) is { IsTruthy: true } hidden)
            {
                agentFindings.Add(new JsonObject { ["level"] = "error", ["code"] = "pack-hidden-chars", ["message"] = $"the text holds a hidden character ({T(hidden)})" });
            }

            var parsed = config.PackAgentParse(text, file);
            string? everyTool = null;
            if (parsed.Get("findings") is { IsArray: true } pf)
            {
                foreach (var f in pf.AsArray())
                {
                    // scaffold-tools U1/U3: in a kit only, a missing tools: line is a flag the pin approves, not a finding; the pack check is unchanged.
                    var noTools = f.Member("code") is { IsText: true } fc && fc.AsString() == "pack-agent-tools"
                        && f.Member("message") is { IsText: true } fmsg && fmsg.AsString().StartsWith("tools is required", StringComparison.Ordinal);
                    if (!noTools)
                    {
                        agentFindings.Add(f);
                        continue;
                    }

                    var except = Obj(parsed.Get("fm"))?.Get("disallowedTools") is { IsArray: true } dt && dt.AsArray().Count > 0
                        ? string.Join(", ", dt.AsArray().Select(x => Esc(T(x))))
                        : null;
                    flags.Add($"role {Esc(role)} agent {Esc(agent)}: inherits every tool, MCP servers included{(except is null ? string.Empty : $" (except: {except})")}");

                    // U5: the preview the pin approves may not understate what this agent can use.
                    everyTool = $"every tool (no tools: list){(except is null ? string.Empty : $" except: {except}")}";
                }
            }

            var contract = config.ValidateAgentContract(new JsonObject
            {
                ["role"] = role,
                ["cls"] = cls,
                ["agent"] = agent,
                ["description"] = builtin ? JsonValue.Null : row.Get("description") is { IsTruthy: true } d ? d : JsonValue.Null,
                ["builtin"] = builtin,
                ["cwd"] = c.Cwd,
                ["inMemory"] = new JsonObject { ["path"] = file, ["text"] = text, ["fm"] = parsed.Get("fm") ?? JsonValue.Null },
            });
            if (contract.Get("findings") is { IsArray: true } cf)
            {
                agentFindings.AddRange(cf.AsArray());
            }

            var toolReport = config.PackToolReport(parsed.Get("fm"));
            var flagged = toolReport.Get("flagged") is { IsArray: true } fl ? fl.AsArray().Select(x => Esc(T(x))).ToList() : [];
            var section = new List<string> { $"  {file} (the kit names it {Show(agentEntry.Get("file"))}; that name is not used for the path)" };

            // M6: the server-grant ⚠ lines role trust prints, before the flat list; a kit has no .mcp.json, so none is pinned.
            section.AddRange(config.PackToolLines(toolReport).Where(l => l.StartsWith('⚠')).Select(l => $"    {Esc(l)}"));
            section.Add($"    flagged tools: {everyTool ?? (flagged.Count == 0 ? "none" : string.Join(", ", flagged))}");
            foreach (var f in agentFindings)
            {
                var line = $"{T(f.Member("level"))} {T(f.Member("code"))}: {Esc(T(f.Member("message")))}";
                section.Add($"    {line}");
                if (f.Member("level") is { IsText: true } lv && lv.AsString() == "error")
                {
                    findings.Add($"{who} agent text: {line}");
                }
            }

            section.Add("    ---- text ----");
            section.AddRange(text.Split('\n').Select(l => "    | " + Esc(l)));
            agentSections.Add(string.Join("\n", section));
        }

        // K5: a member's role must come with the kit, be a built-in, or already be defined here.
        var defined = config.RegistryRoles(c.Registry);
        foreach (var r in memberRoles.Distinct(StringComparer.Ordinal))
        {
            if (!kitRoles.ContainsKey(r) && !config.IsBuiltinRole(r) && !defined.Contains(r))
            {
                findings.Add($"member role {Esc(r)} is neither in the kit nor defined here");
            }
        }

        // K3: each member's values pass the check `roster add` and `roster edit` apply, against the roles here plus the kit's own rows.
        var overlay = c.Registry.Clone();
        var overlayRoles = Obj(overlay.Get("roles"))?.Clone() ?? new JsonObject();
        foreach (var role in kitRoles.Keys)
        {
            if (Obj(Obj(kitRoles[role])?.Get("row")) is { } kitRow && !config.IsFromRow(kitRow) && !overlayRoles.ContainsKey(role))
            {
                overlayRoles[role] = kitRow;
            }
        }

        overlay["roles"] = overlayRoles;
        var overlayNames = config.RegistryRoles(overlay);
        foreach (var (where, member) in memberRows.Where(m => overlayNames.Contains(T(m.Member.Get("role")))))
        {
            if (c.Services.Launch is not { } launch)
            {
                findings.Add($"{where}: its values can't be checked here (no launch rules in this process)");
                continue;
            }

            foreach (var error in launch.Rules.ValidateMember(JsonValue.FromObject(member), overlay))
            {
                findings.Add($"{where}: {Esc(error)}");
            }
        }

        // ---- the rules
        var ruleLines = new List<string>();
        if (kit.Get("rules") is { } rulesValue)
        {
            if (!rulesValue.IsArray)
            {
                findings.Add("kit: \"rules\" must be a list");
            }
            else
            {
                var i = 0;
                foreach (var rv in rulesValue.AsArray())
                {
                    var where = $"rule {i++}";
                    if (Obj(rv) is not { } rule || rule.Get("key") is not { IsText: true } key || rule.Get("text") is not { IsText: true } ruleText)
                    {
                        findings.Add($"{where}: must be an object {{key, text}} of text");
                        continue;
                    }

                    Keys(rule, RuleKeys, where);
                    ruleLines.Add($"  {Esc(key.AsString())}: {Esc(ruleText.AsString())}");
                }
            }

            ruleLines.Add("rules: not carried by this kit (roster rules are added in a later version)");
        }

        var lines = new List<string>();
        if (flags.Count > 0)
        {
            lines.Add($"flags ({flags.Count}) — approved by the pin:");
            lines.AddRange(flags.Select(f => "  " + f));
        }

        lines.Add("writes:");
        lines.AddRange(writes.Select(w => "  " + w));
        lines.AddRange(notes.Select(n => "note: " + n));
        if (agentSections.Count > 0)
        {
            lines.Add("agent files:");
            lines.AddRange(agentSections);
        }

        if (ruleLines.Count > 0)
        {
            lines.Add("rules:");
            lines.AddRange(ruleLines);
        }

        if (packSections.Count > 0)
        {
            lines.Add("pack roles (never adopted by an import):");
            lines.AddRange(packSections);
        }

        if (findings.Count > 0)
        {
            lines.Add($"findings ({findings.Count}):");
            lines.AddRange(findings.Select(f => "  " + f));
        }

        if (conflicts.Count > 0)
        {
            lines.Add($"conflicts ({conflicts.Count}):");
            lines.AddRange(conflicts.Select(f => "  " + f));
        }

        lines.Add(findings.Count + conflicts.Count > 0
            ? $"commit: refused while {findings.Count} finding(s) and {conflicts.Count} conflict(s) stand"
            : "commit: nothing stands against a commit with the pin below");
        lines.Add($"pin: {pin}");
        return new KitPlan(string.Join("\n", lines) + "\n", findings.Count, conflicts.Count, path, newRoster, newBlock, newRoles, newAgents);
    }

    /// <summary>
    /// What the kit carries for role <paramref name="role"/>: a pack role's reference, a built-in's override row, or a custom row with
    /// the text of its resolved agent file. Null for a built-in nobody overrode, or a role no level defines.
    /// </summary>
    private static JsonObject? KitRole(RosterContext c, LevelFiles files, string role)
    {
        var config = c.Services.Config;
        var raw = RawRow(c, files, role);
        if (raw is null)
        {
            return null;
        }

        var entry = new JsonObject { ["row"] = raw.Clone() };
        if (config.IsFromRow(raw) || config.IsBuiltinRole(role))
        {
            return entry;
        }

        var agent = raw.Get("agent") is { IsText: true } a ? a.AsString() : role;
        if (agent.Contains(':', StringComparison.Ordinal))
        {
            return entry;
        }

        if (config.LocateAgentFile(JsonValue.FromString(agent), c.Cwd).Get("path") is { IsText: true } found)
        {
            entry["agent"] = new JsonObject { ["file"] = agent + ".md", ["text"] = File.ReadAllText(found.AsString()) };
        }

        return entry;
    }

    /// <summary>Role <paramref name="role"/>'s row as written in the level file that defines it; null when only the shipped defaults do.</summary>
    internal static JsonObject? RawRow(RosterContext c, LevelFiles files, string role)
    {
        var sources = c.Registry.Get("sources") is { IsMap: true } s ? s.AsObject() : new JsonObject();
        if (PackVerbs.LevelOfScope(sources.Get(role)) is not { IsText: true } level || level.AsString() == "shipped")
        {
            return null;
        }

        var data = files.Load(files.PathOf(level.AsString())).Data;
        return data.Get("roles") is { IsMap: true } roles && roles.AsObject().Get(role) is { IsMap: true } row ? row.AsObject() : null;
    }
}
