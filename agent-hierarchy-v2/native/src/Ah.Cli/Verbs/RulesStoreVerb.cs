using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah rules add|edit|off|remove|list</c>: the user's rules of engagement. Writes go to the daemon, which proves the caller; reads are open
/// to everyone. Exit codes: 0 ok, 1 not found, 2 refused, 3 conflict.
/// </summary>
internal static class RulesStoreVerb
{
    internal const string Usage =
        "usage: ah rules add|edit <scope> --key K --text T [--said W] | edit <scope> --key K [--text T] [--applies A] [--choose C] [--said W] | off|remove <scope> --key K [--said W] | propose <scope> --key K --text T --applies A --choose C [--said W] | accept K [<scope>] [--text T] [--applies A] [--choose C] | list [<scope> | --member name@team | --item <work id>] [--all] [--json] [--cwd <dir>]\n"
        + "       ah rules goal [--team <name>] [--text T [--done-when D] | --clear] [--said W] [--json] [--cwd <dir>]   (the team goal: guidance, never a gate; set or cleared only on the user's word)\n"
        + "       ah rules cap (--team <name> | --project <work id> | --goal <work id>) --unit U --max N [--said W] [--json] [--cwd <dir>]   (a spend cap: reaching it asks the user and never stops a call)\n"
        + "<scope> is one of: --global | --roster <name> | --repo | --team <name> | --project <work id> | --goal <work id>";

    private static readonly HashSet<string> Bools = new(StringComparer.Ordinal) { "global", "repo", "json", "all", "clear" };

    private static readonly HashSet<string> Values = new(StringComparer.Ordinal) { "roster", "team", "project", "goal", "key", "text", "said", "member", "item", "cwd", "applies", "choose", "done-when", "unit", "max" };

    /// <summary>The scope flags <c>ah rules goal</c> refuses: a goal is a team's, named by <c>--team</c> or the caller's.</summary>
    private static readonly string[] NotWithGoal = ["global", "roster", "repo", "project", "goal", "member", "item", "key"];

    internal static bool Handles(string? verb) => verb is "add" or "edit" or "off" or "remove" or "list" or "propose" or "accept" or "goal" or "cap";

    internal static int Run(CliContext context, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah rules: the services have not been composed");
            return 70;
        }

        var flags = new Dictionary<string, string?>(StringComparer.Ordinal);
        var first = 1;
        if (args[0] == "accept" && args.Count > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            flags["key"] = args[1];
            first = 2;
        }

        for (var i = first; i < args.Count; i++)
        {
            var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : null;
            if (name is not null && Bools.Contains(name))
            {
                flags[name] = null;
            }
            else if (name is not null && Values.Contains(name) && i + 1 < args.Count)
            {
                flags[name] = args[++i];
            }
            else if (name == "help")
            {
                return RulesVerb.Help(stdout);
            }
            else
            {
                return Fail(stderr, $"ah rules: {args[i]} is not understood; {Usage}");
            }
        }

