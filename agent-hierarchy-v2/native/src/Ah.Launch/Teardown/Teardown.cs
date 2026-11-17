using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Codex;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Mux;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.Roster;

namespace Ah.Launch;

/// <summary>
/// The scope a teardown or listing reads for: a team (null for the default team), or no team at all (the branch with no team file,
/// where herdr is the only registry a loose peer can be in).
/// </summary>
public sealed record TeamScope(string? Team, bool NoTeam = false);

/// <summary>The herdr topology arm: what herdr could answer, and the live panes whose names say they belong to a scope.</summary>
public sealed record HerdrArm(bool Ok, string? Reason, JsonArray Agents, string Prefix, JsonArray Matched);

/// <summary>A pane's peer target, or the message of the ambiguity that ends the command.</summary>
public sealed record PeerTarget(JsonValue? Member, string? Fail);

/// <summary>Closes verified: <paramref name="Results"/> are the per-target rows, <paramref name="StillLive"/> the names still listed.</summary>
public sealed record CloseVerification(IReadOnlyList<JsonObject> Results, IReadOnlyList<string> StillLive);

/// <summary>What <see cref="ITeamRowStore.UpdateRows"/> did: whether the team file was cleared, and the rows it kept (null when nothing was written).</summary>
public sealed record TeamRowsUpdate(bool Cleared, IReadOnlyList<JsonValue>? Rows);

/// <summary>The team file's rows, rewritten through the <c>team</c> state domain.</summary>
public interface ITeamRowStore
{
    /// <summary>
    /// Rewrites the team file at <paramref name="path"/> with the rows <paramref name="pick"/> chooses from the file as it stands (null leaves it
    /// alone), or clears the file when no row is left: a team's last departure ends it. A pane member that leaves takes its activity record.
    /// </summary>
    /// <remarks>With <paramref name="keepFile"/> the file stays, with an empty member list, when no row is left: a lane child's file also records its leader and its parent.</remarks>
    /// <remarks>A non-null <paramref name="journal"/> entry is appended to the file's <c>parent_actions</c> list (newest last, the last 20 kept) in the same write.</remarks>
    TeamRowsUpdate UpdateRows(string dir, string path, Func<JsonObject, IReadOnlyList<JsonValue>?> pick, bool keepFile = false, JsonObject? journal = null);
}

/// <summary>The outcome of pruning a team file after a whole-team close.</summary>
public sealed record ReconcileOutcome(IReadOnlyList<string> Pruned, IReadOnlyList<JsonObject> Kept, bool TeamRemoved, string? TeamFile);

/// <summary>
/// Teardown and listing over the live registries: peers.jsonl rows plus the herdr topology arm, the close set, the verified close, and
/// the reconcile of the team file afterwards.
/// </summary>
public interface ITeardown
{
    /// <summary>
    /// The live herdr panes whose names say they belong to this scope's hierarchy, in <c>livePeerSlots</c> shape. Being listed by herdr IS the
    /// liveness signal. The pane running the command is never included; a pane reporting a cwd in another checkout is excluded.
    /// </summary>
    HerdrArm HerdrArmMatches(string dir, TeamScope scope, LaunchContext context);

    /// <summary>The live registry for a scope: peers.jsonl rows plus the herdr arm's panes that no known row already names.</summary>
    JsonArray LiveRegistrySlots(string dir, TeamScope scope, LaunchContext context, IReadOnlyList<JsonValue>? known = null);

    /// <summary>Live peers attributed to <paramref name="scope"/> that no team row names (<paramref name="tracked"/> are the member names a team file records).</summary>
    JsonArray UntrackedLive(string dir, TeamScope scope, IReadOnlySet<string> tracked, LaunchContext context);

    /// <summary>Which sources an answer consulted and what each yielded: <c>{team, peers, herdr}</c>.</summary>
    JsonObject SourcesField(string dir, TeamScope scope, JsonValue team, LaunchContext context);

    /// <summary>The live peer records shaped like team members, so the close helpers apply to them verbatim.</summary>
    JsonArray PeerFallbackMembers(string dir, TeamScope scope, LaunchContext context, IReadOnlyList<JsonValue>? known = null);

    /// <summary>Live registry peers that are not already one of <paramref name="members"/> (a pane hosts exactly one session).</summary>
    JsonArray PeerExtras(string dir, IReadOnlyList<JsonValue> members, TeamScope scope, LaunchContext context);

    /// <summary>
    /// The peer a name stands for, first match winning by pane id, session id, a unique prefix of at least eight characters, name and
    /// last the herdr display name. Two matches never pick: the answer is the ambiguity.
    /// </summary>
    PeerTarget ResolvePeerTarget(IReadOnlyList<JsonValue> fallback, string name, string? teamTransport, LaunchContext context);

    /// <summary>
    /// Closes every target, then re-queries the transport until each is gone or the deadline passes. A result's <c>closed</c> means
    /// verified gone, not that the close command ran. Appends a warning to <paramref name="warnings"/> for every shortfall.
    /// </summary>
    CloseVerification CloseAndVerify(IReadOnlyList<JsonObject> closable, string teamTransport, List<string> warnings, LaunchContext context, JsonValue? teamRecord = null, string? dir = null);

    /// <summary>After a whole-team close, row by row over a fresh read: a closed session's row goes, one whose session may still exist stays, with the reason.</summary>
    ReconcileOutcome ReconcileAfterClose(string dir, JsonValue snapshot, IReadOnlyList<JsonObject> results, string path, LaunchContext context);

    /// <summary>The short hash that binds <c>--plan-token</c> to the exact close set: the team id and the sorted non-null pane ids.</summary>
    string CloseToken(JsonValue teamId, IReadOnlyList<JsonValue> closable);

    /// <summary>The members with something addressable to close: a pane or mail route, with a <c>transport_id</c>.</summary>
    IReadOnlyList<JsonValue> ClosableMembers(IReadOnlyList<JsonValue> members);

    /// <summary>
    /// Liveness for one member, kind-aware and three-valued: <c>{live, indeterminate, why, agent_status, ready}</c>. Callers treat
    /// <c>indeterminate</c> as refuse, never as dead.
    /// </summary>
    JsonObject MemberLiveness(string dir, JsonValue member, string? team = null);
}

