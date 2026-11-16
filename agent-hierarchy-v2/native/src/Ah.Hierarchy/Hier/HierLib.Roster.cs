using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Hierarchy;

internal sealed partial class HierLib : IHierarchyRoster
{
    private static bool ToNameMatches(JsonValue? toName, JsonValue name) => StrictEqualsU(toName, name) || toName is { IsNull: true };

    private string IsoNowString() => Timestamps.ToIsoString(_clock.UtcNow);

    /// <summary>Rows with a repeated <c>op_id</c> dropped: the first copy of each op is the one that counts.</summary>
    private static bool FirstOfOp(JsonValue row, HashSet<string> seen)
    {
        if (row.Member("op_id") is not { IsTruthy: true } id)
        {
            return true;
        }

        return JsOps.SetKey(id) is not { } key || seen.Add(key);
    }

    /// <summary>The gate log of <paramref name="dir"/> (per-dir-files-to-db S3: the <c>gates</c> table), in append order, each op's first row only; an unparsable row is skipped.</summary>
    public JsonArray ReadGates(string dir) => ReadGatesOrNull(dir, out _) ?? new JsonArray();

    /// <summary>
    /// As <see cref="ReadGates"/>, but null when the database cannot be read. The parsed rows of each dir are held (b459 step 13): a read
    /// parses only the rows after the held position, and the dir's row count, read with them, guards the hold; held plus new rows that
    /// differ from it mean a row was deleted or imported before the first, and the dir is read again in full. The array is new; its rows
    /// are shared, and callers must not change one.
    /// </summary>
    public JsonArray? ReadGatesOrNull(string dir, out string? why)
    {
        why = null;
        if (_gates is null)
        {
            return new JsonArray();
        }

        lock (_gateHolds)
        {
            var held = _gateHolds.GetValueOrDefault(dir);
            GateRowsAfter? delta;
            try
            {
                // from long.MinValue: an imported row sits at seq <= 0
                delta = _gates.After(dir, held?.Seq ?? long.MinValue);
                if (delta is not null && held is not null && held.Bodies + delta.Rows.Count != delta.Count)
                {
                    held = null;
                    delta = _gates.After(dir, long.MinValue);
                }
            }
            catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
            {
                why = e.Message;
                return null;
            }

            if (delta is null)
            {
                return _gates.BodiesOrNull(dir, out why) is { } bodies ? GateHold.Of(bodies.Select(static (b, i) => new GateRowAt(i + 1, b))).Copy() : null;
            }

            held ??= new GateHold();
            held.Take(delta.Rows);
            _gateHolds[dir] = held;
            return held.Copy();
        }
    }

    /// <summary>Drops every dir's held gate rows, so the next read of each is in full (the on-demand rebuild).</summary>
    public void ForgetGateHolds()
    {
        lock (_gateHolds)
        {
            _gateHolds.Clear();
        }
    }

    private readonly Dictionary<string, GateHold> _gateHolds = new(StringComparer.Ordinal);

    /// <summary>A dir's parsed gate rows: each op's first row only, an unparsable row skipped, and how many rows were read and to where.</summary>
    private sealed class GateHold
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly JsonArray _rows = [];

        internal long Seq { get; private set; }

        internal long Bodies { get; private set; }

        internal static GateHold Of(IEnumerable<GateRowAt> rows)
        {
            var hold = new GateHold();
            hold.Take(rows);
            return hold;
        }

        internal void Take(IEnumerable<GateRowAt> rows)
        {
            foreach (var (seq, body) in rows)
            {
                Seq = seq;
                Bodies++;
                JsonValue row;
                try
                {
                    row = JsonText.Parse(body);
                }
                catch (JsonParseException)
                {
                    continue;
                }

                if (row.IsMap && FirstOfOp(row, _seen))
                {
                    _rows.Add(row);
                }
            }
        }