        var g = services.Value;
        var dir = BacklogVerb.DirOf(g, flags.GetValueOrDefault("cwd") ?? Directory.GetCurrentDirectory());
        var json = flags.ContainsKey("json");
        try
        {
            return args[0] switch
            {
                "list" => List(context, g, dir, flags, json, stdout, stderr),
                "goal" => Goal(context, g, dir, flags, json, stdout, stderr),
                "cap" => Cap(g, dir, flags, json, stdout, stderr),
                _ => Write(context, g, dir, args[0], flags, json, stdout, stderr),
            };
        }
        catch (RulesUnreadableException e)
        {
            stderr.WriteLine($"ah rules: the rules could not be read: {e.Message}");
            return RulesOfEngagement.Refused;
        }
    }

    private static int Fail(TextWriter stderr, string message, int code = RulesOfEngagement.Refused)
    {
        stderr.WriteLine(message);
        return code;
    }

    /// <summary>The scope flags given, as level and text.</summary>
    private static List<(string Level, string Text)> ScopesOf(Dictionary<string, string?> flags)
    {
        var scopes = new List<(string, string)>();
        foreach (var level in new[] { RulesOfEngagement.Global, RulesOfEngagement.Repo })
        {
            if (flags.ContainsKey(level))
            {
                scopes.Add((level, string.Empty));
            }
        }

        foreach (var level in new[] { RulesOfEngagement.Roster, RulesOfEngagement.Team, RulesOfEngagement.Project, RulesOfEngagement.Goal })
        {
            if (flags.GetValueOrDefault(level) is { } text)
            {
                scopes.Add((level, text));
            }
        }

        return scopes;
    }

    private static int Write(CliContext context, ServiceGraph g, string dir, string op, Dictionary<string, string?> flags, bool json, TextWriter stdout, TextWriter stderr)
    {
        var scopes = ScopesOf(flags);
        if (op == "accept" && scopes.Count == 0 && flags.GetValueOrDefault("key") is { } acceptKey)
        {
            // with no scope, the one decision rule of that key
            var prefix = RulesReads.With<string?>(context.Env.AhHome, null, query => HierarchyModule.WorkPrefix(query, dir));
            var found = RulesReads.With<IReadOnlyList<RuleRow>>(context.Env.AhHome, [], query => RulesOfEngagement.Rows(query, dir)).Where(r => r.Key == acceptKey && r.IsDecision).ToList();
            if (found.Count != 1)
            {
                return Fail(stderr, found.Count == 0 ? $"ah rules accept: no decision rule {acceptKey}" : $"ah rules accept: {acceptKey} is a decision rule at {found.Count} scopes; name one; {Usage}", found.Count == 0 ? RulesOfEngagement.NotFound : RulesOfEngagement.Refused);
            }

            scopes.Add((found[0].Level, RulesJson.ScopeOf(found[0], prefix)));
        }

        if (scopes.Count != 1 || flags.GetValueOrDefault("key") is not { } key || flags.ContainsKey("all") || flags.ContainsKey("member") || flags.ContainsKey("item"))
        {
            return Fail(stderr, $"ah rules {op}: exactly one scope and --key are needed; {Usage}");
        }

        if ((op is "add" or "propose" && !flags.ContainsKey("text")) || (op == "edit" && !flags.ContainsKey("text") && !flags.ContainsKey("applies") && !flags.ContainsKey("choose")))
        {
            return Fail(stderr, $"ah rules {op}: --text is needed; {Usage}");
        }

        if (op == "propose" && (!flags.ContainsKey("applies") || !flags.ContainsKey("choose")))
        {
            return Fail(stderr, $"ah rules propose: --applies and --choose are needed; {Usage}");
        }

        if (op is not ("propose" or "edit" or "accept") && (flags.ContainsKey("applies") || flags.ContainsKey("choose")))
        {
            return Fail(stderr, $"ah rules {op}: --applies and --choose go with propose, edit or accept; {Usage}");
        }

        var body = new JsonObject { ["op"] = op, ["dir"] = dir, ["level"] = scopes[0].Level, ["scope"] = scopes[0].Text, ["key"] = key };
        foreach (var field in new[] { "text", "said", "applies", "choose" })
        {
            if (flags.GetValueOrDefault(field) is { } value)
            {
                body[field] = value;
            }
        }

        if (Post(g, op, body, stderr) is not { } answer)
        {
            return RulesOfEngagement.Refused;
        }

        var code = (int)answer.Member("code")!.Value.AsNumber();
        var message = answer.Member("message") is { IsText: true } m ? m.AsString() : string.Empty;
        if (code != RulesOfEngagement.Ok)
        {
            var reason = answer.Member("reason") is { IsText: true } r ? $"{r.AsString()}: " : string.Empty;
            return Fail(stderr, $"ah rules {op}: {reason}{message}", code);
        }

        if (op is "add" or "edit" or "accept" && OverCap(context, g, dir))
        {
            stderr.WriteLine($"ah rules {op}: your resolved list is past the {RulesOfEngagement.BlockCap}-character block, so the last rules will not be shown at session start; they are in ah rules list");
        }

        if (json)
        {
            stdout.WriteLine(JsonText.Stringify(answer, 0));
            return code;
        }

        stdout.WriteLine(message);
        if (op == "remove" && answer.Member("rule") is { } removed)
        {
            stdout.WriteLine(JsonText.Stringify(removed, 0));
        }

        // the words the user just made a standing decision
        if (op == "accept" && answer.Member("rule") is { IsMap: true } rule)
        {
            string Field(string name) => rule.Member(name) is { IsText: true } v ? v.AsString() : string.Empty;
            stdout.WriteLine($"it reads: when {Field("applies")}, choose {Field("choose")}. {Field("text")}");
        }

        stdout.WriteLine(RulesOfEngagement.Live);
        return code;
    }

    /// <summary>The daemon's answer to a write, which carries a <c>code</c>; or null, with why written to <paramref name="stderr"/>.</summary>
    private static JsonValue? Post(ServiceGraph g, string op, JsonObject body, TextWriter stderr)
    {
        var reply = g.Daemon.RequestAsync(new DaemonRequest("POST", "/v1/rules", JsonText.ToUtf8(body, 0), TimeSpan.FromSeconds(30))).GetAwaiter().GetResult();
        JsonValue? answer = null;
        if (reply.Status == 200)
        {
            try
            {
                answer = JsonText.Parse(reply.Body.ToArray());
            }
            catch (JsonParseException)
            {
                answer = null;
            }
        }

        if (answer?.Member("code") is not { IsNumber: true })
        {
            stderr.WriteLine($"ah rules {op}: the daemon did not take the write ({(reply.Status == 0 ? reply.FallbackReason : "status " + reply.Status.ToString(System.Globalization.CultureInfo.InvariantCulture))}); nothing was written; start ahd and try again");
            return null;
        }

        return answer;
    }

    /// <summary>
    /// <c>ah rules goal</c> (ah2-53): reads the goal that applies to a team, or sets, replaces or clears the team's own through the daemon, which checks who may write
    /// exactly as for a rule. Guidance only: nothing reads the goal to refuse work.
    /// </summary>
    private static int Goal(CliContext context, ServiceGraph g, string dir, Dictionary<string, string?> flags, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (NotWithGoal.FirstOrDefault(flags.ContainsKey) is { } scope)
        {
            return Fail(stderr, $"ah rules goal: --{scope} does not go with goal; {Usage}");
        }

        var clear = flags.ContainsKey("clear");
        var text = flags.GetValueOrDefault("text");
        if (clear && text is not null)
        {
            return Fail(stderr, $"ah rules goal: --text and --clear do not go together; {Usage}");
        }

        if (flags.ContainsKey("done-when") && text is null)
        {
            return Fail(stderr, $"ah rules goal: --done-when goes with --text; {Usage}");
        }

        if ((flags.GetValueOrDefault("team") ?? CallerAsk(context.Env, g, dir).Team) is not { } team)
        {
            return Fail(stderr, $"ah rules goal: name the team: --team <name>; {Usage}");
        }

        if (!clear && text is null)
        {
            var goal = RulesReads.GoalOf(context.Env.AhHome, dir, RulesReads.TeamChain(g, dir, team));
            if (json)
            {
                stdout.WriteLine(JsonText.Stringify(new JsonObject { ["goal"] = goal is null ? JsonValue.Null : RulesJson.Goal(goal) }, 0));
            }
            else if (goal is null)
            {
                stdout.WriteLine($"team {team} has no goal");
            }
            else
            {
                stdout.WriteLine(goal.Text);
                if (goal.Applies is { } doneWhen)
                {
                    stdout.WriteLine($"done when: {doneWhen}");
                }

                var day = DateTimeOffset.FromUnixTimeMilliseconds(goal.UpdatedMs).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                stdout.WriteLine($"({(goal.Scope == team ? "team" : "from team")} {goal.Scope}, set by {goal.By} on {day})");
            }

            return RulesOfEngagement.Ok;
        }

        var body = new JsonObject { ["op"] = clear ? "goal-clear" : "goal", ["dir"] = dir, ["level"] = RulesOfEngagement.Team, ["scope"] = team, ["key"] = RulesOfEngagement.GoalKey };
        foreach (var (field, flag) in new[] { ("text", "text"), ("said", "said"), ("applies", "done-when") })
        {
            if (flags.GetValueOrDefault(flag) is { } value)
            {
                body[field] = value;
            }
        }

        if (Post(g, "goal", body, stderr) is not { } answer)
        {
            return RulesOfEngagement.Refused;
        }

        var code = (int)answer.Member("code")!.Value.AsNumber();
        var message = answer.Member("message") is { IsText: true } m ? m.AsString() : string.Empty;
        if (code != RulesOfEngagement.Ok)
        {
            return Fail(stderr, $"ah rules goal: {message}", code);
        }

        if (json)
        {
            stdout.WriteLine(JsonText.Stringify(answer, 0));
            return code;
        }

        stdout.WriteLine(message);
        stdout.WriteLine(RulesOfEngagement.Live);
        return code;
    }

    /// <summary>
    /// <c>ah rules cap</c> (ah2-512 S4): sets or replaces a spend cap through the daemon, which checks who may write exactly as for a rule. The text is generated there.
    /// A cap only asks the user when it is reached; nothing stops a call.
    /// </summary>
    private static int Cap(ServiceGraph g, string dir, Dictionary<string, string?> flags, bool json, TextWriter stdout, TextWriter stderr)
    {
        var scopes = ScopesOf(flags);
        if (scopes is not [(RulesOfEngagement.Team or RulesOfEngagement.Project or RulesOfEngagement.Goal, _)] || flags.ContainsKey("key") || flags.ContainsKey("text"))
        {
            return Fail(stderr, $"ah rules cap: exactly one of --team, --project or --goal (a cap has no --repo, --global or --roster scope; its key and text are made for it); {Usage}");
        }

        if (HierarchyModule.SpendUnitOf(flags.GetValueOrDefault("unit")) is not { } unit)
        {
            return Fail(stderr, $"ah rules cap: --unit is 1 to 40 of a-z, 0-9 and -; {Usage}");
        }

        if (HierarchyModule.SpendMicrosOf(flags.GetValueOrDefault("max")) is not { } max || max <= 0)
        {
            return Fail(stderr, $"ah rules cap: --max is a number above 0 with at most 6 decimal places; {Usage}");
        }

        var body = new JsonObject
        {
            ["op"] = "cap", ["dir"] = dir, ["level"] = scopes[0].Level, ["scope"] = scopes[0].Text, ["key"] = RulesOfEngagement.CapKeyPrefix + unit, ["applies"] = HierarchyModule.SpendFormat(max),
        };
        if (flags.GetValueOrDefault("said") is { } said)
        {
            body["said"] = said;
        }

        if (Post(g, "cap", body, stderr) is not { } answer)
        {
            return RulesOfEngagement.Refused;
        }

        var code = (int)answer.Member("code")!.Value.AsNumber();
        var message = answer.Member("message") is { IsText: true } m ? m.AsString() : string.Empty;
        if (code != RulesOfEngagement.Ok)
        {
            return Fail(stderr, $"ah rules cap: {message}", code);
        }

        stdout.WriteLine(json ? JsonText.Stringify(answer, 0) : message + "\n" + RulesOfEngagement.Live);
        return code;
    }

    /// <summary>Whether the writer's own resolved list no longer fits a session's block. The write has already happened, so a list that cannot be read says no.</summary>
    private static bool OverCap(CliContext context, ServiceGraph g, string dir)
    {
        try
        {
            return RulesOfEngagement.PassesCap(
                RulesReads.ForMember(g, context.Env.AhHome, dir, CallerAsk(context.Env, g, dir)).Rules,
                RulesReads.With<string?>(context.Env.AhHome, null, query => HierarchyModule.WorkPrefix(query, dir)));
        }
        catch (Exception e) when (e is RulesUnreadableException or StoreUnavailableException)
        {
            return false;
        }
    }

    private static int List(CliContext context, ServiceGraph g, string dir, Dictionary<string, string?> flags, bool json, TextWriter stdout, TextWriter stderr)
    {
        var ahHome = context.Env.AhHome;
        var scopes = ScopesOf(flags);
        var member = flags.GetValueOrDefault("member");
        var item = flags.GetValueOrDefault("item");
        var selectors = scopes.Count + (member is null ? 0 : 1) + (item is null ? 0 : 1);
        if (selectors > 1 || (flags.ContainsKey("all") && selectors > 0))
        {
            return Fail(stderr, $"ah rules list: at most one of a scope, --member, --item or --all; {Usage}");
        }

        var prefix = RulesReads.With<string?>(ahHome, null, query => HierarchyModule.WorkPrefix(query, dir));
        if (flags.ContainsKey("all"))
        {
            return WriteAll(RulesReads.With<IReadOnlyList<RuleRow>>(ahHome, [], query => RulesOfEngagement.Rows(query, dir)), RulesReads.With<IReadOnlyList<RuleRow>>(ahHome, [], query => RulesOfEngagement.Goals(query, dir)), prefix, json, stdout);
        }

        ResolvedRules resolved;
        IReadOnlyList<string> chain;
        if (member is not null)
        {
            if (AskOf(g, dir, member) is not { } ask)
            {
                return Fail(stderr, $"ah rules list: no member {member}", RulesOfEngagement.NotFound);
            }

            resolved = RulesReads.ForMember(g, ahHome, dir, ask);
            chain = RulesReads.TeamChain(g, ask.TeamDir ?? dir, ask.Team, ask.Parent);
        }
        else if (item is not null || scopes is [(RulesOfEngagement.Project or RulesOfEngagement.Goal, _)])
        {
            var text = item ?? scopes[0].Text;
            if (WorkVerb.Resolve(g, ahHome, dir, text, null).Row is not { } row)
            {
                return Fail(stderr, $"ah rules list: no work item {text}", RulesOfEngagement.NotFound);
            }

            resolved = RulesReads.ForItem(g, ahHome, dir, row);
            chain = RulesReads.TeamChain(g, dir, row.IsBacklog ? null : row.Team);
        }
        else if (scopes is [(RulesOfEngagement.Global or RulesOfEngagement.Roster, var roster)])
        {
            // the global rules alone, or with one roster's for a team of it: no repo rule
            string[] levels = roster.Length == 0 ? [RulesOfEngagement.Global] : [RulesOfEngagement.Global, RulesOfEngagement.Roster];
            var rows = RulesReads.With<IReadOnlyList<RuleRow>>(ahHome, [], query => RulesOfEngagement.Rows(query, dir)).Where(r => levels.Contains(r.Level, StringComparer.Ordinal));
            resolved = RulesOfEngagement.Resolve(rows, RuleSubject.RepoOnly with { Roster = roster.Length == 0 ? null : roster });
            chain = [];
        }
        else if (scopes.Count == 1)
        {
            var team = scopes[0].Level == RulesOfEngagement.Team ? scopes[0].Text : null;
            resolved = RulesReads.ForTeam(g, ahHome, dir, team);
            chain = RulesReads.TeamChain(g, dir, team);
        }
        else
        {
            var ask = CallerAsk(context.Env, g, dir);
            resolved = RulesReads.ForMember(g, ahHome, dir, ask);
            chain = RulesReads.TeamChain(g, ask.TeamDir ?? dir, ask.Team, ask.Parent);
        }

        var goal = RulesReads.GoalOf(ahHome, dir, chain);
        return WriteResolved(resolved, goal, goal is not null && goal.Scope != chain[0], prefix, json, stdout);
    }

    /// <summary>The caller, found as <c>ah roster whoami</c> finds it.</summary>
    internal static RulesAsk CallerAsk(IAhEnvironment env, ServiceGraph g, string dir)
    {
        var names = WorkVerb.CallerNames(env, g, dir);
        string? team = null;
        try
        {
            var found = g.Roster.LocateSession(dir, [dir], null, false, null);
            team = found.Get("answeredBy") is { IsTruthy: true } && found.Get("teamName") is { IsText: true } name ? name.AsString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
        {
            // no team to be found: the caller gets the repo's rules
        }

        return new RulesAsk(team, names, IsOrchestrator(env, g, dir, team));
    }

    private static bool IsOrchestrator(IAhEnvironment env, ServiceGraph g, string dir, string? team)
    {
        if (team is null || env.GetVariable("AH_MEMBER_NAME") is { Length: > 0 } || env.GetVariable("CLAUDE_CODE_SESSION_ID") is not { Length: > 0 } session)
        {
            return false;
        }

        try
        {
            return g.Roster.ReadTeamRecord(dir, team).Member("orchestrator")?.Member("session_id") is { IsText: true } o && o.AsString() == session;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
        {
            return false;
        }
    }

    /// <summary>The ask for <c>name@team</c>, or null when the team has no such member.</summary>
    private static RulesAsk? AskOf(ServiceGraph g, string dir, string address)
    {
        var parts = address.Split('@', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 || g.Roster.ReadTeamRecord(dir, parts[1]) is not { IsMap: true } record)
        {
            return null;
        }

        if (parts[0] == "orchestrator")
        {
            return new RulesAsk(parts[1], new HashSet<string>(StringComparer.Ordinal), true);
        }

        var row = record.Member("members") is { IsArray: true } members
            ? members.AsArray().FirstOrDefault(m => m.Member("name") is { IsText: true } n && n.AsString() == parts[0])
            : default;
        if (!row.IsMap)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal) { parts[0] };
        if (row.Member("role") is { IsText: true } role && role.AsString().Length > 0)
        {
            _ = names.Add(role.AsString());
        }

        return new RulesAsk(parts[1], names, false);
    }

    private static int WriteAll(IReadOnlyList<RuleRow> rows, IReadOnlyList<RuleRow> goals, string? prefix, bool json, TextWriter stdout)
    {
        if (json)
        {
            var array = new JsonArray();
            foreach (var row in rows)
            {
                array.Add(RulesJson.Row(row, prefix));
            }

            var goalArray = new JsonArray();
            foreach (var goal in goals)
            {
                goalArray.Add(RulesJson.Goal(goal));
            }

            stdout.WriteLine(JsonText.Stringify(new JsonObject { ["rows"] = array, ["goals"] = goalArray }, 0));
            return RulesOfEngagement.Ok;
        }

        foreach (var row in rows)
        {
            stdout.WriteLine($"[{row.Key}] {(row.Off ? "(off)" : TextOf(row))} ({Where(row, prefix)})");
        }

        foreach (var goal in goals)
        {
            stdout.WriteLine(GoalListLine(goal, false));
        }

        return RulesOfEngagement.Ok;
    }

    /// <summary>A team goal as <c>ah rules list</c> prints it, raw as rule text is.</summary>
    private static string GoalListLine(RuleRow goal, bool inherited) =>
        $"[goal] {goal.Text}{(goal.Applies is { } doneWhen ? "; done when: " + doneWhen : string.Empty)} ({(inherited ? "from team" : "team")} {goal.Scope})";

    private static int WriteResolved(ResolvedRules resolved, RuleRow? goal, bool inherited, string? prefix, bool json, TextWriter stdout)
    {
        if (json)
        {
            var listed = RulesJson.Resolved(resolved, prefix);
            listed["goal"] = goal is null ? JsonValue.Null : RulesJson.Goal(goal);
            stdout.WriteLine(JsonText.Stringify(listed, 0));
            return RulesOfEngagement.Ok;
        }

        if (goal is not null)
        {
            stdout.WriteLine(GoalListLine(goal, inherited));
        }

        foreach (var rule in resolved.Rules)
        {
            stdout.WriteLine($"[{rule.Row.Key}] {TextOf(rule.Row)} ({Where(rule.Row, prefix)})");
        }

        foreach (var row in resolved.Off)
        {
            stdout.WriteLine($"[{row.Key}] switched off ({Where(row, prefix)})");
        }

        return RulesOfEngagement.Ok;
    }

    /// <summary>A guide rule's text; a decision rule's, with what it answers and whether the user has accepted it.</summary>
    private static string TextOf(RuleRow row) =>
        !row.IsDecision ? row.Text : $"{(row.IsProposed ? "proposed" : "accepted")} decision: when {row.Applies}, choose {row.Choose}. {row.Text}";

    private static string Where(RuleRow row, string? prefix) =>
        row.Level is RulesOfEngagement.Repo or RulesOfEngagement.Global ? row.Level : $"{row.Level} {RulesJson.ScopeOf(row, prefix)}";
}
