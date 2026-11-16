using Ah.Core.Json;

namespace Ah.Hierarchy;

internal sealed partial class HierLib
{
    public string TierLine(JsonObject resolved, JsonValue? model)
    {
        var tier = _config.TierOf(model);
        var custom = string.Empty;
        if (_config.CustomTierText(resolved).Length > 0)
        {
            var view = _config.AvailabilityView(resolved).Get("view");
            custom = _config.CustomTierText(view is { IsMap: true } v ? v.AsObject() : null);
        }

        var roles = $"architect {RoleModelLabel("architect", resolved)}; ultra-advisor {RoleModelLabel("ultra-advisor", resolved)}{custom}";
        if (model is { IsTruthy: true } m && tier is not null)
        {
            return $"tier: you are {JsConvert.ToJsString(m)}({tier.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}); {roles}";
        }

        return $"tier: model unknown — see TIER RULE; {roles}";
    }

    private List<string> TeamStateLines(string dir, JsonObject resolved, string repoBasename, double now)
    {
        var teamValue = resolved.Get("team") is { IsTruthy: true } tv ? tv : JsonValue.Null;
        var open = OpenExchanges(dir, teamValue, ListExchangesOfTeam(dir, teamValue));
        var shown = open.Take(10).Select(e =>
            $"{JsConvert.ToJsString(e.Member("id"))} {JsConvert.ToJsString(e.Member("to"))} {JsConvert.ToJsString(e.Member("slug"))} {FmtAge(ExchangeAgeSec(e, now))}");
        var openLine = open.Count > 0
            ? $"open exchanges: {open.Count} — {string.Join(", ", shown)}{(open.Count > 10 ? $" +{open.Count - 10} more: ah msg list" : string.Empty)}"
            : "open exchanges: none";
        var team = _roster.ReadTeamRecord(dir, TextOf(teamValue));
        string peersLine;
        if (team.Member("members") is { IsArray: true } members && members.AsArray().Count > 0)
        {
            var ros = Roster(dir, resolved, repoBasename, now);
            var rows = members.AsArray().Select(m =>
            {
                var role = JsConvert.ToJsString(m.Member("role"));
                if (!_config.RouteHasPane(m.Member("route")) || !(m.Member("name") is { IsTruthy: true } name))
                {
                    return $"{role}=(subagent)";
                }

                var list = ros.Get(role) is { IsArray: true } l ? l.AsArray() : new JsonArray();
                var live = list.FirstOrDefault(i => i.Member("name") is { } n && StrictEquals(n, name));
                var found = list.Any(i => i.Member("name") is { } n && StrictEquals(n, name));
                var state = found
                    ? (Member(live, "busy").IsTruthy ? "busy" : "idle")
                    : _memberVerdict?.Invoke(dir, m) switch { "live" => "live", "proven_dead" => "gone", _ => "not-seen" };
                return $"{role}={JsConvert.ToJsString(name)} {state}";
            });
            var teamObject = team.IsMap ? team.AsObject() : null;
            var partial = _config.TeamIsPartial(dir, TextOf(resolved.Get("cwd")), TextOf(teamValue), teamObject, resolved).IsTruthy ? " [partial]" : string.Empty;
            peersLine = $"Team {JsConvert.ToJsString(team.Member("team_id"))} (authoritative){partial}: {string.Join("; ", rows)}";
        }
        else
        {
            var ros = Roster(dir, resolved, repoBasename, now);
            var anyPeer = _config.ChainRoles(resolved).Any(r => ros.Get(r) is { IsArray: true } a && a.AsArray().Count > 0);
            peersLine = $"peers: {(anyPeer ? RosterLine(ros) : "none")}";
        }

        var lines = new List<string> { openLine, peersLine };
        lines.AddRange(WorkstreamLines(dir, resolved, repoBasename, TextOf(teamValue), team, now));
        return lines;
    }

