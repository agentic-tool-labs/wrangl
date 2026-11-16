using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Launch;

namespace Ah.Commands;

/// <summary>What a look at a member's pane came to: herdr's answer for it, or tmux's read of the screen standing in for one.</summary>
internal sealed record PaneState(bool Live, bool Indeterminate, string? Why, string? AgentStatus, bool Ready, JsonObject? Prompt)
{
    internal HerdrAgentState AsHerdr() => new(Live, Indeterminate, Why ?? string.Empty, AgentStatus, Ready);
}

/// <summary>The member lookups and the pane reads shared by <c>deliver</c> and <c>answer</c>.</summary>
internal static class PaneMembers
{
    internal static string Js(JsonValue? value) => value is { } v ? JsonText.Stringify(v, 0) : "undefined";

    internal static IRosterPanes Panes(RosterContext c, string verb) =>
        c.Services.Panes ?? throw new RosterFailure($"{verb} is not available here: the pane services are not composed");

    internal static IRosterLaunch Launch(RosterContext c, string verb) =>
        c.Services.Launch ?? throw new RosterFailure($"{verb} is not available here: the launch layer is not composed");

    /// <summary>
    /// The non-claude pane member <c>deliver</c> or <c>answer</c> names, by its final name in this invocation's team. A Claude member
    /// is reached by SendMessage, never through its pane, so naming one is a usage error, as is a name the team does not hold.
    /// </summary>
    internal static JsonObject PaneMemberOrFail(RosterContext c, string dir, string? name, string verb)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new RosterFailure($"{verb}: name the member — {verb} <member name>");
        }

        var config = c.Services.Config;
        var teamPath = c.Services.Roster.TeamPath(dir, c.TeamFile);
        var team = c.Services.Roster.ReadTeamRecord(dir, c.TeamFile);
        if (!team.IsMap)
        {
            throw new RosterFailure($"{verb}: there is no team at {teamPath} — pass --team <name> for the team {name} is in");
        }

        var members = team.Member("members") is { IsArray: true } list ? list.AsArray().Where(m => m.IsMap).ToList() : [];
        var found = members.FirstOrDefault(m => m.Member("name") is { IsText: true } n && n.AsString() == name);
        if (found.IsMap == false)
        {
            var pane = members.Where(m => config.IsPaneMember(m)).Select(m => MemberPlanning.JsConvertText(m.Member("name")));
            var names = string.Join(", ", pane);
            throw new RosterFailure($"{verb}: {teamPath} has no member named {Js(JsonValue.FromString(name))} — its pane members are: {(names.Length == 0 ? "(none)" : names)}");
        }

        var kind = MemberPlanning.JsConvertText(config.ResolveKind(found));
        var kindDefault = Launch(c, verb).Registry.KindDefault;
        if (kind == kindDefault && verb != "deliver")
        {
            throw new RosterFailure($"{verb}: {name} is a Claude session — SendMessage it");
        }

        if (!config.IsPaneMember(found) && !(verb == "deliver" && config.RouteHasPane(found.Member("route"))))
        {
            throw new RosterFailure($"{verb}: {name} (kind {kind}) is recorded with route {Js(found.Member("route"))}, not \"pane\"");
        }

        return found.AsObject();
    }

    /// <summary>The member's last 20 lines, for diagnosis only: a report is only ever the file.</summary>
    internal static JsonValue PaneTail(RosterContext c, JsonObject member)
    {
        try
        {
            var name = MemberPlanning.JsConvertText(member.Get("name"));
            var r = HerdrOf(c, name, member.Get("session_id") is { IsText: true } s ? s.AsString() : null).InvokeAllowFailure(
                ["agent", "read", name, "--source", "recent-unwrapped", "--lines", "20"]);
            return r.Ok ? TailLines(r.Stdout) : JsonValue.Null;
        }
        catch (InvalidOperationException)
        {
            return JsonValue.Null;
        }
    }

    internal static string TailLines(string text)
    {
        var lines = text.Split('\n');
        return string.Join('\n', lines.Length > 20 ? lines[^20..] : lines);
    }

    /// <summary>The parsed JSON a herdr call wrote to either stream, or null.</summary>
    internal static JsonValue? HerdrJson(HerdrRaw res)
    {
        foreach (var stream in new[] { res.Stdout, res.Stderr })
        {
            if (JsText.Trim(stream).Length == 0)
            {
                continue;
            }

            if (JsonText.TryParse(Utf8Text.Encode(stream), out var value))
            {
                return value;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- reads

    /// <summary>The paths a kind's answer descriptions name, resolved for this cwd the way the trust check resolves them.</summary>
    private static JsonObject PromptContext(RosterContext c, string kind)
    {
        var panes = Panes(c, "deliver");
        var resolved = Path.GetFullPath(c.Cwd);
        var trust = Launch(c, "deliver").Harnesses.Has(kind) ? panes.Trust.Check(c.Cwd) : null;
        return new JsonObject
        {
            ["cwd"] = resolved,
            ["configPath"] = trust?.Get("configPath") is { IsTruthy: true } cp ? cp : string.Empty,
            ["codexHome"] = trust?.Get("codexHome") is { IsTruthy: true } ch ? ch : string.Empty,
            ["trustRoot"] = trust?.Get("trustRoot") is { IsTruthy: true } tr ? tr : resolved,
        };
    }

    /// <summary>One <c>visible</c> read of a herdr member's screen, with the answers to offer.</summary>
    internal static JsonObject ReadPrompt(RosterContext c, JsonObject member, string? agentStatus) =>
        Launch(c, "deliver").Launcher.ReadPrompt(member, agentStatus, c.LaunchContext());

    /// <summary>One tmux capture of pane <paramref name="pane"/>, or null when it cannot be read.</summary>
    internal static string? TmuxCapture(RosterContext c, string pane)
    {
        try
        {
            var r = Panes(c, "deliver").Mux.Run("tmux", ["capture-pane", "-p", "-t", pane], 15000);
            return r.Ok ? r.Stdout : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The prompt read for a member on tmux pane <paramref name="pane"/>, from one capture; <c>unreadable</c> when it failed.</summary>
    internal static JsonObject ReadPromptTmux(RosterContext c, JsonObject member, string pane, string? capture)
    {
        var panes = Panes(c, "deliver");
        var kind = MemberPlanning.JsConvertText(c.Services.Config.ResolveKind(member));
        var screen = capture ?? string.Empty;
        var seen = panes.Screens.RecognizeScreen(kind, screen, null);
        var prompt = seen.Get("prompt") is { IsTruthy: true } p ? JsConvert.ToJsString(p) : null;
        var options = prompt is not null
            ? panes.Screens.PromptOptions(kind, prompt, seen.Get("block"), PromptContext(c, kind))
            : new JsonArray();
        return new JsonObject
        {
            ["composer"] = seen.Get("composer") ?? JsonValue.Null,
            ["recognized"] = prompt is null ? JsonValue.Null : prompt,
            ["block"] = seen.Get("block") ?? JsonValue.Null,
            ["blocked_by"] = prompt is null ? JsonValue.Null : prompt,
            ["screen"] = screen,
            ["screen_hash"] = panes.Screens.ScreenHash(screen),
            ["options"] = options,
            ["working"] = seen.Get("busy") is { IsTruthy: true } || seen.Get("draft") is { IsTruthy: true },
            ["unreadable"] = capture is null,
        };
    }

    /// <summary>A member on tmux as herdr would report it: blocked on a recognised prompt, working on the busy signature, else idle.</summary>
    internal static PaneState TmuxAgentState(RosterContext c, JsonObject member, string pane)
    {
        var p = ReadPromptTmux(c, member, pane, TmuxCapture(c, pane));
        if (p.Get("unreadable") is { IsTruthy: true })
        {
            return new PaneState(true, true, $"tmux capture-pane failed for pane {pane}", null, false, p);
        }

        var status = p.Get("blocked_by") is { IsTruthy: true } ? "blocked" : p.Get("working") is { IsTruthy: true } ? "working" : "idle";
        return new PaneState(true, false, null, status, true, p);
    }

    internal static PaneState HerdrAgentState(RosterContext c, string name)
    {
        var s = HerdrOf(c, name, null).AgentState(name);
        return new PaneState(s.Live, s.Indeterminate, s.Why, s.AgentStatus, s.Ready, null);
    }

    /// <summary>The herdr adapter of the server member <paramref name="name"/> lives on (<see cref="IRosterLaunch.FlavourOf"/>); with no <paramref name="session"/>, its team row's.</summary>
    private static Ah.Launch.IHerdr HerdrOf(RosterContext c, string name, string? session)
    {
        var launch = Launch(c, "deliver");
        session ??= MemberPlanning.Text(c.Services.Roster.TeamMemberByName(c.Dir, name, c.TeamFile).Member("session_id"));
        return launch.HerdrFor(IRosterLaunch.FlavourOf(c.Services, c.Dir, c.TeamFile, session, name));
    }

    /// <summary><c>{blocked_by, screen, screen_hash, options}</c> as deliver, spawn and answer report it.</summary>
    internal static JsonObject BlockedFields(JsonObject p) => new()
    {
        ["blocked_by"] = p.Get("blocked_by") ?? JsonValue.Null,
        ["screen"] = p.Get("screen") ?? JsonValue.Null,
        ["screen_hash"] = p.Get("screen_hash") ?? JsonValue.Null,
        ["options"] = p.Get("options") ?? JsonValue.Null,
    };

    // ---------------------------------------------------------------- activity

    /// <summary>
    /// A pane member's activity record as <c>deliver</c> learns its state from herdr: <c>See</c> records each observation and
    /// <c>Final</c> records the last one again as the run ends. The record is written only for a state it does not already hold, so
    /// neither refreshes the time a state began.
    /// </summary>
    internal sealed class ActivityObserver(RosterContext c, string dir, JsonObject member)
    {
        private JsonObject? _latest;

        internal void See(string? agentStatus, JsonObject? p = null)
        {
            var activity = agentStatus switch
            {
                "working" => "working",
                "idle" => "idle",
                "done" => "idle",
                "blocked" => "blocked",
                "agent_not_found" => "unknown",
                _ => null,
            };
            if (activity is null)
            {
                return;
            }

            var blocked = activity == "blocked";
            JsonValue blockedBy = JsonValue.Null;
            JsonValue note = JsonValue.Null;
            if (blocked)
            {
                blockedBy = p?.Get("blocked_by") is { IsTruthy: true } b ? b : JsonValue.Null;
                var kind = MemberPlanning.JsConvertText(c.Services.Config.ResolveKind(member));
                var recognized = p?.Get("recognized") is { IsText: true } r ? r.AsString() : null;
                var screen = p?.Get("screen") is { IsText: true } sc ? sc.AsString() : null;
                var line = Panes(c, "deliver").Reading.PromptNoteLine(kind, recognized, screen);
                note = line is null ? JsonValue.Null : line;
            }

            _latest = new JsonObject { ["activity"] = activity, ["blocked_by"] = blockedBy, ["note"] = note };
            Record(_latest);
        }

        internal void Final()
        {
            if (_latest is not null)
            {
                Record(_latest);
            }
        }

        private void Record(JsonObject fields) =>
            c.Services.Hierarchy.Status.RecordActivityRouted(dir, $"pane-{MemberPlanning.JsConvertText(member.Get("name"))}", fields);
    }

    // ---------------------------------------------------------------- respawn

    /// <summary>
    /// The command that stands a not-live member up again. A roster row for its role that derives its name, or the name it was
    /// renamed from, means <c>spawn-one</c> resolves it; any other member is re-spawned with <c>spawn-ad-hoc</c> from its recorded
    /// fields, and <c>spawn_note</c> says the name may come back different.
    /// </summary>
    internal static JsonObject RespawnCommand(RosterContext c, string dir, JsonObject member)
    {
        var self = c.SelfCommand;
        var config = c.Services.Config;
        var found = config.ResolveRoster(c.Cwd, c.Services.Roster.TeamRosterKey(dir, c.TeamFile), c.RepoBasename, c.Registry);
        var role = member.Get("role");
        var name = member.Get("name");
        var renamedFrom = member.Get("renamed_from");
        JsonValue? row = null;
        if (found.IsMap && found.Member("members") is { IsArray: true } rows)
        {
            row = rows.AsArray()
                .Where(m => m.IsMap)
                .Cast<JsonValue?>()
                .FirstOrDefault(m => Same(m!.Value.Member("role"), role)
                    && (Same(m.Value.Member("name"), name) || (renamedFrom is { IsTruthy: true } && Same(m.Value.Member("name"), renamedFrom))));
        }

        var team = c.Opts.Text("team") is { } t ? $" --team {RosterContext.ArgWord(t)}" : string.Empty;
        var cwd = RosterContext.ArgWord(c.Cwd);
        var roleText = MemberPlanning.JsConvertText(role);
        if (row is not null)
        {
            return new JsonObject { ["spawn"] = $"{self} spawn-one {roleText} --member {MemberPlanning.JsConvertText(name)}{team} --cwd {cwd}" };
        }

        var kind = MemberPlanning.JsConvertText(config.ResolveKind(member));
        var flags = new List<string>();
        if (kind != Launch(c, "deliver").Registry.KindDefault)
        {
            flags.Add($"--kind {RosterContext.ArgWord(kind)}");
        }

        foreach (var (key, flag) in new[] { ("model", "--model"), ("effort", "--effort"), ("route", "--route"), ("autoMode", "--auto-mode") })
        {
            if (member.Get(key) is { IsTruthy: true } v)
            {
                flags.Add($"{flag} {RosterContext.ArgWord(MemberPlanning.JsConvertText(v))}");
            }
        }

        if (Launch(c, "deliver").Rules.MemberArgs(member) is { } args)
        {
            flags.Add($"--args {RosterContext.ShQuote(JsonText.Stringify(args, 0))}");
        }

        return new JsonObject
        {
            ["spawn"] = string.Join(' ', [$"{self} spawn-ad-hoc {roleText}", .. flags]) + $"{team} --cwd {cwd}",
            ["spawn_note"] =
                $"{MemberPlanning.JsConvertText(name)} has no roster row, so it is re-spawned ad hoc from its record. Its name is derived again and may come back different: brief the name the spawn result reports.",
        };
    }

    private static bool Same(JsonValue? a, JsonValue? b) =>
        a is { IsText: true } x && b is { IsText: true } y && x.AsString() == y.AsString();
}
