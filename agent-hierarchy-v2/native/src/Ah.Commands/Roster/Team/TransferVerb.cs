using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// <c>transfer &lt;member&gt; --to &lt;team&gt; [--stream &lt;ws&gt;] [--dry-run]</c>: hands a member of this team to a lane's sub-orchestrator, or back
/// to this team's Orchestrator. The member keeps its pane, its worktree and its briefs; only who it answers to changes, through the <c>lead</c> of its row.
/// </summary>
internal static class TransferVerb
{
    private const string Orchestrator = "orchestrator";

    private const string Usage = "ah roster transfer <member> --to <team> [--stream <ws>] [--dry-run]";

    private static readonly string[] Flags = ["to", "stream", "dry-run", "replace-live", "cwd", "team"];

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"transfer: unrecognized flag --{key}: {Usage}");
            }
        }

        var name = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : throw new RosterFailure($"transfer needs a member name: {Usage}");
        var to = c.Opts.Text("to") is { Length: > 0 } target ? target : throw new RosterFailure($"transfer needs --to <team>: {Usage}");
        var services = c.Services;
        var dir = c.Dir;
        var valid = JsonValue.FromObject(services.Config.ValidateTeamAlias(to, null));
        if (valid.Member("ok") is not { IsTruthy: true })
        {
            throw new RosterFailure($"transfer: --to {to} is not a team name ({MemberPlanning.Text(valid.Member("why"))}): {Usage}");
        }

        var home = c.TeamFile ?? services.Config.TeamPrefix(c.Cwd, null);
        var team = services.Roster.ReadTeamRecord(dir, c.TeamFile);
        var rows = team.Member("members") is { IsArray: true } list ? list.AsArray().ToList() : [];
        var row = rows.Cast<JsonValue?>().FirstOrDefault(m => MemberPlanning.Text(m!.Value.Member("name")) == name);

        if (name == Orchestrator)
        {
            throw Refuse(c, "member-is-orchestrator", $"transfer: {name} is the Orchestrator, not a member; nothing was changed");
        }

        if (row is not { } member)
        {
            var known = string.Join(", ", rows.Select(m => MemberPlanning.Text(m.Member("name"))).Where(n => !string.IsNullOrEmpty(n)));
            throw Refuse(c, "unknown-member", $"transfer: no member named {name} — known members: {(known.Length > 0 ? known : "(none)")}; nothing was changed");
        }

        var newLead = ResolveTarget(c, dir, home, to, team, out var stream);
        if (newLead is not null && newLead.Name == name)
        {
            throw Refuse(c, "lead-is-member", $"transfer: {name} cannot answer to itself; nothing was changed");
        }

        var current = HierarchyModule.LeadOf(member);
        if (Same(current, newLead))
        {
            throw Refuse(c, "already-there", $"transfer: {name} already answers to {Describe(newLead, home)}; nothing was changed");
        }

        var oldEffective = HierarchyModule.EffectiveLead(home, member, team => services.Roster.ReadTeamRecord(dir, team), services.Hierarchy.Core.IdentityLive);
        var newEffective = newLead ?? new LeadRef(home, Orchestrator);
        var dry = c.Opts.IsTrue("dry-run");
        var eventId = dry ? null : services.Hierarchy.Core.NewId();
        var role = MemberPlanning.Text(member.Member("role"));

        JsonObject Describing(JsonObject result)
        {
            result["transfer"] = dry ? "planned" : "done";
            result["member"] = name;
            result["from"] = Ref(oldEffective);
            result["to"] = Ref(newEffective);
            result["lead"] = newLead is null ? JsonValue.Null : Ref(newLead);
            if (dry)
            {
                result["open_requests"] = OpenRequests(c, dir, name, role);
                result["checklist_items"] = JsonValue.Null;
                var recipients = new JsonArray();
                recipients.AddRange(Recipients(name, home, oldEffective, newEffective).Select(a => (JsonValue)a));
                result["recipients"] = recipients;
            }
            else
            {
                result["note"] = $"applies now for routing; {name} learns it at its next turn (event {eventId})";
            }

            return result;
        }

        bool Concurrent(JsonObject row) => !Same(HierarchyModule.LeadOf(row), current);

        if (c.Opts.Has("stream"))
        {
            var edited = false;
            return MoveVerb.MoveToStream(
                c,
                dir,
                team,
                member,
                name,
                moved =>
                {
                    if (Concurrent(moved))
                    {
                        throw Refuse(c, "lead-changed", $"transfer: {name} changed lead while this transfer ran; nothing was changed");
                    }

                    edited = true;
                    return WithLead(moved, newLead);
                },
                result =>
                {
                    _ = Describing(result);
                    result["transferred"] = !dry && edited;
                    if (!dry && edited)
                    {
                        result["event"] = Announce(c, dir, eventId!, name, home, oldEffective, newEffective, c.Opts.Text("stream"));
                    }
                });
        }

        if (dry)
        {
            c.Out(Describing(new JsonObject { ["transferred"] = false, ["dry_run"] = true }));
            return 0;
        }

        var wrote = false;
        services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            doc =>
            {
                if (doc.IsNull)
                {
                    return null;
                }

                var next = new JsonArray();
                foreach (var r in doc.Member("members") is { IsArray: true } l ? l.AsArray() : [])
                {
                    if (r.IsMap && MemberPlanning.Text(r.Member("name")) == name)
                    {
                        if (Concurrent(r.AsObject()))
                        {
                            throw Refuse(c, "lead-changed", $"transfer: {name} changed lead while this transfer ran; nothing was changed");
                        }

                        wrote = true;
                        next.Add(WithLead(r.AsObject().Clone(), newLead));
                    }
                    else
                    {
                        next.Add(r);
                    }
                }

                if (!wrote)
                {
                    throw Refuse(c, "unknown-member", $"transfer: {name} is no longer a member; nothing was changed");
                }

                var updated = doc.AsObject().Clone();
                updated["members"] = next;
                return TeamFileOutcome.Write(updated);
            });
        if (!wrote)
        {
            throw Refuse(c, "no-write", $"transfer: the team file was not written; nothing was changed");
        }

        var output = Describing(new JsonObject { ["transferred"] = true });
        output["event"] = Announce(c, dir, eventId!, name, home, oldEffective, newEffective, null);
        c.Out(output);
        return 0;
    }

    private static string[] Recipients(string member, string home, LeadRef from, LeadRef to) =>
        [$"{member}@{home}", HierarchyModule.LeadAddress(from), HierarchyModule.LeadAddress(to)];

    /// <summary>
    /// The open requests addressed to the member: by the <c>to_name</c> of the request, or by its role when it names nobody. These stay
    /// answerable to whoever sent them. Requests the member sent are not listed.
    /// </summary>
    private static JsonArray OpenRequests(RosterContext c, string dir, string member, string? role)
    {
        var list = new JsonArray();
        foreach (var e in c.Services.Hierarchy.Core.ListExchanges([dir], live: true))
        {
            if (e.Member("open") is not { IsTruthy: true })
            {
                continue;
            }

            var named = RequestField(c, e, "to_name");
            var mine = named is not null ? named == member : role is { Length: > 0 } && MemberPlanning.Text(e.Member("to")) == role;
            if (mine)
            {
                list.Add(new JsonObject { ["id"] = e.Member("id")!.Value, ["slug"] = e.Member("slug") ?? JsonValue.Null });
            }
        }

        return list;
    }

    private static string? RequestField(RosterContext c, JsonValue exchange, string key)
    {
        if (exchange.Member("request")?.Member("path") is not { IsText: true } path)
        {
            return null;
        }

        try
        {
            var started = false;
            foreach (var line in (MemberPlanning.Text(c.Services.Hierarchy.Core.ReadMsgFile(path.AsString()).Member("text")) ?? string.Empty).Split('\n'))
            {
                if (line.TrimEnd() == "---")
                {
                    if (started)
                    {
                        return null;
                    }

                    started = true;
                }
                else if (line.StartsWith(key + ":", StringComparison.Ordinal))
                {
                    return line[(key.Length + 1)..].Trim() is { Length: > 0 } value ? value : null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static JsonObject WithLead(JsonObject row, LeadRef? lead)
    {
        if (lead is null)
        {
            _ = row.Remove("lead");
        }
        else
        {
            row["lead"] = Ref(lead);
        }

        return row;
    }

    private static JsonObject Ref(LeadRef lead) => new() { ["team"] = lead.Team, ["name"] = lead.Name };

    private static bool Same(LeadRef? a, LeadRef? b) => a is null ? b is null : b is not null && a.Equals(b);

    private static string Describe(LeadRef? lead, string home) => lead is null ? $"the Orchestrator of {home}" : $"{lead.Name}@{lead.Team}";

    private static RosterFailure Refuse(RosterContext c, string code, string text) =>
        c.Refuse(new JsonObject { ["refused"] = code, ["message"] = text }, text);

    /// <summary>The lead <c>--to</c> names (null: the home Orchestrator), refusing a target that is not a running lane of this team.</summary>
    private static LeadRef? ResolveTarget(RosterContext c, string dir, string home, string to, JsonValue team, out string? stream)
    {
        stream = null;
        if (to == home)
        {
            return null;
        }

        var target = c.Services.Roster.ReadTeamRecord(dir, to);
        if (target.IsNull
            || target.Member("parent") is not { IsMap: true } parent
            || MemberPlanning.Text(parent.Member("team")) != home
            || MemberPlanning.Text(parent.Member("stream")) is not { Length: > 0 } laneStream)
        {
            throw Refuse(c, "target-not-lane", $"transfer: {to} is not a lane of {home} (a lane is a child team made for one of this team's streams); nothing was changed");
        }

        stream = laneStream;
        if (c.Opts.Text("stream") is { Length: > 0 } asked && asked != laneStream)
        {
            throw Refuse(c, "stream-not-lane", $"transfer: --stream {asked} is not the stream of {to} ({laneStream}); nothing was changed");
        }

        var record = c.Services.Launch!.Streams.StreamsOf(team).Get(laneStream);
        var streamDone = record is not { IsTruthy: true } found || MemberPlanning.Text(found.Member("state")) == "done";
        if (streamDone || MemberPlanning.Text(target.Member("workstream")?.Member("state")) == "done")
        {
            throw Refuse(c, "lane-closed", $"transfer: the lane {to} is done; nothing was changed");
        }

        var owner = record is { } r ? MemberPlanning.Text(r.Member("sub_orchestrator")) : null;
        if (string.IsNullOrEmpty(owner) || !LeadLive(c, dir, new LeadRef(to, owner)))
        {
            throw Refuse(c, "lane-no-so", $"transfer: the lane {to} has no live sub-orchestrator; retry once it is up (after a restart it comes back on its own); nothing was changed");
        }

        return new LeadRef(to, owner);
    }

    /// <summary>Whether the lead's process is live: the lead is the Orchestrator of its team, whose process the team file records.</summary>
    private static bool LeadLive(RosterContext c, string dir, LeadRef lead) =>
        HierarchyModule.OrchestratorLive(c.Services.Roster.ReadTeamRecord(dir, lead.Team), c.Services.Hierarchy.Core.IdentityLive);

    // TODO: also append a team-history `transfer` row once a team-history store that records events (not only roster fingerprints) is on the train; until then this event is the only audit.
    /// <summary>One <c>member-transfer</c> event under one id to the member, its old effective lead and its new one; the addresses it reached.</summary>
    internal static JsonValue Announce(RosterContext c, string dir, string id, string member, string home, LeadRef from, LeadRef to, string? stream)
    {
        var mail = c.Services.EventMail;
        if (mail is null)
        {
            return new JsonObject { ["id"] = id, ["sent"] = false, ["warning"] = "no mailbox on this host: no member-transfer event was sent, so the member is not told and the audit has no event" };
        }

        var data = new JsonObject { ["member"] = member, ["from"] = Ref(from), ["to"] = Ref(to) };
        if (stream is not null)
        {
            data["stream"] = stream;
        }

        var text = $"{member} now answers to {HierarchyModule.LeadAddress(to)} (was {HierarchyModule.LeadAddress(from)}); it applies now for routing, {member} learns it at its next turn";
        var reached = mail.SendEvent(c.Cwd, dir, id, "member-transfer", text, data, Recipients(member, home, from, to));
        var list = new JsonArray();
        list.AddRange(reached.Select(a => (JsonValue)a));
        return new JsonObject { ["id"] = id, ["reached"] = list };
    }
}
