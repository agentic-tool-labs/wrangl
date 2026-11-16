using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// Healing a team's members against the live herdr topology: the identity pass, then (when the caller can locate the peer registry
/// and the team's directory) the registration and working-directory passes, and an explicit <c>--bind</c>. It reads and never writes.
/// </summary>
internal static class Resync
{
    /// <summary>
    /// The outcome: <paramref name="Members"/> and <paramref name="Counts"/> are null when the topology could not be asked
    /// (<paramref name="QueryOk"/> false, <paramref name="QueryError"/> says why) or when a <c>--bind</c> failed validation
    /// (<paramref name="BindError"/>), in which case nothing was applied.
    /// </summary>
    internal sealed record Result(JsonArray? Members, JsonObject? Counts, bool QueryOk, string? QueryError, string? Warning, string? BindError);

    private static bool IsObject(JsonValue v) => v.IsMap;

    /// <summary>Whether two optional JSON values are the same by JavaScript <c>===</c> (absent equals absent only).</summary>
    private static bool Same(JsonValue? a, JsonValue? b) =>
        a is null || b is null ? a is null && b is null : JsonText.Stringify(a.Value, 0) == JsonText.Stringify(b.Value, 0);

    /// <summary>Name first (durable across workspace moves), pane id second, else no match.</summary>
    private static JsonValue? MatchMemberToPane(RosterContext c, JsonValue member, IReadOnlyList<JsonValue> topology)
    {
        if (member.Member("name") is { IsTruthy: true } name)
        {
            var byName = topology.FirstOrDefault(p => Same(p.Member("name"), name));
            if (byName.IsMap)
            {
                return byName;
            }
        }

        if (member.Member("transport_id") is { IsTruthy: true } id)
        {
            var byId = topology.FirstOrDefault(p => Same(p.Member("pane_id"), id));
            if (byId.IsMap && !CarriesAnotherName(c, member, byId))
            {
                return byId;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a pane found by its recorded id carries a name that is not this member's: an id can be reused by an unrelated pane, and
    /// one that is named something else is never adopted. A pane with no name at all can still be the member's.
    /// </summary>
    private static bool CarriesAnotherName(RosterContext c, JsonValue member, JsonValue pane)
    {
        var current = pane.Member("name") is { IsTruthy: true } n ? MemberPlanning.JsConvertText(n)
            : pane.Member("title") is { IsTruthy: true } t ? MemberPlanning.JsConvertText(t) : null;
        var recorded = MemberPlanning.Text(member.Member("name"));
        if (current is null || recorded is null || current == recorded)
        {
            return false;
        }

        var want = c.Services.Config.HierarchyNameParts(recorded, c.Registry);
        var got = c.Services.Config.HierarchyNameParts(current, c.Registry);
        return want is null || got is null
            || MemberPlanning.JsConvertText(want.Get("prefix")) != MemberPlanning.JsConvertText(got.Get("prefix"))
            || MemberPlanning.JsConvertText(want.Get("role")) != MemberPlanning.JsConvertText(got.Get("role"));
    }

    /// <summary>
    /// A pane the agent list does not show because nothing agent-like runs in it (a member whose Claude exited leaves a shell), read
    /// from herdr directly. It is accepted only when herdr returns that same pane id, so a bind never adopts a pane herdr does not have.
    /// </summary>
    private static bool PaneWithoutAgent(IRosterLaunch launch, string paneId, Dictionary<string, JsonValue> found)
    {
        try
        {
            if (launch.Herdr.Invoke(["pane", "get", paneId]).Member("result")?.Member("pane") is { IsMap: true } pane && Text(pane.Member("pane_id")) == paneId)
            {
                found[paneId] = pane;
                return true;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // herdr has no such pane or could not answer: the bind is refused as not live
        }

        return false;
    }

    private static JsonObject Healed(JsonValue member, JsonValue pane)
    {
        var healed = member.AsObject().Clone();
        SetOrRemove(healed, "transport_id", pane.Member("pane_id"));
        SetOrRemove(healed, "tab_id", pane.Member("tab_id"));
        SetOrRemove(healed, "workspace_id", pane.Member("workspace_id"));
        healed.Remove("transport_stale");
        return healed;
    }

    private static void SetOrRemove(JsonObject target, string key, JsonValue? value)
    {
        if (value is { } v)
        {
            target[key] = v;
        }
        else
        {
            target.Remove(key);
        }
    }

    private static JsonObject Place(JsonValue? transportId, JsonValue? tabId, JsonValue? workspaceId)
    {
        var o = new JsonObject();
        if (transportId is { } t)
        {
            o["transport_id"] = t;
        }

        if (tabId is { } tab)
        {
            o["tab_id"] = tab;
        }

        if (workspaceId is { } ws)
        {
            o["workspace_id"] = ws;
        }

        return o;
    }

    private static JsonObject Counts(int updated, int unchanged, int notFound, int skipped, int malformed, int ambiguous) => new()
    {
        ["updated"] = updated,
        ["unchanged"] = unchanged,
        ["not_found"] = notFound,
        ["skipped"] = skipped,
        ["malformed"] = malformed,
        ["ambiguous"] = ambiguous,
    };

    internal static Result ResyncMembers(
        RosterContext c, JsonValue team, string? dir = null, string? teamCwd = null, string? selfPaneId = null, JsonObject? bind = null)
    {
        var launch = c.Services.Launch!;
        var config = c.Services.Config;
        IReadOnlyList<JsonValue> topology;
        try
        {
            topology = launch.Topology.Query().ToList();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new Result(null, null, false, error.Message, null, null);
        }

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = false;
        int updated = 0, unchanged = 0, notFound = 0, skipped = 0, malformed = 0, ambiguous = 0;
        var teamMembers = team.Member("members") is { IsArray: true } list ? list.AsArray().ToList() : [];
        var members = new List<JsonValue>();
        foreach (var m in teamMembers)
        {
            if (!IsObject(m))
            {
                malformed++;
                members.Add(m);
                continue;
            }

            if (!config.RouteHasPane(m.Member("route")))
            {
                skipped++;
                var s = m.AsObject().Clone();
                s["status"] = "skipped";
                members.Add(s);
                continue;
            }

            var match = MatchMemberToPane(c, m, topology);
            if (match is { } found && claimed.Contains(Text(found.Member("pane_id"))))
            {
                duplicate = true;
                match = null;
            }

            if (match is not { } pane)
            {
                notFound++;
                var nf = m.AsObject().Clone();
                nf["status"] = "not_found";
                nf["transport_stale"] = true;
                members.Add(nf);
                continue;
            }

            claimed.Add(Text(pane.Member("pane_id")));
            var from = Place(m.Member("transport_id"), m.Member("tab_id"), m.Member("workspace_id"));
            var to = Place(pane.Member("pane_id"), pane.Member("tab_id"), pane.Member("workspace_id"));
            var healed = Healed(m, pane);
            if (!Same(m.Member("transport_id"), pane.Member("pane_id")) || !Same(m.Member("tab_id"), pane.Member("tab_id"))
                || !Same(m.Member("workspace_id"), pane.Member("workspace_id")))
            {
                updated++;
                healed["status"] = "updated";
                healed["from"] = from;
                healed["to"] = to;
            }
            else
            {
                unchanged++;
                healed["status"] = "unchanged";
            }

            members.Add(healed);
        }

        if (bind is not null)
        {
            var topologyIds = new HashSet<string>(topology.Select(p => Text(p.Member("pane_id"))), StringComparer.Ordinal);
            var knownNames = members.Where(IsObject).Select(m => m.Member("name")).Where(n => n is { IsTruthy: true })
                .Select(n => MemberPlanning.JsConvertText(n)).ToList();
            var willClaim = new HashSet<string>(claimed, StringComparer.Ordinal);
            var panesWithoutAgent = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            foreach (var name in bind.Keys)
            {
                var paneId = MemberPlanning.JsConvertText(bind[name]);
                var idx = members.FindIndex(m => IsObject(m) && MemberPlanning.Text(m.Member("name")) == name);
                if (idx == -1)
                {
                    return BindFailure($"--bind: unknown member \"{name}\" (known: {(knownNames.Count > 0 ? string.Join(", ", knownNames) : "none")})");
                }

                if (!config.RouteHasPane(members[idx].Member("route")))
                {
                    return BindFailure($"--bind: member \"{name}\" has route \"{MemberPlanning.JsConvertText(members[idx].Member("route"))}\", not \"peer\"");
                }

                if (!topologyIds.Contains(paneId) && !PaneWithoutAgent(launch, paneId, panesWithoutAgent))
                {
                    return BindFailure($"--bind: pane \"{paneId}\" (for \"{name}\") is not in live herdr topology");
                }

                if (willClaim.Contains(paneId))
                {
                    return BindFailure($"--bind: pane \"{paneId}\" (for \"{name}\") is already claimed");
                }

                if (selfPaneId is not null && paneId == selfPaneId)
                {
                    return BindFailure($"--bind: pane \"{paneId}\" (for \"{name}\") is the caller's own pane");
                }

                willClaim.Add(paneId);
            }

            foreach (var name in bind.Keys)
            {
                var paneId = MemberPlanning.JsConvertText(bind[name]);
                var idx = members.FindIndex(m => IsObject(m) && MemberPlanning.Text(m.Member("name")) == name);
                var m = members[idx];
                var topo = panesWithoutAgent.TryGetValue(paneId, out var bare) ? bare : topology.First(p => Text(p.Member("pane_id")) == paneId);
                var from = Place(m.Member("transport_id"), m.Member("tab_id"), m.Member("workspace_id"));
                var to = Place(topo.Member("pane_id"), topo.Member("tab_id"), topo.Member("workspace_id"));
                var healed = Healed(m, topo);
                claimed.Add(paneId);
                switch (MemberPlanning.Text(m.Member("status")))
                {
                    case "not_found":
                        notFound--;
                        break;
                    case "unchanged":
                        unchanged--;
                        break;
                    case "updated":
                        updated--;
                        break;
                }

                updated++;
                healed["status"] = "updated";
                healed["from"] = from;
                healed["to"] = to;
                healed["match_by"] = "bind";
                members[idx] = healed;
            }
        }

        List<(JsonValue M, int I)> AwaitingRepair() => members
            .Select((m, i) => (m, i))
            .Where(x => IsObject(x.m) && MemberPlanning.Text(x.m.Member("status")) == "not_found" && x.m.Member("transport_id") is not { IsNull: false })
            .ToList();

        if (dir is not null && teamCwd is not null)
        {
            var messages = c.Services.Hierarchy.Messages;
            var teamCwdReal = messages.RealCwd(teamCwd);
            var liveRoster = c.Services.Hierarchy.Roster.LatestRoster(dir).ToList();
            var topologyIds = new HashSet<string>(topology.Select(p => Text(p.Member("pane_id"))), StringComparer.Ordinal);
            foreach (var (m, i) in AwaitingRepair())
            {
                var candidates = liveRoster.Where(rec =>
                    MemberPlanning.Text(rec.Member("status")) == "up"
                    && c.Services.Hierarchy.Core.IdentityLive(rec.Member("pid") ?? JsonValue.Null, rec.Member("pid_start"))
                    && rec.Member("pane_id") is { IsNull: false } paneId
                    && rec.Member("cwd") is { IsNull: false } recCwd
                    && messages.RealCwd(MemberPlanning.JsConvertText(recCwd)) == teamCwdReal
                    && Same(rec.Member("role"), m.Member("role"))
                    && topologyIds.Contains(MemberPlanning.JsConvertText(paneId))
                    && !claimed.Contains(MemberPlanning.JsConvertText(paneId))
                    && !(selfPaneId is not null && MemberPlanning.JsConvertText(paneId) == selfPaneId)).ToList();
                if (candidates.Count != 1)
                {
                    continue;
                }

                var recPane = MemberPlanning.JsConvertText(candidates[0].Member("pane_id"));
                var topo = topology.First(p => Text(p.Member("pane_id")) == recPane);
                claimed.Add(recPane);
                notFound--;
                updated++;
                var from = Place(JsonValue.Null, m.Member("tab_id"), m.Member("workspace_id"));
                var to = Place(topo.Member("pane_id"), topo.Member("tab_id"), topo.Member("workspace_id"));
                var healed = Healed(m, topo);
                healed["status"] = "updated";
                healed["from"] = from;
                healed["to"] = to;
                healed["match_by"] = "peers_jsonl";
                members[i] = healed;
            }
        }

        if (teamCwd is not null)
        {
            var messages = c.Services.Hierarchy.Messages;
            var teamCwdReal = messages.RealCwd(teamCwd);
            var raw = topology
                .Where(p => p.Member("cwd") is { IsNull: false } pc
                    && messages.RealCwd(MemberPlanning.JsConvertText(pc)) == teamCwdReal
                    && !claimed.Contains(Text(p.Member("pane_id"))))
                .ToList();
            var candidatePanes = raw.Where(p => !(selfPaneId is not null && Text(p.Member("pane_id")) == selfPaneId)).ToList();
            var awaiting = AwaitingRepair();
            var autoBind = selfPaneId is not null && candidatePanes.Count == 1 && awaiting.Count == 1;
            foreach (var (m, i) in awaiting)
            {
                if (autoBind)
                {
                    var topo = candidatePanes[0];
                    claimed.Add(Text(topo.Member("pane_id")));
                    notFound--;
                    updated++;
                    var from = Place(JsonValue.Null, m.Member("tab_id"), m.Member("workspace_id"));
                    var to = Place(topo.Member("pane_id"), topo.Member("tab_id"), topo.Member("workspace_id"));
                    var healed = Healed(m, topo);
                    healed["status"] = "updated";
                    healed["from"] = from;
                    healed["to"] = to;
                    healed["match_by"] = "cwd";
                    members[i] = healed;
                }
                else if (raw.Count == 0)
                {
                    var clean = m.AsObject().Clone();
                    clean.Remove("transport_stale");
                    clean["status"] = "not_found";
                    members[i] = clean;
                }
                else
                {
                    notFound--;
                    ambiguous++;
                    var amb = m.AsObject().Clone();
                    amb["status"] = "ambiguous";
                    var cand = new JsonArray();
                    foreach (var p in candidatePanes)
                    {
                        var o = new JsonObject();
                        foreach (var key in new[] { "pane_id", "tab_id", "workspace_id", "cwd" })
                        {
                            if (p.Member(key) is { } v)
                            {
                                o[key] = v;
                            }
                        }

                        cand.Add(o);
                    }

                    amb["candidates"] = cand;
                    members[i] = amb;
                }
            }
        }

        var outMembers = new JsonArray();
        outMembers.AddRange(members);
        return new Result(outMembers, Counts(updated, unchanged, notFound, skipped, malformed, ambiguous), true, null, duplicate ? "duplicate pane match" : null, null);
    }

    private static Result BindFailure(string message) => new(null, null, true, null, null, message);

    private static string Text(JsonValue? v) => v is { } x ? MemberPlanning.JsConvertText(x) : "undefined";

    /// <summary>Strips the per-pass bookkeeping fields before a healed member array is persisted; a non-object passes through untouched.</summary>
    internal static JsonValue StripMeta(JsonValue m)
    {
        if (!m.IsMap)
        {
            return m;
        }

        var copy = m.AsObject().Clone();
        foreach (var key in new[] { "status", "from", "to", "match_by", "candidates" })
        {
            copy.Remove(key);
        }

        return copy;
    }

    /// <summary>The no-op reason for a non-herdr transport, shared by <c>resync</c> and <c>move</c>.</summary>
    internal static string TransportNoop(string? transport) =>
        transport == "tmux" ? "transport tmux not supported (resync reads herdr panes only)" : "transport terminal has no panes";
}