    private List<string> WorkstreamLines(string dir, JsonObject resolved, string repoBasename, string? teamName, JsonValue team, double now)
    {
        var lines = new List<string>();
        if (teamName is null || team.Member("streams") is not { IsMap: true } streams)
        {
            return lines;
        }

        var streamObjects = streams.AsObject();
        var names = streamObjects.Keys.Where(k => TextOf(streamObjects[k].Member("sub_orchestrator")) is not null).ToList();
        if (names.Count == 0)
        {
            return lines;
        }

        var ros = Roster(dir, resolved, repoBasename, now);
        foreach (var ws in names)
        {
            var record = streamObjects[ws];
            var so = TextOf(record.Member("sub_orchestrator"))!;
            var row = team.Member("members") is { IsArray: true } rows ? rows.AsArray().FirstOrDefault(m => TextOf(m.Member("name")) == so) : default;
            var word = _memberVerdict is { } verdictOf && row.IsMap
                ? verdictOf(dir, row) switch { "live" => "live", "proven_dead" => "dead", _ => "unknown" }
                : ros.Keys.Any(k => ros[k] is { IsArray: true } a && a.AsArray().Any(i => i.Member("name") is { } n && TextOf(n) == so)) ? "live" : "unknown";
            var child = (_roster.FindChildTeam(dir, teamName, ws) is { } childName ? _roster.ReadTeamRecord(dir, childName) : JsonValue.Null).Member("workstream");
            var state = TextOf(child?.Member("state")) ?? "unreported";
            var summary = TextOf(child?.Member("summary")) ?? string.Empty;
            var age = child?.Member("updated") is { IsText: true } updated
                && DateTimeOffset.TryParse(updated.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
                ? FmtAge(JsonValue.FromNumber(Math.Max(0, (now - at.ToUnixTimeMilliseconds()) / 1000)))
                : "?";
            var line = state == "paused"
                ? $"workstream {ws}: {so} {word} paused {age} by {TextOf(child?.Member("paused_by")) ?? "?"}: {TextOf(child?.Member("note")) ?? "no note"}"
                : $"workstream {ws}: {so} {word} {state} \"{summary}\" {age}";
            if (TextOf(child?.Member("result")) is { } result)
            {
                line += $" result {result}";
            }

            if (record.Member("close_grants") is { IsArray: true } grants)
            {
                foreach (var grant in grants.AsArray())
                {
                    if (TextOf(grant.Member("expires_at")) is { } expires
                        && DateTimeOffset.TryParse(expires, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var until)
                        && until.ToUnixTimeMilliseconds() > now)
                    {
                        line += $" green-light {TextOf(grant.Member("verb"))} {TextOf(grant.Member("plan_token"))} until {expires}";
                    }
                }
            }

            lines.Add(line);
        }

        return lines;
    }

    public string BuildStateBlock(string dir, JsonObject resolved, string repoBasename, JsonValue? model, JsonValue? sessionId = null, JsonValue? route = null, double? now = null)
    {
        var at = now ?? NowMs;
        var owned = _config.OwnedTeamConfigs(resolved);
        var body = new List<string>();
        if (owned is not null)
        {
            var teams = new JsonArray();
            foreach (var entry in owned)
            {
                teams.Add(entry.Member("team") ?? JsonValue.Null);
            }

            body.Add(_config.OwnedTeamsLead(teams));
            foreach (var entry in owned)
            {
                var team = entry.Member("team") is { IsText: true } t ? t.AsString() : null;
                var r = entry.Member("resolved") is { IsMap: true } rv ? rv.AsObject() : new JsonObject();
                var record = _roster.ReadTeamRecord(dir, team);
                var rosterName = record.Member("roster") is { IsNull: false } rn ? JsConvert.ToJsString(rn) : "default";
                body.Add($"Team {Ah.Roster.RosterModule.TeamArgName(team)} (roster {rosterName}):");
                body.AddRange(TeamStateLines(dir, r, _config.TeamPrefix(TextOf(r.Get("cwd")), team), at));
            }
        }
        else
        {
            body.AddRange(TeamStateLines(dir, resolved, repoBasename, at));
        }

        JsonValue? eff = null;
        if (route is { IsTruthy: true } rt)
        {
            eff = rt;
        }
        else if (sessionId is { IsTruthy: true })
        {
            eff = EffectiveRoute(dir, resolved, sessionId);
        }

        var lines = new List<string> { $"HIERARCHY STATE ({dir}):" };
        lines.AddRange(body);
        lines.Add(RouteLine(eff));
        lines.Add(TierLine(resolved, model));
        try
        {
            var names = new List<string?> { null };
            names.AddRange(_roster.ListTeamNames(dir));
            var orphans = names.Where(n => _roster.TeamIsOrphaned(_roster.ReadTeamRecord(dir, n))).ToList();
            if (orphans.Count > 0)
            {
                var named = string.Join(", ", orphans.Select(n => n is { Length: > 0 } ? n : "default"));
                lines.Add($"ah: {orphans.Count} orphaned team record(s) ({named}) — their orchestrator process is gone. To take one over, ask the user, then `ah roster adopt --orchestrator-pid <your pid> --team <name>` via /wrangl:agent-team; `ah roster reap` lists them and `reap --commit` removes them.");
            }
        }
        catch (Exception)
        {
            // advisory only — never let a probe failure cost the state block
        }

        try
        {
            var misplaced = LatestRoster(dir).Count(r =>
                Member(r, "misplaced").IsTruthy && Member(r, "status") is { IsText: true } st && st.AsString() == "up" && !RecordProvenDead(r));
            if (misplaced > 0)
            {
                lines.Add($"ah: {misplaced} misplaced peer(s) — see `ah roster teams` for detail.");
            }
        }
        catch (Exception)
        {
            // advisory only — never let a probe failure cost the state block
        }

        // the Orchestrator acts on a due groom; a member's state block does not carry it
        if (!IsMemberSession(sessionId) && BacklogHealthOf(dir) is { Due: true } backlog)
        {
            lines.Add(HierarchyModule.BacklogDueLine(backlog.Reason));
        }

        return string.Join('\n', lines);
    }
}