/// <summary>The teardown rules. <paramref name="CheckoutRoot"/> gives a directory's checkout root (its main worktree), or null.</summary>
public sealed record Teardown(
    IAhEnvironment Environment,
    IFileReader Files,
    IConfigCore Config,
    IRosterCore Roster,
    IMemberRegistry Registry,
    IHierarchyRoster HierarchyRoster,
    IHierarchyMessages Messages,
    IHerdr Herdr,
    IHerdrTopology Topology,
    IMuxRunner Mux,
    IClock Clock,
    ISleeper Sleeper,
    IAppserverLauncher Appserver,
    ITeamRowStore Rows,
    Func<string, string?> CheckoutRoot) : ITeardown
{
    /// <summary>A session's registration document, or null; with none, a member's herdr flavour is its team's, else the env's.</summary>
    public Func<string, JsonObject?>? Registration { get; init; }

    /// <summary>The session id of a member's live registration (dir, team file, member row), for a row that records none; see <see cref="ISnapshotBuilder.SessionIdOf"/>.</summary>
    public Func<string, string?, JsonValue, JsonValue?>? SessionOf { get; init; }

    /// <summary>
    /// The herdr adapter of the server <paramref name="member"/> lives on: <see cref="Ah.Core.Mux.HerdrFlavour.Resolve"/> over its
    /// registration, <paramref name="teamRecord"/>, and the env's flavour.
    /// </summary>
    private IHerdr HerdrOf(JsonValue member, JsonValue? teamRecord)
    {
        var session = member.Member("session_id") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null;
        var flavour = Ah.Core.Mux.HerdrFlavour.Resolve(session is null ? null : Registration?.Invoke(session), teamRecord, Herdr.Flavour, member);
        return flavour is null || flavour == Herdr.Flavour || Herdr is not global::Ah.Launch.Herdr env ? Herdr : env.ForFlavour(flavour);
    }

    /// <summary>The topology of <paramref name="herdr"/>'s server: <see cref="Topology"/> when it is the env's.</summary>
    private IHerdrTopology TopologyOf(IHerdr herdr) => ReferenceEquals(herdr, Herdr) ? Topology : new HerdrTopology(herdr);

    private const double VerifyPollMs = 250;

    // The deadline is a guess at herdr's close latency; raise it if a slow herdr keeps reporting panes still live.
    private const double VerifyDeadlineMs = 3000;

    public HerdrArm HerdrArmMatches(string dir, TeamScope scope, LaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(context);
        var query = ArmApplies(dir, scope) ? ArmQuery() : new HerdrArm(false, "team transport is not herdr", new JsonArray(), string.Empty, new JsonArray());
        var prefix = ScopePrefix(scope, context);
        var selfPane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(Environment.GetVariable);
        var real = Messages.RealCwd(context.Cwd);
        var myRoot = Config.FindGitRoot(real);
        var myCheckout = CheckoutRoot(real);
        var prefixes = !string.IsNullOrEmpty(context.TeamArg) || myCheckout is null
            ? [prefix]
            : new List<string> { prefix, NodePath.Basename(myCheckout) }.Distinct(StringComparer.Ordinal).ToList();
        (HashSet<string> Panes, HashSet<string> Names)? others = null;
        var matched = new JsonArray();
        foreach (var a in query.Agents)
        {
            var nameValue = a.Member("name") is { IsTruthy: true } n ? n : a.Member("title") is { IsTruthy: true } t ? t : (JsonValue?)null;
            var key = nameValue is { } kv ? JsConvert.ToJsString(kv) : null;
            if (key is null || Config.HierarchyNameParts(key, context.Registry) is not { } parts)
            {
                continue;
            }

            var paneId = a.Member("pane_id");
            if (!string.IsNullOrEmpty(selfPane) && paneId is { IsText: true } pid && pid.AsString() == selfPane)
            {
                continue;
            }

            var agentCwd = a.Member("cwd") is { IsTruthy: true } c ? JsConvert.ToJsString(c) : null;
            var partsPrefix = JsConvert.ToJsString(parts.Get("prefix"));
            var sameRoot = partsPrefix == prefix
                && !(agentCwd is not null && (myRoot is null || Config.FindGitRoot(Messages.RealCwd(agentCwd)) != myRoot));
            var stray = false;
            if (!sameRoot)
            {
                if (!prefixes.Contains(partsPrefix))
                {
                    continue;
                }

                if (agentCwd is not null && (myCheckout is null || CheckoutRoot(Messages.RealCwd(agentCwd)) != myCheckout))
                {
                    continue;
                }

                if (parts.Get("role") is { IsText: true } role && role.AsString() == "orchestrator")
                {
                    continue;
                }

                others ??= OtherTeamMemberKeys(dir);
                if ((paneId is { IsText: true } p && others.Value.Panes.Contains(p.AsString())) || others.Value.Names.Contains(key))
                {
                    continue;
                }

                stray = true;
            }

            var slot = new JsonObject
            {
                ["name"] = key,
                ["role"] = parts.Get("role") ?? JsonValue.Null,
                ["pid"] = JsonValue.Null,
                ["pane_id"] = paneId ?? JsonValue.Null,
                ["session_id"] = a.Member("session_id") is { IsTruthy: true } sid ? sid : JsonValue.Null,
                ["cwd"] = agentCwd is not null ? agentCwd : JsonValue.Null,
                ["live"] = true,
                ["how"] = $"herdr agent list ({(a.Member("name") is { IsTruthy: true } ? "name" : "title")})",
                ["source"] = "herdr",
            };
            if (stray)
            {
                slot["stray"] = true;
            }

            matched.Add(slot);
        }

        return new HerdrArm(query.Ok, query.Reason, query.Agents, prefix, matched);
    }

    public JsonArray LiveRegistrySlots(string dir, TeamScope scope, LaunchContext context, IReadOnlyList<JsonValue>? known = null)
    {
        var peers = LivePeerSlotsAcross(dir, scope);
        var all = new List<JsonValue>(peers);
        all.AddRange(known ?? []);
        var slots = new JsonArray();
        slots.AddRange(peers);
        slots.AddRange(HerdrArmSlots(dir, scope, all, context));
        return slots;
    }

    public JsonArray UntrackedLive(string dir, TeamScope scope, IReadOnlySet<string> tracked, LaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        var rows = new JsonArray();
        foreach (var s in LiveRegistrySlots(dir, scope, context))
        {
            if (s.Member("live") is not { IsTruthy: true })
            {
                continue;
            }

            if (s.Member("name") is { IsText: true } name && tracked.Contains(name.AsString()))
            {
                continue;
            }

            var row = new JsonObject();
            foreach (var key in new[] { "name", "role", "pane_id", "session_id", "pid", "cwd" })
            {
                if (s.Member(key) is { } v)
                {
                    row[key] = v;
                }
            }

            if (s.Member("source") is { IsText: true } source && source.AsString() == "herdr")
            {
                row["source"] = "herdr";
            }

            rows.Add(row);
        }

        return rows;
    }

    public JsonObject SourcesField(string dir, TeamScope scope, JsonValue team, LaunchContext context)
    {
        var arm = HerdrArmMatches(dir, scope, context);
        var live = LivePeerSlotsAcross(dir, scope).Count(s => s.Member("live") is { IsTruthy: true });
        var teamFile = team.IsTruthy ? Roster.TeamPath(dir, context.TeamFile) : null;
        var members = team.IsTruthy && team.Member("members") is { IsArray: true } m ? m.AsArray().Count : 0;
        var herdr = arm.Ok
            ? new JsonObject { ["ok"] = true, ["agents"] = arm.Agents.Count, ["matched"] = arm.Matched.Count, ["prefix"] = arm.Prefix }
            : new JsonObject { ["ok"] = false, ["reason"] = arm.Reason is not null ? arm.Reason : JsonValue.Null, ["prefix"] = arm.Prefix };
        return new JsonObject
        {
            ["team"] = new JsonObject { ["file"] = teamFile is not null ? teamFile : JsonValue.Null, ["members"] = members },
            ["peers"] = new JsonObject { ["live"] = live },
            ["herdr"] = herdr,
        };
    }

    public JsonArray PeerFallbackMembers(string dir, TeamScope scope, LaunchContext context, IReadOnlyList<JsonValue>? known = null)
    {
        var members = new JsonArray();
        foreach (var s in LiveRegistrySlots(dir, scope, context, known))
        {
            if (s.Member("live") is not { IsTruthy: true })
            {
                continue;
            }

            var row = new JsonObject
            {
                ["role"] = s.Member("role") ?? JsonValue.Null,
                ["name"] = s.Member("name") ?? JsonValue.Null,
                ["route"] = "peer",
                ["transport_id"] = s.Member("pane_id") ?? JsonValue.Null,
                ["session_id"] = s.Member("session_id") is { IsTruthy: true } sid ? sid : JsonValue.Null,
                ["live"] = s.Member("live") ?? JsonValue.Null,
                ["how"] = s.Member("how") ?? JsonValue.Null,
                ["source"] = s.Member("source") is { IsTruthy: true } src ? src : "peers",
            };
            if (s.Member("stray") is { IsTruthy: true })
            {
                row["stray"] = true;
            }

            members.Add(row);
        }

        return members;
    }

    public JsonArray PeerExtras(string dir, IReadOnlyList<JsonValue> members, TeamScope scope, LaunchContext context)
    {
        var panes = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            if (!m.IsMap)
            {
                continue;
            }

            if (m.Member("transport_id") is { IsTruthy: true } t)
            {
                panes.Add(JsConvert.ToJsString(t));
            }

            if (m.Member("name") is { IsTruthy: true } n)
            {
                names.Add(JsConvert.ToJsString(n));
            }
        }

        var extras = new List<JsonValue>();
        foreach (var p in PeerFallbackMembers(dir, scope, context, members))
        {
            var pane = p.Member("transport_id") is { IsTruthy: true } tid ? JsConvert.ToJsString(tid)
                : p.Member("pane_id") is { IsTruthy: true } pid ? JsConvert.ToJsString(pid) : null;
            var isMember = (pane is not null && panes.Contains(pane))
                || (p.Member("name") is { IsTruthy: true } nm && names.Contains(JsConvert.ToJsString(nm)));
            if (!isMember)
            {
                extras.Add(p);
            }
        }

        return DedupPeers(extras);
    }

    public PeerTarget ResolvePeerTarget(IReadOnlyList<JsonValue> fallback, string name, string? teamTransport, LaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(name);

        (JsonValue? Hit, string? Fail) Unique(List<JsonValue> matches)
        {
            if (matches.Count > 1)
            {
                var ids = new JsonArray();
                foreach (var m in matches)
                {
                    ids.Add(new JsonObject
                    {
                        ["name"] = m.Member("name") ?? JsonValue.Null,
                        ["pane_id"] = m.Member("transport_id") is { IsTruthy: true } t ? t : JsonValue.Null,
                        ["session_id"] = m.Member("session_id") is { IsTruthy: true } s ? s : JsonValue.Null,
                        ["role"] = m.Member("role") is { IsTruthy: true } r ? r : JsonValue.Null,
                    });
                }

                return (null, $"dismiss: {JsonText.Stringify(name, 0)} is ambiguous — {matches.Count} live peers match: {JsonText.Stringify(ids, 0)} — re-run with a pane_id or a full session_id; nothing was closed");
            }

            return (matches.Count == 1 ? matches[0] : (JsonValue?)null, null);
        }

        List<JsonValue> Where(Func<JsonValue, bool> predicate) => fallback.Where(predicate).ToList();
        bool Is(JsonValue m, string field, Func<string, bool> test) => m.Member(field) is { IsTruthy: true } v && test(JsConvert.ToJsString(v));

        var steps = new List<List<JsonValue>>
        {
            Where(m => Is(m, "transport_id", v => v == name)),
            Where(m => Is(m, "session_id", v => v == name)),
        };
        if (name.Length >= 8)
        {
            steps.Add(Where(m => Is(m, "session_id", v => v.StartsWith(name, StringComparison.Ordinal))));
        }

        steps.Add(Where(m => m.Member("name") is { IsText: true } n && n.AsString() == name));
        foreach (var matches in steps)
        {
            var (hit, fail) = Unique(matches);
            if (fail is not null || hit is not null)
            {
                return new PeerTarget(hit, fail);
            }
        }

        if (teamTransport is null or "herdr")
        {
            JsonArray? topology = null;
            try
            {
                topology = Topology.Query();
            }
            catch (InvalidOperationException)
            {
                topology = null;
            }

            if (topology is not null)
            {
                var panes = new List<string>();
                foreach (var p in topology)
                {
                    if (p.Member("name") is { IsText: true } n && n.AsString() == name && p.Member("pane_id") is { } pane)
                    {
                        panes.Add(JsConvert.ToJsString(pane));
                    }
                }

                if (panes.Count > 0)
                {
                    var (hit, fail) = Unique(Where(m => Is(m, "transport_id", v => panes.Contains(v))));
                    if (fail is not null || hit is not null)
                    {
                        return new PeerTarget(hit, fail);
                    }
                }
            }
        }

        return new PeerTarget(null, null);
    }

    public CloseVerification CloseAndVerify(IReadOnlyList<JsonObject> closable, string teamTransport, List<string> warnings, LaunchContext context, JsonValue? teamRecord = null, string? dir = null)
    {
        ArgumentNullException.ThrowIfNull(closable);
        ArgumentNullException.ThrowIfNull(warnings);
        // A row stamped with a herdr flavour at spawn lives on herdr whatever the team record says: an implicit team is
        // written before any transport is known, and its record keeps none.
        string TransportOf(JsonObject m) => OnHerdr(m) ? "herdr" : teamTransport;
        var herdrOf = new Dictionary<JsonObject, IHerdr>(ReferenceEqualityComparer.Instance);
        foreach (var m in closable)
        {
            herdrOf[m] = HerdrOf(m, teamRecord);
        }

        var results = new List<JsonObject>();
        foreach (var m in closable)
        {
            // with no address recorded (a crash between spawn and the record write), a core terminal is found by its spawn nonce, never
            // by name; the address found goes on the row, so the re-check, the close and the verify all use the real terminal
            if (TransportOf(m) == "herdr" && m.Get("transport_id") is not { IsTruthy: true } && m.Get("spawn_nonce") is { IsText: true } nonce)
            {
                var found = herdrOf[m].SpawnAddress(nonce.AsString());
                if (found.Address is { } address)
                {
                    m["transport_id"] = address;
                }
                else
                {
                    results.Add(found.Error is { } error
                        ? new JsonObject { ["name"] = m.Get("name") ?? JsonValue.Null, ["transport_id"] = JsonValue.Null, ["closed"] = false, ["error"] = $"its terminal could not be found by spawn nonce {nonce.AsString()}: {error}; nothing was closed" }
                        : new JsonObject { ["name"] = m.Get("name") ?? JsonValue.Null, ["transport_id"] = JsonValue.Null, ["closed"] = true, ["error"] = JsonValue.Null, ["note"] = $"no live terminal carries spawn nonce {nonce.AsString()}; nothing was closed" });
                    continue;
                }
            }

            var conflict = ReuseConflict(m, TransportOf(m), context, herdrOf[m]);
            if (conflict is null)
            {
                var closed = CloseOne(m, teamTransport, context, warnings, herdrOf[m]);
                if (TransportOf(m) == "herdr" && closed.Get("error") is { IsText: true } miss && miss.AsString().Contains("pane_not_found", StringComparison.Ordinal)
                    && OtherServerOf(m, herdrOf[m], dir ?? context.Cwd, context, warnings) is { } other)
                {
                    herdrOf[m] = other;
                    closed["closed"] = true;
                    closed["error"] = JsonValue.Null;
                    closed["transport_id"] = m.Get("transport_id") ?? JsonValue.Null;
                    closed["herdr_flavour"] = other.Flavour;
                }

                results.Add(closed);
                continue;
            }

            var paneId = JsConvert.ToJsString(m.Get("transport_id"));
            var name = JsConvert.ToJsString(m.Get("name"));
            var unverified = conflict.StartsWith('\u0000');
            var detail = unverified ? conflict[1..] : conflict;
            warnings.Add(unverified
                ? $"Pane {paneId} could not be re-checked before closing it ({detail}). It was not closed."
                : $"Pane {paneId} now carries {detail}, not {name}. It was not closed.");
            var row = new JsonObject
            {
                ["name"] = m.Get("name") ?? JsonValue.Null,
                ["transport_id"] = m.Get("transport_id") ?? JsonValue.Null,
                ["closed"] = false,
                ["error"] = unverified ? "pane could not be re-checked" : $"pane now carries {detail}",
            };
            if (m.Get("source") is { IsTruthy: true } source)
            {
                row["source"] = source;
            }

            if (m.Get("stray") is { IsTruthy: true })
            {
                row["stray"] = true;
            }

            results.Add(row);
        }

        var pending = new List<(JsonObject Row, string Transport, IHerdr Herdr)>();
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Get("closed") is { IsTruthy: true })
            {
                pending.Add((results[i], TransportOf(closable[i]), herdrOf[closable[i]]));
            }
        }

        foreach (var group in pending.GroupBy(p => p.Transport == "herdr" ? "herdr:" + p.Herdr.Flavour : p.Transport, StringComparer.Ordinal))
        {
            var transport = group.First().Transport;
            var server = group.First().Herdr;
            var rows = group.Select(p => p.Row).ToList();
            var deadline = Clock.UtcNow.AddMilliseconds(VerifyDeadlineMs);
            var still = rows;
            string? why = null;
            while (true)
            {
                try
                {
                    var ids = LivePaneIds(transport, server);
                    still = rows.Where(r => ids.Contains(JsConvert.ToJsString(r.Get("transport_id")))).ToList();
                }
                catch (InvalidOperationException err)
                {
                    why = err.Message;
                    break;
                }

                if (still.Count == 0 || Clock.UtcNow >= deadline)
                {
                    break;
                }

                Sleeper.Sleep(TimeSpan.FromMilliseconds(VerifyPollMs));
            }

            if (why is not null)
            {
                warnings.Add($"{transport} could not be queried ({why}). {rows.Count} target{(rows.Count == 1 ? " is" : "s are")} unverified.");
                foreach (var r in rows)
                {
                    r["closed"] = false;
                    r["error"] = $"could not verify the close: {why}";
                }
            }
            else
            {
                foreach (var r in still)
                {
                    r["closed"] = false;
                    r["error"] = "still listed after the close command";
                }
            }
        }

        var stillLive = results
            .Where(r => r.Get("closed") is not { IsTruthy: true })
            .Select(r => r.Get("name") is { IsNull: false } n ? JsConvert.ToJsString(n) : JsConvert.ToJsString(r.Get("transport_id")))
            .ToList();
        if (stillLive.Count > 0)
        {
            warnings.Add($"After close, {stillLive.Count} {(stillLive.Count == 1 ? "is" : "are")} still live: {string.Join(", ", stillLive)}.");
        }

        return new CloseVerification(results, stillLive);
    }

    public ReconcileOutcome ReconcileAfterClose(string dir, JsonValue snapshot, IReadOnlyList<JsonObject> results, string path, LaunchContext context)
    {
        var snapshotNames = new HashSet<string>(StringComparer.Ordinal);
        if (snapshot.Member("members") is { IsArray: true } snapshotMembers)
        {
            foreach (var m in snapshotMembers.AsArray())
            {
                if (m.Member("name") is { IsNull: false } n)
                {
                    snapshotNames.Add(JsConvert.ToJsString(n));
                }
            }
        }

        JsonObject? ResultFor(JsonValue m)
        {
            var name = m.Member("name");
            foreach (var r in results)
            {
                if (name is { IsNull: false } n)
                {
                    if (r.Get("name") is { IsNull: false } rn && JsConvert.ToJsString(rn) == JsConvert.ToJsString(n))
                    {
                        return r;
                    }
                }
                else if (r.Get("name") is not { IsNull: false } && r.Get("transport_id") is { IsNull: false } rt && m.Member("transport_id") is { IsNull: false } mt && JsConvert.ToJsString(rt) == JsConvert.ToJsString(mt))
                {
                    return r;
                }
            }

            return null;
        }

        List<string>? pruned = null;
        List<JsonObject>? kept = null;
        var update = Rows.UpdateRows(
            dir,
            path,
            fresh =>
            {
                pruned = [];
                kept = [];
                var keptRows = new List<JsonValue>();
                if (fresh.Get("members") is not { IsArray: true } freshMembers)
                {
                    return null;
                }

                foreach (var m in freshMembers.AsArray())
                {
                    var hasName = m.Member("name") is { IsNull: false };
                    var label = hasName ? JsConvert.ToJsString(m.Member("name")) : JsConvert.ToJsString(m.Member("role"));
                    var r = ResultFor(m);
                    string? why = null;
                    if (hasName && !snapshotNames.Contains(label))
                    {
                        why = "added";
                    }
                    else if (r is not null && r.Get("closed") is not { IsTruthy: true })
                    {
                        why = "close-failed";
                    }
                    else if (r is not null && r.Get("appserver_unverified") is { IsTruthy: true })
                    {
                        why = "appserver-unverified";
                    }
                    else if (r is null && hasName)
                    {
                        var st = MemberLiveness(dir, m);
                        if (st.Get("live") is { IsTruthy: true })
                        {
                            why = "live";
                        }
                        else if (st.Get("indeterminate") is { IsTruthy: true })
                        {
                            why = "indeterminate";
                        }
                    }

                    if (why is not null)
                    {
                        kept.Add(new JsonObject { ["name"] = label, ["why"] = why });

                        // a pane found on the other flavour's server stamps the row, so the next call goes there direct
                        keptRows.Add(r?.Get("herdr_flavour") is { IsText: true } found && m.IsMap ? Stamped(m, found) : m);
                    }
                    else
                    {
                        pruned.Add(label);
                    }
                }

                return keptRows;
            });
        if (pruned is null)
        {
            return new ReconcileOutcome([], [], false, Files.Exists(path) ? path : null);
        }

        return new ReconcileOutcome(pruned, kept ?? [], update.Cleared, path);
    }

    private static JsonValue Stamped(JsonValue row, JsonValue flavour)
    {
        var stamped = row.AsObject().Clone();
        stamped[Ah.Core.Mux.HerdrFlavour.Field] = flavour;
        return stamped;
    }

    public string CloseToken(JsonValue teamId, IReadOnlyList<JsonValue> closable)
    {
        ArgumentNullException.ThrowIfNull(closable);
        var ids = new List<string>();
        foreach (var m in closable)
        {
            if (m.Member("transport_id") is { IsNull: false } t)
            {
                ids.Add(JsConvert.ToJsString(t));
            }
        }

        ids.Sort(StringComparer.Ordinal);
        var idArray = new JsonArray();
        foreach (var id in ids)
        {
            idArray.Add(id);
        }

        var doc = new JsonObject { ["team_id"] = teamId, ["ids"] = idArray };
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonText.Stringify(doc, 0)));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    public IReadOnlyList<JsonValue> ClosableMembers(IReadOnlyList<JsonValue> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return members
            .Where(m => (Config.RouteHasPane(m.Member("route")) || (m.Member("route") is { IsText: true } route && route.AsString() == Registry.RouteMail))
                && (m.Member("transport_id") is { IsNull: false } || m.Member("spawn_nonce") is { IsText: true }))
            .ToList();
    }

    public JsonObject MemberLiveness(string dir, JsonValue member, string? team = null)
    {
        var name = member.Member("name") ?? JsonValue.Null;
        team ??= SoleTeamOf(dir, name);
        var herdr = JsConvert.ToJsString(Config.ResolveKind(member)) == Registry.KindDefault
            ? null
            : HerdrOf(member, team is null ? (JsonValue?)null : Roster.ReadTeamRecord(dir, team)).AgentState(JsConvert.ToJsString(member.Member("name")));
        var (live, why, dead, lastLife, deadAt) = PeerState(dir, name, team);
        if (!live && OwnPool(dir, member) is { } own)
        {
            var other = PeerState(own, name, team);
            var mainLife = lastLife;
            var mainState = (live, why, dead, lastLife, deadAt);
            if (other.Live || other.Why != NoPeerRecord)
            {
                (live, why, dead, lastLife, deadAt) = other;
                if (!live && mainState.dead && !dead)
                {
                    (live, why, dead, lastLife, deadAt) = (mainState.live, mainState.why, true, other.LastLife, mainState.deadAt);
                }
            }

            // A down row decides only when no pool shows a sign of life after it.
            var later = mainLife > lastLife ? mainLife : lastLife;
            if (dead && deadAt is { } diedAt && later is { } seen && seen > diedAt)
            {
                (dead, why, lastLife) = (false, "a sign of life is newer than the row that says the session ended", seen);
            }
        }

        // A herdr agent that answers with a running status raises the member to live; any other answer, agent_not_found included,
        // changes nothing, because herdr names agents and does not know which session a name belongs to now.
        var paneWhy = herdr is { Live: true } && Ah.Core.Time.LivenessRules.PaneShowsAgent(herdr.AgentStatus, herdr.AgentType) ? herdr.Why : null;
        var startingAge = RosterModule.StartingAgeSeconds(member, Clock.UtcNow);
        var gathered = live ? Ah.Core.Time.LivenessVerdict.Live : dead ? Ah.Core.Time.LivenessVerdict.ProvenDead : Ah.Core.Time.LivenessVerdict.Unknown;
        (var settled, why) = Ah.Core.Time.LivenessRules.Settle((gathered, why), paneWhy, startingAge);
        // An undecided member whose registered pid is gone is dead when its herdr pane is still there but shows no agent: the pane
        // outlived its session. herdr is asked here for any kind, and only in this case.
        if (settled == Ah.Core.Time.LivenessVerdict.Unknown && startingAge is null
            && HierarchyRoster.AttributedLivenessIn(dir, name, team).Get("pid_gone") is { IsTruthy: true })
        {
            herdr ??= Herdr.AgentState(JsConvert.ToJsString(name));
            if (herdr is { Live: true } && !Ah.Core.Time.LivenessRules.PaneShowsAgent(herdr.AgentStatus, herdr.AgentType))
            {
                (settled, why) = (Ah.Core.Time.LivenessVerdict.ProvenDead, $"its registered pid is gone and its herdr pane shows no agent ({herdr.Why})");
            }
        }

        (live, dead) = (settled == Ah.Core.Time.LivenessVerdict.Live, settled == Ah.Core.Time.LivenessVerdict.ProvenDead);
        // Only a positive sign that the session is gone makes it not running; anything less is undecided and counts as running.
        var verdict = new JsonObject
        {
            ["live"] = live,
            ["indeterminate"] = !live && !dead,
            ["why"] = why,
            ["agent_status"] = herdr?.AgentStatus is not null ? herdr.AgentStatus : JsonValue.Null,
            ["ready"] = herdr is not null ? herdr.Ready : live,
            ["liveness"] = live ? "live" : dead ? "proven_dead" : "unknown",
            ["last_life_at"] = lastLife is { } at ? Ah.Core.Time.Timestamps.ToIsoString(at) : JsonValue.Null,
        };
        if (!live && startingAge is not null)
        {
            verdict["starting"] = true;
        }

        return Claim(verdict, lastLife);
    }

    /// <summary>
    /// Adds <c>holds_claim</c>, the answer for a new member asking for this one's name or work tree: live, still starting, or undecided with
    /// a sign of life newer than <see cref="Ah.Core.Time.LivenessRules.FreshSeconds"/>. A member that is proven dead, or undecided with only old or no evidence,
    /// holds neither. It decides nothing about closing the member.
    /// </summary>
    private JsonObject Claim(JsonObject verdict, DateTimeOffset? lastLife)
    {
        var holds = verdict["live"].IsTruthy
            || verdict.Get("starting") is { IsTruthy: true }
            || (verdict["indeterminate"].IsTruthy && Ah.Core.Time.LivenessRules.HoldsClaim(Ah.Core.Time.LivenessVerdict.Unknown, lastLife, Clock.UtcNow));
        verdict["holds_claim"] = holds;
        return verdict;
    }

    private const string NoPeerRecord = "no live peers.jsonl record";

    /// <summary>The one team of <paramref name="dir"/> that lists a member of this name; null when none does or several do, so a caller that did not say which team asks of no registration.</summary>
    private string? SoleTeamOf(string dir, JsonValue name)
    {
        if (name is not { IsText: true } wanted)
        {
            return null;
        }

        string? found = null;
        foreach (var team in Roster.ListTeamNames(dir))
        {
            var lists = Roster.ReadTeamRecord(dir, team).Member("members") is { IsArray: true } members
                && members.AsArray().Any(m => m.Member("name") is { IsText: true } n && n.AsString() == wanted.AsString());
            if (!lists)
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = team;
        }

        return found;
    }

    /// <summary>Whether the member has a live row in <paramref name="pool"/>'s peers.jsonl, and what its newest row says when it has not.</summary>
    private (bool Live, string Why, bool ProvenDead, DateTimeOffset? LastLife, DateTimeOffset? DeadAt) PeerState(string pool, JsonValue name, string? team)
    {
        var attributed = HierarchyRoster.AttributedLivenessIn(pool, name, team);
        static DateTimeOffset? Stamp(JsonValue? written) =>
            written is { IsText: true } text && DateTimeOffset.TryParse(text.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        return (
            attributed.Get("live") is { IsTruthy: true },
            attributed.Get("why") is { IsText: true } why ? why.AsString() : "registration",
            attributed.Get("proven_dead") is { IsTruthy: true },
            Stamp(attributed.Get("last_life_at")),
            Stamp(attributed.Get("dead_at")));
    }

    /// <summary>The pool of the checkout the member recorded as its own, when that is not <paramref name="dir"/>: a session in a linked work tree writes its peers.jsonl there.</summary>
    private string? OwnPool(string dir, JsonValue member) =>
        member.Member("expected_root") is { IsText: true, IsTruthy: true } root && Config.HierarchyDir(root.AsString()) is var pool && pool != dir && Files.Exists(pool)
            ? pool
            : null;

    private HerdrArm ArmQuery()
    {
        if (!Herdr.OnPath())
        {
            return new HerdrArm(false, "herdr not on PATH", new JsonArray(), string.Empty, new JsonArray());
        }

        try
        {
            return new HerdrArm(true, null, Topology.Query(), string.Empty, new JsonArray());
        }
        catch (InvalidOperationException err)
        {
            return new HerdrArm(false, err.Message, new JsonArray(), string.Empty, new JsonArray());
        }
    }

    /// <summary>A team riding tmux or terminal has no herdr topology that could describe it; with no team file there is no transport to contradict.</summary>
    private bool ArmApplies(string dir, TeamScope scope)
    {
        if (scope.NoTeam)
        {
            return true;
        }

        var team = Roster.ReadTeamRecord(dir, string.IsNullOrEmpty(scope.Team) ? null : scope.Team);
        return !team.IsTruthy || (team.Member("transport") is { IsText: true } t && t.AsString() == "herdr");
    }

    private string ScopePrefix(TeamScope scope, LaunchContext context) =>
        !scope.NoTeam && !string.IsNullOrEmpty(scope.Team) ? scope.Team : Config.TeamPrefix(context.Cwd, string.IsNullOrEmpty(context.TeamArg) ? null : context.TeamArg);

    private (HashSet<string> Panes, HashSet<string> Names) OtherTeamMemberKeys(string dir)
    {
        var panes = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in new[] { Roster.TeamHome(dir), dir }.Distinct(StringComparer.Ordinal))
        {
            var teams = new List<string?> { null };
            teams.AddRange(Roster.ListTeamNames(d));
            foreach (var name in teams)
            {
                var t = Roster.ReadTeamFileAt(Roster.PoolTeamPath(d, name));
                if (t.Member("members") is not { IsArray: true } members)
                {
                    continue;
                }

                foreach (var m in members.AsArray())
                {
                    if (m.Member("transport_id") is { IsNull: false } tid)
                    {
                        panes.Add(JsConvert.ToJsString(tid));
                    }

                    if (m.Member("name") is { IsTruthy: true } n)
                    {
                        names.Add(JsConvert.ToJsString(n));
                    }
                }
            }
        }

        return (panes, names);
    }

    /// <summary>The hierarchy dirs a teardown reads peers.jsonl from: this pool, the team home, and the pool of every checkout a team member recorded as its own.</summary>
    private List<string> TeardownPools(string dir)
    {
        var pools = new List<string>();

        void Add(string? d)
        {
            if (!string.IsNullOrEmpty(d) && !pools.Contains(d) && Files.Exists(d))
            {
                pools.Add(d);
            }
        }

        var home = Roster.TeamHome(dir);
        Add(dir);
        Add(home);
        foreach (var d in new[] { dir, home }.Distinct(StringComparer.Ordinal))
        {
            var teams = new List<string?> { null };
            teams.AddRange(Roster.ListTeamNames(d));
            foreach (var name in teams)
            {
                var t = Roster.ReadTeamFileAt(Roster.PoolTeamPath(d, name));
                if (t.Member("members") is not { IsArray: true } members)
                {
                    continue;
                }

                foreach (var m in members.AsArray())
                {
                    if (m.Member("expected_root") is { IsText: true, IsTruthy: true } root)
                    {
                        Add(Config.HierarchyDir(root.AsString()));
                    }
                }
            }
        }

        return pools;
    }

    private List<JsonValue> LivePeerSlotsAcross(string dir, TeamScope scope)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var slots = new List<JsonValue>();
        foreach (var pool in TeardownPools(dir))
        {
            JsonValue? team = scope.Team is not null && !scope.NoTeam ? (JsonValue)scope.Team : (JsonValue?)null;
            foreach (var s in HierarchyRoster.LivePeerSlots(pool, team, scope.NoTeam))
            {
                var key = s.Member("pane_id") is { IsTruthy: true } p ? $"pane:{JsConvert.ToJsString(p)}"
                    : s.Member("session_id") is { IsTruthy: true } sid ? $"sid:{JsConvert.ToJsString(sid)}"
                    : $"name:{JsConvert.ToJsString(s.Member("name"))}";
                if (seen.Add(key))
                {
                    slots.Add(s);
                }
            }
        }

        return slots;
    }

    /// <summary>
    /// Registry wins: an agent a team row or a peers row already names — by pane id, name or session id — is that row. Only rows that are
    /// not known-dead count: a stale <c>up</c> record whose pid is gone describes a session that ended.
    /// </summary>
    private List<JsonValue> HerdrArmSlots(string dir, TeamScope scope, IReadOnlyList<JsonValue> known, LaunchContext context)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in known)
        {
            if (k.Member("live") is { IsBool: true } live && !live.AsBool())
            {
                continue;
            }

            if (k.Member("pane_id") is { IsTruthy: true } p)
            {
                ids.Add(JsConvert.ToJsString(p));
            }

            if (k.Member("transport_id") is { IsTruthy: true } t)
            {
                ids.Add(JsConvert.ToJsString(t));
            }

            if (k.Member("name") is { IsTruthy: true } n)
            {
                names.Add(JsConvert.ToJsString(n));
            }

            if (k.Member("session_id") is { IsTruthy: true } s)
            {
                sessions.Add(JsConvert.ToJsString(s));
            }
        }

        var slots = new List<JsonValue>();
        foreach (var s in HerdrArmMatches(dir, scope, context).Matched)
        {
            var paneId = s.Member("pane_id") is { IsNull: false } pid ? JsConvert.ToJsString(pid) : null;
            var name = s.Member("name") is { IsNull: false } nm ? JsConvert.ToJsString(nm) : null;
            var session = s.Member("session_id") is { IsTruthy: true } sid ? JsConvert.ToJsString(sid) : null;
            if ((paneId is not null && ids.Contains(paneId)) || (name is not null && names.Contains(name)) || (session is not null && sessions.Contains(session)))
            {
                continue;
            }

            slots.Add(s);
        }

        return slots;
    }

    private static JsonArray DedupPeers(IEnumerable<JsonValue> members)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var m in members)
        {
            var key = m.Member("transport_id") is { IsTruthy: true } t ? JsConvert.ToJsString(t)
                : m.Member("session_id") is { IsTruthy: true } s ? JsConvert.ToJsString(s) : null;
            if (key is not null && !seen.Add(key))
            {
                continue;
            }

            result.Add(m);
        }

        return result;
    }

    /// <summary>Whether a member row is addressed on herdr: it came from a herdr listing, or spawn stamped its flavour.</summary>
    private static bool OnHerdr(JsonObject m) =>
        m.Get("source") is { IsTruthy: true } || HerdrFlavour.OfMember(JsonValue.FromObject(m)) is not null;

    private JsonObject CloseOne(JsonObject m, string teamTransport, LaunchContext context, List<string> warnings, IHerdr herdr)
    {
        var row = new JsonObject
        {
            ["name"] = m.Get("name") ?? JsonValue.Null,
            ["transport_id"] = m.Get("transport_id") ?? JsonValue.Null,
        };
        if (m.Get("appserver_pid") is { IsNull: false } recorded)
        {
            var name = JsConvert.ToJsString(m.Get("name"));
            if (recorded is { IsNumber: true } n && Math.Floor(n.AsNumber()) == n.AsNumber() && n.AsNumber() >= int.MinValue && n.AsNumber() <= int.MaxValue && LaunchModule.IsSignalablePid((int)n.AsNumber()))
            {
                var socket = CodexModule.AppserverSocket(Environment.AhHome, context.TeamFile ?? "team", name);
                if (Appserver.Terminate((int)n.AsNumber(), socket))
                {
                    row["appserver_stopped"] = true;
                }
                else
                {
                    row["appserver_unverified"] = true;
                    warnings.Add(
                        $"appserver pid not verified: {name}'s recorded appserver_pid {JsConvert.ToJsString(recorded)} is not the Codex app-server listening on {socket} (or its command line could not be read), so it was not signalled. Its team row stays.");
                }
            }
            else
            {
                row["appserver_unverified"] = true;
                warnings.Add(
                    $"appserver pid not verified: {name}'s recorded appserver_pid {JsonText.Stringify(recorded, 0)} is not a process id (a whole number above 1), so nothing was signalled. Its team row stays.");
            }
        }

        var transport = OnHerdr(m) ? "herdr" : teamTransport;
        try
        {
            CloseMemberPane(transport, JsConvert.ToJsString(m.Get("transport_id")), herdr);
            row["closed"] = true;
            row["error"] = JsonValue.Null;
        }
        catch (InvalidOperationException err)
        {
            row["closed"] = false;
            row["error"] = err.Message;
        }

        if (m.Get("source") is { IsTruthy: true } source)
        {
            row["source"] = source;
        }

        if (m.Get("stray") is { IsTruthy: true })
        {
            row["stray"] = true;
        }

        return row;
    }

    /// <summary>
    /// The other flavour's server, once it has closed <paramref name="m"/>'s pane there; null when it could not. A row older than the
    /// <c>herdr_flavour</c> tag resolves to the team's or env's server, and a pane id names a pane only on its own server, so the pane is
    /// closed there only when its foreground process is the member's registered pid; with no pid registered, nothing is closed.
    /// </summary>
    private IHerdr? OtherServerOf(JsonObject m, IHerdr tried, string dir, LaunchContext context, List<string> warnings)
    {
        if (Herdr is not global::Ah.Launch.Herdr env || tried.Flavour is not { } flavour)
        {
            return null;
        }

        var otherFlavour = flavour == Ah.Core.Mux.HerdrFlavour.Core ? Ah.Core.Mux.HerdrFlavour.Tui : Ah.Core.Mux.HerdrFlavour.Core;
        IHerdr other = otherFlavour == Herdr.Flavour ? Herdr : env.ForFlavour(otherFlavour);
        var pane = JsConvert.ToJsString(m.Get("transport_id"));
        var name = JsConvert.ToJsString(m.Get("name"));
        var session = m.Get("session_id") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString()
            : SessionOf?.Invoke(dir, context.TeamFile, m) is { IsText: true } found && found.AsString().Length > 0 ? found.AsString() : null;
        var registered = session is null ? null : Registration?.Invoke(session)?.Get("pid");
        try
        {
            // a member whose Claude exited (a bare shell after a herdr restart) has no pid to prove its pane; its spawn label does
            if (m.Get("spawn_nonce") is { IsText: true } nonce && other.SpawnAddress(nonce.AsString()).Address is { } spawned)
            {
                other.Invoke(["pane", "close", spawned]);
                m["transport_id"] = spawned;
                return other;
            }

            var proven = registered is { IsNumber: true } pid
                && other.Invoke(["pane", "process-info", "--pane", pane]) is var info
                && info.Member("result")?.Member("process_info")?.Member("shell_pid") is var shell
                && BareDetector.ProcessesOf(info)?.Any(p => p.Pid == pid.AsNumber() && !(shell is { IsNumber: true } sp && sp.AsNumber() == p.Pid)) == true;
            if (!proven)
            {
                warnings.Add($"Pane {pane} of {name} is not on the {flavour} server, and the {otherFlavour} server's pane {pane} could not be tied to {name}. It was not closed.");
                return null;
            }

            other.Invoke(["pane", "close", pane]);
            return other;
        }
        catch (InvalidOperationException err)
        {
            warnings.Add($"Pane {pane} of {name} is not on the {flavour} server, and the {otherFlavour} server could not be asked ({err.Message}). It was not closed.");
            return null;
        }
    }

    private void CloseMemberPane(string transport, string transportId, IHerdr herdr)
    {
        if (transport == "herdr")
        {
            if (!LaunchModule.IsHerdrPaneId(transportId))
            {
                throw new InvalidOperationException($"{JsonText.Stringify(transportId, 0)} is not a herdr pane id, so it was not passed to herdr");
            }

            herdr.Invoke(["pane", "close", transportId]);
            return;
        }

        if (transport == "tmux")
        {
            if (!LaunchModule.IsTmuxPaneId(transportId))
            {
                throw new InvalidOperationException($"{JsonText.Stringify(transportId, 0)} is not a tmux pane id, so it was not passed to tmux");
            }

            var r = Mux.Run("tmux", ["kill-pane", "-t", transportId], 10000);
            if (!r.Ok)
            {
                throw new InvalidOperationException(r.ErrorMessage ?? "tmux kill-pane failed");
            }

            return;
        }

        throw new InvalidOperationException($"disband --close: transport {JsonText.Stringify(transport, 0)} has no addressable pane to close");
    }

    /// <summary>The pane ids a transport lists right now, read fresh. Throws when the transport cannot be asked.</summary>
    private HashSet<string> LivePaneIds(string transport, IHerdr herdr)
    {
        if (transport == "herdr")
        {
            return new HashSet<string>(TopologyOf(herdr).Query(fresh: true).Select(a => JsConvert.ToJsString(a.Member("pane_id"))), StringComparer.Ordinal);
        }

        if (transport == "tmux")
        {
            var listed = Mux.Run("tmux", ["list-panes", "-a", "-F", "#{pane_id}"], 10000);
            if (!listed.Ok)
            {
                throw new InvalidOperationException(listed.ErrorMessage ?? "tmux list-panes failed");
            }

            return new HashSet<string>(listed.Stdout.Split('\n').Where(l => l.Length > 0), StringComparer.Ordinal);
        }

        throw new InvalidOperationException($"transport {JsonText.Stringify(transport, 0)} has no pane list to query");
    }

    /// <summary>
    /// The name now on a herdr pane when it is not the one the plan matched (panes get reused), else null. A leading NUL marks an
    /// unverified conflict: a stray is closed only once its name is confirmed again.
    /// </summary>
    private string? ReuseConflict(JsonObject m, string transport, LaunchContext context, IHerdr herdr)
    {
        if (transport != "herdr" || m.Get("name") is not { IsTruthy: true } nameValue || m.Get("transport_id") is not { IsNull: false } tid)
        {
            return null;
        }

        var name = JsConvert.ToJsString(nameValue);
        var stray = m.Get("stray") is { IsTruthy: true };
        JsonArray agents;
        try
        {
            agents = TopologyOf(herdr).Query(fresh: true);
        }
        catch (InvalidOperationException err)
        {
            return stray ? "\u0000" + err.Message : null;
        }

        JsonValue? agent = null;
        foreach (var x in agents)
        {
            if (x.Member("pane_id") is { } p && JsConvert.ToJsString(p) == JsConvert.ToJsString(tid))
            {
                agent = x;
                break;
            }
        }

        // the same agent session under another name is the same member, not a pane that reused the id
        if (agent?.Member("session_id") is { IsText: true, } live && live.AsString().Length > 0
            && m.Get("session_id") is { IsText: true, } held && held.AsString().Length > 0 && live.AsString() == held.AsString())
        {
            return null;
        }

        var now = agent?.Member("name") is { IsTruthy: true } an ? JsConvert.ToJsString(an)
            : agent?.Member("title") is { IsTruthy: true } at ? JsConvert.ToJsString(at) : null;
        if (now is null)
        {
            return agent is not null ? "(no name)" : null;
        }

        if (stray)
        {
            return now == name ? null : now;
        }

        // a member's pane carries its name or another ordinal of the same prefix and role; a name that does not parse, or parses to
        // anything else, is some other session's pane that reused the id
        if (now == name)
        {
            return null;
        }

        var want = Config.HierarchyNameParts(name, context.Registry);
        var got = Config.HierarchyNameParts(now, context.Registry);
        if (want is null || got is null)
        {
            return now;
        }

        var same = JsConvert.ToJsString(want.Get("prefix")) == JsConvert.ToJsString(got.Get("prefix"))
            && JsConvert.ToJsString(want.Get("role")) == JsConvert.ToJsString(got.Get("role"));
        return same ? null : now;
    }
}