        internal JsonArray Copy()
        {
            var copy = new JsonArray();
            copy.AddRange(_rows);
            return copy;
        }
    }

    public bool HasGate(string dir, Func<JsonValue, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return ReadGates(dir).Any(predicate);
    }

    public JsonValue SessionRouteRecord(string dir, JsonValue? sessionId)
    {
        if (sessionId is not { IsTruthy: true } sid)
        {
            return JsonValue.Null;
        }

        JsonValue last = JsonValue.Null;
        foreach (var r in ReadGates(dir))
        {
            if (Member(r, "type") is { IsText: true } type && type.AsString() == "route"
                && r.Member("session_id") is { } rs && StrictEquals(rs, sid)
                && Member(r, "value") is { IsText: true } value && _config.RouteValues.Contains(value.AsString()))
            {
                last = r;
            }
        }

        return last;
    }

    public void RecordRoute(string dir, JsonValue sessionId, JsonValue value) =>
        AppendGateRouted(dir, new JsonObject { ["type"] = "route", ["session_id"] = sessionId, ["value"] = value });

    public JsonObject EffectiveRoute(string dir, JsonObject? resolved, JsonValue? sessionId)
    {
        var session = SessionRouteRecord(dir, sessionId);
        if (!session.IsNull)
        {
            return new JsonObject { ["value"] = Member(session, "value"), ["source"] = "session" };
        }

        if (resolved?.Get("route") is { IsTruthy: true } route)
        {
            return new JsonObject { ["value"] = route, ["source"] = "config" };
        }

        return new JsonObject { ["value"] = "peers", ["source"] = "default" };
    }

    public string? RoleForPeerName(JsonValue? name, JsonObject resolved, string repoBasename)
    {
        try
        {
            var cwd = TextOf(resolved.Get("cwd"));
            var member = _roster.TeamMemberByName(_config.HierarchyDir(cwd), TextOf(name), TextOf(resolved.Get("team")));
            if (!member.IsNull)
            {
                return JsConvert.ToJsString(member.Member("role"));
            }
        }
        catch (Exception)
        {
            // team lookup is best-effort; fall through to the existing paths
        }

        var roles = resolved.Get("roles");
        foreach (var role in _config.ChainRoles(resolved))
        {
            var targets = _config.ResolvedPeerTargets(role, roles?.Member(role), repoBasename);
            if (name is { } n && targets.Any(t => StrictEquals(t, n)))
            {
                return role;
            }
        }

        return _config.RoleFromName(TextOf(name), resolved);
    }

    public string? RoleForAnyPeerName(string dir, JsonValue? name, JsonObject resolved, string repoBasename)
    {
        try
        {
            var membership = _roster.ResolveMemberTeam(dir, TextOf(name));
            if (membership.Found)
            {
                var member = _roster.TeamMemberByName(dir, TextOf(name), membership.Team);
                if (!member.IsNull)
                {
                    return TextOf(member.Member("role"));
                }
            }
        }
        catch (Exception)
        {
            // team lookup is best-effort; fall through to the existing paths
        }

        var prefixes = new List<string>();
        var cwd = TextOf(resolved.Get("cwd"));
        if (resolved.Get("ownedTeams") is { IsArray: true } owned && owned.AsArray().Count > 1)
        {
            foreach (var team in owned.AsArray())
            {
                prefixes.Add(_config.TeamPrefix(cwd, TextOf(team)));
            }
        }
        else
        {
            prefixes.Add(repoBasename);
        }

        var roles = resolved.Get("roles");
        foreach (var role in _config.ChainRoles(resolved))
        {
            foreach (var prefix in prefixes)
            {
                var targets = _config.ResolvedPeerTargets(role, roles?.Member(role), prefix);
                if (name is { } n && targets.Any(t => StrictEquals(t, n)))
                {
                    return role;
                }
            }
        }

        return _config.RoleFromName(TextOf(name), resolved);
    }

    public JsonArray ReadRoster(string dir)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new JsonArray();
        foreach (var body in _rosterRecords?.Bodies(dir) ?? [])
        {
            JsonValue r;
            try
            {
                r = JsonText.Parse(body);
            }
            catch (JsonParseException)
            {
                continue;
            }

            if (!(Member(r, "type") is { IsText: true } type && type.AsString() == "peer"))
            {
                continue;
            }

            if (FirstOfOp(r, seen))
            {
                rows.Add(r);
            }
        }

        return rows;
    }

    /// <summary>One peer-roster row as the <c>peers.append</c> operation writes it: <c>{type: "peer", ...rec, ts}</c>.</summary>
    private JsonObject PeerRow(JsonValue? record)
    {
        var row = new JsonObject { ["type"] = "peer" };
        foreach (var key in JsOps.Spread(record).Keys)
        {
            row[key] = JsOps.Spread(record)[key];
        }

        row["ts"] = IsoNowString();
        return row;
    }

    private readonly Dictionary<string, (string Stamp, JsonValue[] Rows)> _latestRosters = new(StringComparer.Ordinal);

    /// <summary>
    /// The newest roster row of each member, held per dir while its stamp (the peers counter and the dir's row count) reads the same
    /// (b459 step 15): every member's read in a sweep shares one parse. The rows are shared; a fresh array is returned each call.
    /// </summary>
    public JsonArray LatestRoster(string dir)
    {
        var stamp = _rosterRecords?.Stamp(dir);
        if (stamp is not null)
        {
            lock (_latestRosters)
            {
                if (_latestRosters.TryGetValue(dir, out var held) && held.Stamp == stamp)
                {
                    return [.. held.Rows];
                }
            }
        }

        var built = BuildLatestRoster(dir);
        if (stamp is not null)
        {
            lock (_latestRosters)
            {
                _latestRosters[dir] = (stamp, [.. built]);
            }
        }

        return built;
    }

    /// <summary>Drops every held roster, so the next read parses the rows again (the on-demand rebuild).</summary>
    public void ForgetLatestRosters()
    {
        lock (_latestRosters)
        {
            _latestRosters.Clear();
        }
    }

    private JsonArray BuildLatestRoster(string dir)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        var unique = 0;
        foreach (var rec in ReadRoster(dir))
        {
            var key = Or(rec.Member("name"), Or(rec.Member("session_id"), string.Empty));
            if (!key.IsTruthy)
            {
                continue;
            }

            var k = JsOps.SetKey(key) ?? "ref:" + unique++;
            if (!byKey.ContainsKey(k))
            {
                order.Add(k);
            }

            byKey[k] = rec;
        }

        var result = new JsonArray();
        foreach (var k in order)
        {
            result.Add(byKey[k]);
        }

        return result;
    }

    private static JsonObject Without(JsonValue rec, params string[] drop)
    {
        var copy = JsOps.Spread(rec);
        foreach (var key in drop)
        {
            copy.Remove(key);
        }

        return copy;
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

    public JsonArray AttributedRoster(string dir) => AttributedRoster(LatestRoster(dir), _roster.PaneResolver(dir));

    /// <summary>The attribution rule over the latest roster rows <paramref name="recs"/> and the pane rule <paramref name="byPane"/>.</summary>
    private static JsonArray AttributedRoster(JsonArray recs, Func<string?, Ah.Roster.PaneMatch?> byPane)
    {
        var attributed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var outList = new List<JsonValue>();
        foreach (var rec in recs)
        {
            if (Member(rec, "name").IsTruthy || !Member(rec, "pane_id").IsTruthy)
            {
                outList.Add(rec);
                continue;
            }

            var paneId = TextOf(rec.Member("pane_id"));
            var hit = byPane(paneId);
            JsonValue? memberName = hit is null ? (JsonValue?)null : Member(hit.Member, "name");
            if (hit is null || !(memberName is { IsTruthy: true } mn) || !StrictEqualsU(hit.Member.Member("role"), rec.Member("role")))
            {
                outList.Add(rec);
                continue;
            }

            var view = Without(rec, "team");
            view["name"] = mn;
            var viewName = mn.IsText ? mn.AsString() : JsConvert.ToJsString(mn);
            if (!attributed.TryGetValue(viewName, out var prev)
                || string.CompareOrdinal(JsConvert.ToJsString(Or(view.Get("ts"), string.Empty)), JsConvert.ToJsString(Or(prev.Get("ts"), string.Empty))) >= 0)
            {
                attributed[viewName] = view;
            }

            outList.Add(view);
        }

        if (attributed.Count == 0)
        {
            return recs;
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var rec in outList)
        {
            var nameValue = rec.Member("name");
            var nameKey = nameValue is { IsText: true } nt ? nt.AsString() : null;
            if (nameKey is null || !attributed.TryGetValue(nameKey, out var rep))
            {
                result.Add(rec);
                continue;
            }

            if (!emitted.Add(nameKey))
            {
                continue;
            }

            JsonValue? named = null;
            foreach (var r in recs)
            {
                if (r.Member("name") is { IsText: true } rn && rn.AsString() == nameKey
                    && Member(r, "status") is { IsText: true } st && st.AsString() is "seen" or "briefed")
                {
                    named = r;
                    break;
                }
            }

            if (named is { } n)
            {
                var merged = rep.Clone();
                SetOrRemove(merged, "busy", n.Member("busy"));
                SetOrRemove(merged, "shell", n.Member("shell"));
                SetOrRemove(merged, "task", n.Member("task"));
                result.Add(merged);
            }
            else
            {
                result.Add(rep);
            }
        }

        return result;
    }

    public bool HealFalseDown(string dir, JsonValue? sessionId, JsonValue? pid)
    {
        try
        {
            if (sessionId is not { IsTruthy: true } sid || JsConvert.AsInteger(pid) is not { } p || p <= 0)
            {
                return false;
            }

            var rows = ReadRoster(dir).Where(r => r.Member("session_id") is { } s && StrictEquals(s, sid)).ToList();
            if (rows.Count == 0 || !(Member(rows[^1], "status") is { IsText: true } last && last.AsString() == "down"))
            {
                return false;
            }

            JsonValue? up = null;
            for (var i = rows.Count - 1; i >= 0; i--)
            {
                if (Member(rows[i], "status") is { IsText: true } st && st.AsString() == "up")
                {
                    up = rows[i];
                    break;
                }
            }

            if (up is not { } found || !(found.Member("pid") is { IsNumber: true } fp && fp.AsNumber() == p))
            {
                return false;
            }

            var rec = Without(found, "type", "ts");
            rec["status"] = "up";
            AppendRosterRecordRouted(dir, rec, hook: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public JsonValue UpRecordFor(string dir, JsonValue? sessionId)
    {
        foreach (var r in LatestRoster(dir))
        {
            if (r.Member("session_id") is { } s && sessionId is { } sid && StrictEquals(s, sid)
                && Member(r, "status") is { IsText: true } st && st.AsString() == "up")
            {
                return r;
            }
        }

        return JsonValue.Null;
    }

    public bool RecordProvenDead(JsonValue record, double? now = null) =>
        JudgeRow(record, null, AgeSecOf(record.Member("ts"), now)) is { Verdict: Ah.Core.Time.LivenessVerdict.ProvenDead };

    public JsonObject RecordLiveness(JsonValue record, double? now = null)
    {
        var ageSec = AgeSecOf(record.Member("ts"), now);
        var live = false;
        JsonValue? how = record.Member("status");
        var status = TextOf(record.Member("status"));
        if (JudgeRow(record, null, ageSec) is { } judged)
        {
            live = judged.Verdict == Ah.Core.Time.LivenessVerdict.Live;
            if (status == "up")
            {
                how = "up-pid";
            }
        }

        var result = new JsonObject { ["live"] = live };
        Put(result, "how", how);
        result["ageSec"] = ageSec;
        return result;
    }

    public JsonObject AttributedLiveness(JsonArray roster, JsonValue? name, string? team = null) => Attributed(roster, name, team, null);

    public JsonObject AttributedLivenessIn(string dir, JsonValue? name, string? team = null) => Attributed(AttributedRoster(dir), name, team, dir);

    /// <summary>What the status compute takes from <see cref="Attributed"/> for a peer member: whether it is live, and its session id.</summary>
    private (bool Live, string? SessionId) MemberLifeOf(JsonArray roster, JsonValue name, string? team, Func<string, string, MemberRegistrationLiveness?> registration)
    {
        var att = Attributed(roster, name, team, null, registration);
        return (att["live"].AsBool(), Member(att["rec"], "session_id") is { IsText: true } sid ? sid.AsString() : null);
    }

    private JsonObject Attributed(JsonArray roster, JsonValue? name, string? team, string? dir, Func<string, string, MemberRegistrationLiveness?>? registration = null)
    {
        registration ??= (n, t) => _core.Liveness?.ForMember(n, t);
        JsonValue rec = JsonValue.Null;
        foreach (var r in roster)
        {
            // A row that never carried a name is known by the role@session form the roster lists it under (LivePeerSlots).
            var known = Or(r.Member("name"), SynthesizedPeerName(r));
            if (known.IsTruthy && name is { } n && StrictEquals(known, n))
            {
                rec = r;
                break;
            }
        }

        // With no team listing the member, its registration is found by session or address; only a proven death decides then.
        var registered = name is not { IsText: true } named ? null
            : team is not null ? registration(named.AsString(), team)
            : RegisteredTeamOf(named.AsString(), rec) is { } found && registration(named.AsString(), found) is { ProvenDead: true } gone ? gone : null;
        if (registered is not null && name is { IsText: true } memberName)
        {
            // A member with a registration is decided by it, whatever peers.jsonl says; its session id is the registration's.
            // b459 step 16: a record that already carries the registration's session id is emitted as is (rows are read, never written,
            // downstream); only one whose session differs is copied to set it
            JsonObject fromRegistration;
            if (rec.IsMap && registered.SessionId is { } own && rec.Member("session_id") is { IsText: true } held && held.AsString() == own)
            {
                fromRegistration = rec.AsObject();
            }
            else
            {
                fromRegistration = rec.IsMap ? rec.AsObject().Clone() : new JsonObject { ["name"] = memberName };
                fromRegistration["session_id"] = registered.SessionId;
            }
            return new JsonObject
            {
                ["rec"] = fromRegistration,
                ["live"] = registered.Live,
                ["registered"] = true,
                ["why"] = registered.Why,
                ["proven_dead"] = registered.ProvenDead,
                ["pid_gone"] = registered.PidGone,
                ["liveness"] = registered.Live ? "live" : registered.ProvenDead ? "proven_dead" : "unknown",
                ["last_life_at"] = registered.At is { } at ? Ah.Core.Time.Timestamps.ToIsoString(at) : JsonValue.Null,
            };
        }

        var activityLife = dir is null ? null : ActivityLifeAt(dir, rec, name);
        if (!rec.IsMap)
        {
            return Unregistered(rec, Ah.Core.Time.LivenessVerdict.Unknown, "no live peers.jsonl record", null, activityLife);
        }

        // A briefed row never decides; the newest up, down or seen row of the member does.
        var deciding = dir is not null && PeerKindOf(TextOf(rec.Member("status"))) == Ah.Core.Time.PeerRowKind.None && NewestDecidingRow(dir, rec) is { } older ? older : rec;
        var status = TextOf(deciding.Member("status"));
        var kind = PeerKindOf(status);
        var rowAt = RowAt(deciding);
        if (JudgeRow(deciding, activityLife, null, MemberRunsClaude(dir, team, name)) is not { } judged)
        {
            return Unregistered(rec, Ah.Core.Time.LivenessVerdict.Unknown, $"its latest peers.jsonl row is not live (status {(string.IsNullOrEmpty(status) ? "unknown" : status)})", rowAt, activityLife);
        }

        return Unregistered(rec, judged.Verdict, judged.Why, rowAt, activityLife, kind);
    }

    /// <summary>
    /// The team of the registration that names <paramref name="name"/>: the one of the row's session when it has one, else the one
    /// team any registration names it in; null when none, or when the name is registered in more than one team.
    /// </summary>
    private string? RegisteredTeamOf(string name, JsonValue rec)
    {
        if (_core.Liveness is not { } liveness)
        {
            return null;
        }

        var prefix = name + "@";
        IEnumerable<JsonObject> candidates = rec.IsMap && rec.Member("session_id") is { IsText: true } sid && liveness.RegistrationOf(sid.AsString()) is { } own
            ? [own]
            : liveness.Registrations();
        var teams = candidates
            .SelectMany(static r => r.Get("addresses") is { IsArray: true } many ? many.AsArray().ToList() : r.Get("address") is { } one ? [one] : new List<JsonValue>())
            .Where(a => a is { IsText: true } && a.AsString().StartsWith(prefix, StringComparison.Ordinal) && a.AsString().Length > prefix.Length)
            .Select(a => a.AsString()[prefix.Length..])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return teams.Count == 1 ? teams[0] : null;
    }

    /// <summary>The newest up, down or seen row that names the same member as <paramref name="rec"/>, or null when there is none.</summary>
    private JsonValue? NewestDecidingRow(string dir, JsonValue rec)
    {
        var byPane = _roster.PaneResolver(dir);
        var key = AttributedKey(rec, byPane);
        if (!key.IsTruthy)
        {
            return null;
        }

        JsonValue? found = null;
        foreach (var row in ReadRoster(dir))
        {
            if (StrictEquals(AttributedKey(row, byPane), key)
                && PeerKindOf(TextOf(row.Member("status"))) != Ah.Core.Time.PeerRowKind.None)
            {
                found = row;
            }
        }

        return found;
    }

    /// <summary>The name a row stands for: its own, else the member its pane is attributed to (when the role agrees), else its session id.</summary>
    private static JsonValue AttributedKey(JsonValue row, Func<string?, Ah.Roster.PaneMatch?> byPane)
    {
        if (Member(row, "name").IsTruthy)
        {
            return row.Member("name")!.Value;
        }

        if (Member(row, "pane_id").IsTruthy
            && byPane(TextOf(row.Member("pane_id"))) is { } hit
            && Member(hit.Member, "name") is { IsTruthy: true } memberName
            && StrictEqualsU(hit.Member.Member("role"), row.Member("role")))
        {
            return memberName;
        }

        return Or(row.Member("session_id"), string.Empty);
    }

    /// <summary>The newest peers row per member that can decide liveness: where the newest row is a briefed one, the member's newest up, down or seen row stands for it, and a member with none is left out.</summary>
    internal JsonArray LivenessRoster(string dir)
    {
        var result = new JsonArray();
        foreach (var row in AttributedRoster(dir))
        {
            if (PeerKindOf(TextOf(row.Member("status"))) != Ah.Core.Time.PeerRowKind.None)
            {
                result.Add(row);
            }
            else if (TextOf(row.Member("status")) == "briefed" && NewestDecidingRow(dir, row) is { } deciding)
            {
                result.Add(deciding);
            }
        }

        return result;
    }

    /// <summary>What a host with no <see cref="IMemberLiveness"/> can say about a pid: whether the core's identity check calls it the session.</summary>
    private sealed class CoreIdentity(IHierarchyCore core) : IMemberLiveness
    {
        public MemberRegistrationLiveness? ForMember(string memberName, string team) => null;

        public bool PidIdentityLive(int pid, string? recordedStart) => core.IdentityLive(JsonValue.FromNumber(pid), recordedStart is null ? JsonValue.Null : JsonValue.FromString(recordedStart));
    }

    private static Ah.Core.Time.PeerRowKind PeerKindOf(string? status) => Ah.Core.Time.LivenessAssembler.PeerKindOf(status);

    private static DateTimeOffset? RowAt(JsonValue rec) =>
        rec.Member("ts") is { IsText: true } ts && Ah.Core.Time.Timestamps.TryParseMs(ts.AsString(), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;

    /// <summary>
    /// The shared verdict over a peers row and the process it names; null when the row says nothing a verdict is made of (a status that is
    /// none of up, down, seen, briefed) or no implementation can observe processes.
    /// </summary>
    private (Ah.Core.Time.LivenessVerdict Verdict, string Why)? JudgeRow(JsonValue rec, DateTimeOffset? activityLife, double? ageSeconds = null, bool claudeHarness = true)
    {
        var kind = PeerKindOf(TextOf(rec.Member("status")));
        if (kind == Ah.Core.Time.PeerRowKind.None)
        {
            return null;
        }

        var liveness = _core.Liveness ?? new CoreIdentity(_core);
        var pid = Member(rec, "pid");
        var start = TextOf(rec.Member("pid_start"));
        var rowAt = RowAt(rec);
        var age = ageSeconds ?? (rowAt is { } at ? (_clock.UtcNow - at).TotalSeconds : (double?)null);
        return liveness.JudgePeerRow(kind, pid.IsNumber ? (long)pid.AsNumber() : null, string.IsNullOrEmpty(start) ? null : start, rowAt, age, activityLife, claudeHarness);
    }

    /// <summary>
    /// Whether the member runs a claude harness: its <c>kind</c> in the team record, claude when the record, the member or its kind is absent.
    /// A member of another kind is not held to the claude command line.
    /// </summary>
    private bool MemberRunsClaude(string? dir, string? team, JsonValue? name)
    {
        if (dir is null || name is not { IsText: true } memberName || _roster.ReadTeamRecord(dir, team == "_default" ? null : team) is not { IsMap: true } record
            || record.Member("members") is not { IsArray: true } members)
        {
            return true;
        }

        foreach (var member in members.AsArray())
        {
            if (member.Member("name") is { IsText: true } n && n.AsString() == memberName.AsString())
            {
                return member.Member("kind") is not { IsText: true } kind || kind.AsString().Length == 0 || kind.AsString() == "claude";
            }
        }

        return true;
    }

    public DateTimeOffset? ActivityLifeOf(string dir, string? sessionId, string name) =>
        ActivityLifeAt(dir, sessionId is null ? JsonValue.Null : JsonValue.FromObject(new JsonObject { ["session_id"] = sessionId }), JsonValue.FromString(name));

    /// <summary>
    /// The newest time on the session's or pane's activity record (<c>at</c>, <c>tool_at</c>, <c>working_at</c>), the evidence of life a
    /// down row is set against; null when there is no record.
    /// </summary>
    private DateTimeOffset? ActivityLifeAt(string dir, JsonValue rec, JsonValue? name)
    {
        double newest = 0;
        var files = new List<string>();
        if (rec.IsMap && rec.Member("session_id") is { IsText: true, IsTruthy: true } sid)
        {
            files.Add(sid.AsString() + ".json");
        }

        if (name is { IsText: true } paneName)
        {
            files.Add($"pane-{paneName.AsString()}.json");
        }

        foreach (var file in files)
        {
            if (ReadActivity(dir, file) is not { IsMap: true } activity)
            {
                continue;
            }

            foreach (var key in new[] { "at", "tool_at", "working_at" })
            {
                if (activity.Member(key) is { IsText: true } stamp && Ah.Core.Time.Timestamps.TryParseMs(stamp.AsString(), out var stampMs))
                {
                    newest = Math.Max(newest, stampMs);
                }
            }
        }

        return newest > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)newest) : null;
    }

    private static JsonObject Unregistered(JsonValue rec, Ah.Core.Time.LivenessVerdict verdict, string why, DateTimeOffset? rowAt, DateTimeOffset? activityLife, Ah.Core.Time.PeerRowKind kind = Ah.Core.Time.PeerRowKind.None)
    {
        var dead = verdict == Ah.Core.Time.LivenessVerdict.ProvenDead;
        // A down row is a record of the end, not of life; every other row shows the member alive when it was written.
        var life = kind is Ah.Core.Time.PeerRowKind.Up or Ah.Core.Time.PeerRowKind.Seen && rowAt is { } written && (activityLife is not { } other || written > other) ? written : activityLife;
        return new JsonObject
        {
            ["rec"] = rec,
            ["live"] = verdict == Ah.Core.Time.LivenessVerdict.Live,
            ["proven_dead"] = dead,
            ["liveness"] = verdict == Ah.Core.Time.LivenessVerdict.Live ? "live" : dead ? "proven_dead" : "unknown",
            ["why"] = why,
            ["last_life_at"] = life is { } stamp && !dead ? Ah.Core.Time.Timestamps.ToIsoString(stamp) : JsonValue.Null,
            ["dead_at"] = rowAt is { } deadStamp && dead ? Ah.Core.Time.Timestamps.ToIsoString(deadStamp) : JsonValue.Null,
        };
    }

    public JsonValue SynthesizedPeerName(JsonValue record)
    {
        var role = Or(record.Member("role"), JsonValue.Null);
        var sid = JsConvert.ToJsString(Or(record.Member("session_id"), string.Empty));
        if (role.IsTruthy && sid.Length > 0)
        {
            return $"{JsConvert.ToJsString(role)}@{(sid.Length > 8 ? sid[..8] : sid)}";
        }

        return JsonValue.Null;
    }

    private bool InScope(string dir, JsonValue effective, JsonValue? team, bool noTeamScope)
    {
        if (noTeamScope)
        {
            return effective.IsNull || !(effective.IsText && _roster.ListTeamNames(dir).Contains(effective.AsString()));
        }

        return StrictEquals(effective, team ?? JsonValue.Null);
    }

    public JsonArray LivePeerSlots(string dir, JsonValue? team = null, bool noTeamScope = false, double? now = null)
    {
        var slots = new JsonArray();
        foreach (var rec in AttributedRoster(dir))
        {
            if (Member(rec, "status") is { IsText: true } st && st.AsString() == "down")
            {
                continue;
            }

            var nameValue = Or(rec.Member("name"), SynthesizedPeerName(rec));
            if (!nameValue.IsTruthy)
            {
                continue;
            }

            var recName = rec.Member("name");
            var found = false;
            string? membershipTeam = null;
            if (recName is { IsTruthy: true } rn)
            {
                var m = _roster.ResolveMemberTeam(dir, TextOf(rn));
                found = m.Found;
                membershipTeam = m.Team;
            }

            var tag = rec.Member("team") ?? JsonValue.Null;
            var effective = found ? (membershipTeam is null ? JsonValue.Null : membershipTeam) : tag;
            if (!InScope(dir, effective, team, noTeamScope))
            {
                continue;
            }

            var slot = new JsonObject
            {
                ["name"] = nameValue,
                ["role"] = Or(rec.Member("role"), JsonValue.Null),
                ["pid"] = rec.Member("pid") is { } pidv && !pidv.IsNull ? pidv : JsonValue.Null,
                ["pane_id"] = Or(rec.Member("pane_id"), JsonValue.Null),
                ["session_id"] = Or(rec.Member("session_id"), JsonValue.Null),
                ["cwd"] = Or(rec.Member("cwd"), JsonValue.Null),
            };
            var liveness = RecordLiveness(rec, now);
            foreach (var key in liveness.Keys)
            {
                slot[key] = liveness[key];
            }

            slots.Add(slot);
        }

        return slots;
    }

    public JsonObject Roster(string dir, JsonObject resolved, string repoBasename, double? now = null)
    {
        var at = now ?? NowMs;
        var result = new JsonObject();
        var chain = _config.ChainRoles(resolved);
        foreach (var role in chain)
        {
            result[role] = new JsonArray();
        }

        var unattributed = new JsonArray();
        var teamValue = resolved.Get("team") is { IsTruthy: true } tv ? tv : JsonValue.Null;
        var open = new List<(JsonValue To, JsonValue? ToName)>();
        foreach (var e in OpenExchanges(dir, teamValue, ListExchangesOfTeam(dir, teamValue)))
        {
            var parsed = _core.ReadMsgFile(Member(Member(e, "request"), "path").AsString());
            open.Add((Member(e, "to"), parsed.Member("fm")?.Member("to_name") ?? (parsed.Member("fm") is { IsMap: true } ? (JsonValue?)null : JsonValue.Null)));
        }

        foreach (var rec in AttributedRoster(dir))
        {
            if (Member(rec, "status") is { IsText: true } st && st.AsString() == "down")
            {
                continue;
            }

            var roleValue = rec.Member("role") is { IsTruthy: true } rv ? rv : RoleForPeerName(rec.Member("name"), resolved, repoBasename) is { } named ? (JsonValue)named : JsonValue.Null;
            var role = TextOf(roleValue);
            if (role is null || role.Length == 0 || !result.ContainsKey(role))
            {
                continue;
            }

            var liveness = RecordLiveness(rec, at);
            var baseRow = new JsonObject
            {
                ["role"] = role,
                ["live"] = liveness["live"],
            };
            if (liveness.Get("how") is { } how)
            {
                baseRow["how"] = how;
            }

            baseRow["ageSec"] = liveness["ageSec"];
            baseRow["busy"] = rec.Member("busy") is { IsBool: true } b && b.AsBool();
            baseRow["task"] = Or(rec.Member("task"), JsonValue.Null);

            // Unattributed rows lead with the name; a listed instance carries it after the shared fields.
            JsonObject NameFirst(JsonValue name)
            {
                var o = new JsonObject { ["name"] = name };
                foreach (var k in baseRow.Keys)
                {
                    o[k] = baseRow[k];
                }

                return o;
            }

            JsonObject NameLast(JsonValue name, JsonObject extra)
            {
                var o = baseRow.Clone();
                o["name"] = name;
                foreach (var k in extra.Keys)
                {
                    o[k] = extra[k];
                }

                return o;
            }

            if (!Member(rec, "name").IsTruthy)
            {
                unattributed.Add(NameFirst(JsonValue.Null));
                if (teamValue.IsNull)
                {
                    var synth = JsOps.Spread(rec);
                    synth["role"] = role;
                    var name = SynthesizedPeerName(synth);
                    var mine = open.Where(o => StrictEquals(o.To, role) && ToNameMatches(o.ToName, name)).ToList();
                    result[role].AsArray().Add(NameLast(name, new JsonObject
                    {
                        ["openBriefs"] = mine.Count,
                        ["unassigned"] = mine.Count(o => o.ToName is { IsNull: true }),
                    }));
                }

                continue;
            }

            var recName = rec.Member("name")!.Value;
            var membership = _roster.ResolveMemberTeam(_config.HierarchyDir(TextOf(resolved.Get("cwd"))), TextOf(recName));
            var tag = rec.Member("team") ?? JsonValue.Null;
            if (!tag.IsNull && (!membership.Found || !(membership.Team is not null && tag.IsText && tag.AsString() == membership.Team)))
            {
                unattributed.Add(NameFirst(recName));
                continue;
            }

            var recTeam = membership.Found ? (membership.Team is null ? JsonValue.Null : membership.Team) : JsonValue.Null;
            if (!StrictEquals(recTeam, teamValue))
            {
                if (!teamValue.IsNull && !membership.Found)
                {
                    unattributed.Add(NameFirst(recName));
                }

                continue;
            }

            var mineNamed = open.Where(o => StrictEquals(o.To, role) && ToNameMatches(o.ToName, recName)).ToList();
            result[role].AsArray().Add(NameLast(recName, new JsonObject
            {
                ["openBriefs"] = mineNamed.Count,
                ["unassigned"] = mineNamed.Count(o => o.ToName is { IsNull: true }),
            }));
        }

        foreach (var key in result.Keys.ToList())
        {
            var rows = result[key].AsArray().ToList();
            rows = rows
                .Select((row, index) => (row, index))
                .OrderByDescending(x => x.row.Prop("live").AsBool() ? 1 : 0)
                .ThenBy(x => x.row.Prop("ageSec").AsNumber())
                .ThenBy(x => x.index)
                .Select(x => x.row)
                .ToList();
            var sorted = new JsonArray();
            foreach (var row in rows)
            {
                sorted.Add(row);
            }

            result[key] = sorted;
        }

        result["unattributed"] = unattributed;
        return result;
    }

    public string DescribeInstance(JsonValue instance)
    {
        var bits = new List<string>
        {
            $"{(Member(instance, "live").IsTruthy ? "live" : "stale")} {JsConvert.ToJsString(instance.Member("how"))} {FmtAge(instance.Member("ageSec"))} ago",
        };
        if (Member(instance, "busy").IsTruthy)
        {
            bits.Add("busy");
        }

        if (Member(instance, "openBriefs") is { IsTruthy: true } ob)
        {
            var n = ob.IsNumber ? ob.AsNumber() : double.NaN;
            var unassigned = Member(instance, "unassigned");
            bits.Add($"{JsConvert.ToJsString(ob)} open brief{(n == 1 ? string.Empty : "s")}{(unassigned.IsTruthy ? $" ({JsConvert.ToJsString(unassigned)} to role, no instance yet)" : string.Empty)}");
        }

        return $"\"{JsConvert.ToJsString(instance.Member("name"))}\" {string.Join(", ", bits)}";
    }

    public string RosterLine(JsonObject roster)
    {
        var keys = new List<string>(_config.PeerEligibleRoles);
        foreach (var k in roster.Keys)
        {
            if (!keys.Contains(k))
            {
                keys.Add(k);
            }
        }

        var parts = new List<string>();
        foreach (var role in keys.Where(k => k != "unattributed"))
        {
            var list = roster.Get(role) is { IsArray: true } l ? l.AsArray() : new JsonArray();
            if (list.Count == 0)
            {
                parts.Add($"{role}: none");
                continue;
            }

            parts.Add(string.Join("; ", list.Select(i =>
                $"{role}={JsConvert.ToJsString(i.Member("name"))} {(Member(i, "live").IsTruthy ? "live" : "stale")} {FmtAge(i.Member("ageSec"))} {JsConvert.ToJsString(i.Member("how"))}"
                + $"{(Member(i, "busy").IsTruthy ? " busy" : string.Empty)}{(Member(i, "task").IsTruthy ? $"({JsConvert.ToJsString(i.Member("task"))})" : string.Empty)}"
                + $"{(Member(i, "openBriefs").IsTruthy ? $" {JsConvert.ToJsString(i.Member("openBriefs"))} open" : string.Empty)}")));
        }

        return string.Join("; ", parts);
    }

    public JsonValue SessionModel(JsonValue? input, string? dir)
    {
        if (input?.Member("model") is { IsText: true } m && m.AsString().Length > 0)
        {
            return m;
        }

        var env = _environment.GetVariable("CLAUDE_MODEL");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        var sid = input?.Member("session_id");
        if (!string.IsNullOrEmpty(dir) && sid is { IsTruthy: true } s)
        {
            JsonValue last = JsonValue.Null;
            foreach (var r in ReadGates(dir))
            {
                if (Member(r, "type") is { IsText: true } t && t.AsString() == "model" && r.Member("session_id") is { } rs && StrictEquals(rs, s)
                    && Member(r, "model") is { IsText: true } model)
                {
                    last = model;
                }
            }

            return last;
        }

        return JsonValue.Null;
    }

    public void CacheSessionModel(string dir, JsonValue? sessionId, JsonValue? model)
    {
        if (sessionId is { IsTruthy: true } sid && model is { IsTruthy: true } m)
        {
            AppendGateRouted(dir, new JsonObject { ["type"] = "model", ["session_id"] = sid, ["model"] = m });
        }
    }

    public JsonValue RoleTier(string role, JsonObject resolved, JsonValue? sessionTier)
    {
        var model = resolved.Get("roles")?.Member(role)?.Member("model");
        if (model is { IsText: true } m && m.AsString() == "inherit")
        {
            return sessionTier ?? JsonValue.Null;
        }

        return _config.TierOf(model) is { } tier ? tier : JsonValue.Null;
    }

    public string RoleModelLabel(string role, JsonObject resolved)
    {
        var model = resolved.Get("roles")?.Member(role)?.Member("model");
        var tier = _config.TierOf(model);
        return $"{JsConvert.ToJsString(model)}({(tier is null ? "?" : tier.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))})";
    }

    public string RouteLine(JsonValue? route)
    {
        if (route is not { IsTruthy: true } r)
        {
            return "route: unknown — chain roles run only as peers";
        }

        return $"route: {JsConvert.ToJsString(r.Member("value"))} (from {JsConvert.ToJsString(r.Member("source"))}) — chain roles run only as peers";
    }
}
