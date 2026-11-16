using Ah.Abstractions;
using Ah.Core.Digests;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Roster;

internal sealed partial class RosterCore
{
    internal IFileReader Files => files;

    internal IProcessInspector Processes => processes;

    /// <summary>The database's way of holding teams while the teams domain is the database's; null where no state database is wired.</summary>
    internal ITeamAuthority? TeamAuthority => teamAuthority;

    private static JsonObject Obj(params (string Key, JsonValue Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs)
        {
            o[k] = v;
        }

        return o;
    }

    private static JsonValue OrNull(string? s) => s is null ? JsonValue.Null : s;

    /// <summary>The team's <c>members</c> read as <c>t.members.&lt;op&gt;(m =&gt; m.field)</c> does: a null element throws.</summary>
    private static IEnumerable<JsonValue> StrictMembers(JsonValue team)
    {
        if (team.IsNull || team.Member("members") is not { IsArray: true } list)
        {
            yield break;
        }

        foreach (var m in list.AsArray())
        {
            if (m.IsNull)
            {
                throw new InvalidOperationException("cannot read a property of a null team member");
            }

            yield return m;
        }
    }

    private static bool RouteHasPane(JsonValue? route) =>
        route is { IsText: true } r && (r.AsString() == "peer" || r.AsString() == "pane");

    // ---------------------------------------------------------------- paths and files

    public string TeamHome(string dir) => TeamHomeDir(dir);

    public string PoolTeamPath(string dir, string? team) => RawTeamPath(dir, team);

    public JsonValue ReadTeamFileAt(string path) => ReadTeamFile(path);


    public JsonObject ReadTeamWithDigest(string path)
    {
        if (teamAuthority is not null)
        {
            // No file is the team: one the database does not hold, or a database never created, is absent whatever lies at the path.
            if (teamAuthority.Read(path) is not { } stored)
            {
                return Obj(("doc", JsonValue.Null), ("digest", "absent"));
            }

            var digest = ContentDigest.OfBytes(stored.Span);
            return Obj(("doc", HeldTeamDocument(path, stored.Span, digest)), ("digest", digest));
        }

        var read = ContentDigest.ReadWithDigest(files, path);
        return read.Bytes is null
            ? Obj(("doc", JsonValue.Null), ("digest", read.Digest))
            : Obj(("doc", HeldTeamDocument(path, read.Bytes, read.Digest)), ("digest", read.Digest));
    }

    private readonly Dictionary<string, (string Digest, JsonValue Doc)> _teamDocs = new(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="TeamDocument"/> of the bytes stored for <paramref name="path"/>, held by (path, digest) (b459 step 13): the same bytes are
    /// parsed once and every reader shares the one document. Callers must not change it; every editor clones first.
    /// </summary>
    private JsonValue HeldTeamDocument(string path, ReadOnlySpan<byte> bytes, string? digest = null)
    {
        digest ??= ContentDigest.OfBytes(bytes);
        lock (_teamDocs)
        {
            if (_teamDocs.TryGetValue(path, out var held) && held.Digest == digest)
            {
                return held.Doc;
            }
        }

        var doc = TeamDocument(bytes);
        lock (_teamDocs)
        {
            _teamDocs[path] = (digest, doc);
        }

        return doc;
    }

    public void ForgetTeamDocs()
    {
        lock (_teamDocs)
        {
            _teamDocs.Clear();
        }
    }

    /// <summary>The team record in <paramref name="bytes"/>, or null when they are not JSON or hold no members array.</summary>
    internal static JsonValue TeamDocument(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var data = JsonText.Parse(bytes);
            return data.IsMap && data.AsObject().Get("members") is { IsArray: true } ? data : JsonValue.Null;
        }
        catch (JsonParseException)
        {
            return JsonValue.Null;
        }
    }

    public JsonObject TeamFileState(string dir, string? team = null) => TeamFileStateAt(TeamPath(dir, team));

    public JsonObject TeamFileStateAt(string path)
    {
        JsonObject State(string state, JsonValue team) => Obj(("state", state), ("path", path), ("team", team));
        if (teamAuthority is { IsDatabase: true } authority)
        {
            if (authority.ReadStored(path) is not { } stored)
            {
                return State("absent", JsonValue.Null);
            }

            var row = HeldTeamDocument(path, stored.Span);
            return row.IsNull ? State("unusable", JsonValue.Null) : State("usable", row);
        }

        if (!files.Exists(path))
        {
            return State("absent", JsonValue.Null);
        }

        string text;
        try
        {
            text = files.ReadText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return State("absent", JsonValue.Null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return State("unusable", JsonValue.Null);
        }

        try
        {
            var data = JsonText.Parse(text);
            return data.IsMap && data.AsObject().Get("members") is { IsArray: true }
                ? State("usable", data)
                : State("unusable", JsonValue.Null);
        }
        catch (JsonParseException)
        {
            return State("unusable", JsonValue.Null);
        }
    }

    // ---------------------------------------------------------------- snapshots

    public string SnapshotsHome(string dir) => NodePath.Join(TeamHomeDir(dir), "snapshots");

    /// <summary>The stamp of a snapshot file name: <c>\d{8}T\d{9}Z</c>, nineteen characters.</summary>
    internal static bool IsSnapshotStamp(string s)
    {
        if (s.Length != 19 || s[8] != 'T' || s[18] != 'Z')
        {
            return false;
        }

        for (var i = 0; i < 18; i++)
        {
            if (i != 8 && s[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The stamp of <paramref name="file"/> when it is <c>&lt;team&gt;-&lt;stamp&gt;.json</c>, else null.</summary>
    internal static string? SnapshotStampOf(string file, string teamName)
    {
        var prefix = teamName + "-";
        if (!file.StartsWith(prefix, StringComparison.Ordinal) || !file.EndsWith(".json", StringComparison.Ordinal))
        {
            return null;
        }

        var middle = file[prefix.Length..^5];
        return IsSnapshotStamp(middle) ? middle : null;
    }

    public IReadOnlyList<JsonObject> ListSnapshots(string dir, string teamName)
    {
        var baseDir = SnapshotsHome(dir);

        // the rows, plus any old file with no row (files-off-disk §12.3); a busy database throws rather than list nothing
        var stored = teamAuthority?.SnapshotFiles(TeamHomeDir(dir), teamName) ?? [];
        IReadOnlyList<string> names;
        try
        {
            names = files.List(baseDir);
        }
        catch (Exception)
        {
            names = [];
        }

        var mine = stored.Concat(names).Distinct(StringComparer.Ordinal).Where(f => SnapshotStampOf(f, teamName) is not null).ToList();
        mine.Sort(StringComparer.Ordinal);
        mine.Reverse();
        return mine.Select(f => Obj(("path", NodePath.Join(baseDir, f)), ("file", f), ("ts", SnapshotStampOf(f, teamName)!))).ToList();
    }

    public JsonValue ReadSnapshot(string path)
    {
        // the row at the path's key (the real path of the team home two levels up, and the file name), else the file (§12.2, §12.3)
        var stored = teamAuthority?.ReadSnapshot(NodePath.Dirname(NodePath.Dirname(path)), NodePath.Basename(path));
        try
        {
            var doc = JsonText.Parse(stored ?? files.ReadText(path));
            return doc.IsMap
                && doc.AsObject().Get("kind") is { IsText: true } k && k.AsString() == "ah-team-snapshot"
                && doc.AsObject().Get("v") is { IsNumber: true } v && v.AsNumber() == 1
                && doc.AsObject().Get("members") is { IsArray: true }
                    ? doc
                    : JsonValue.Null;
        }
        catch (Exception)
        {
            return JsonValue.Null;
        }
    }

    // ---------------------------------------------------------------- members of a team

    public JsonValue TeamMemberRenamedFrom(string dir, string? name, string? team = null)
    {
        var t = ReadTeamRecord(dir, team);
        if (t.IsNull || string.IsNullOrEmpty(name))
        {
            return JsonValue.Null;
        }

        foreach (var m in t.AsObject()["members"].AsArray())
        {
            if (m.IsTruthy && m.Member("renamed_from") is { IsText: true } r && r.AsString() == name)
            {
                return m;
            }
        }

        return JsonValue.Null;
    }

    public JsonArray TeamMembersForRole(string dir, string? role, string? team = null)
    {
        var result = new JsonArray();
        foreach (var m in StrictMembers(ReadTeamRecord(dir, team)))
        {
            if (m.Member("role") is { IsText: true } r && r.AsString() == role && RouteHasPane(m.Member("route")))
            {
                result.Add(m);
            }
        }

        return result;
    }

    public JsonArray TeamMemberNameSet(string dir, string? team = null)
    {
        var seen = new List<JsonValue>();
        foreach (var m in StrictMembers(ReadTeamRecord(dir, team)))
        {
            if (m.Member("name") is { IsTruthy: true } n && !seen.Any(s => KindHarness.JsStrictEquals(s, n)))
            {
                seen.Add(n);
            }
        }

        var array = new JsonArray();
        array.AddRange(seen);
        return array;
    }

    public JsonArray TeamsWithMember(string dir, IReadOnlyList<string?> teams, string? name)
    {
        var result = new JsonArray();
        if (string.IsNullOrEmpty(name))
        {
            return result;
        }

        foreach (var team in teams)
        {
            var names = TeamMemberNameSet(dir, team);
            if (names.Any(n => n.IsText && n.AsString() == name))
            {
                result.Add(OrNull(team));
            }
        }

        return result;
    }

    public string? MemberNamePrefixOf(JsonValue? name, JsonValue? role) => MemberNamePrefix(name, role);

    public string? TeamRosterKey(string dir, string? teamName) => TeamRosterEntry(dir, teamName)?.Key;

    // ---------------------------------------------------------------- scope and attribution

    public JsonObject DefaultTeamScope(string dir, JsonValue prefix, Func<JsonValue, string?> suggestTeamAlias)
    {
        ArgumentNullException.ThrowIfNull(suggestTeamAlias);
        var valid = config.ValidateTeamAlias(prefix).Get("ok") is { IsBool: true } ok && ok.AsBool();
        var legacy = TeamFileState(dir, null);
        var state = legacy["state"].AsString();

        JsonObject Base()
        {
            var o = Obj(("team", JsonValue.Null), ("defaulted", true));
            if (!valid)
            {
                o["unnamable"] = prefix;
                o["suggested"] = OrNull(suggestTeamAlias(prefix));
            }

            return o;
        }

        if (state == "usable")
        {
            return Base();
        }

        if (state == "unusable")
        {
            var o = Base();
            o["unreadable"] = legacy["path"];
            return o;
        }

        if (!valid)
        {
            return Base();
        }

        return Obj(("team", prefix), ("defaulted", true));
    }

    public JsonValue SessionMemberRow(JsonValue team, string? paneId, MemberMarker? marker = null, string? teamName = null)
    {
        if (!team.IsTruthy || team.Member("members") is not { IsArray: true } list)
        {
            return JsonValue.Null;
        }

        if (!string.IsNullOrEmpty(paneId))
        {
            foreach (var m in list.AsArray())
            {
                if (m.IsTruthy && m.Member("transport_id") is { IsText: true } t && t.AsString() == paneId)
                {
                    return m;
                }
            }
        }

        if (marker is null)
        {
            return JsonValue.Null;
        }

        foreach (var m in list.AsArray())
        {
            if (m.IsTruthy && m.Member("name") is { IsText: true } n && n.AsString() == marker.Name && HoldsNoOtherPane(m, paneId) && HoldsNoOtherProcess(m, marker) && IsStartingUnregistered(m, marker, teamName))
            {
                return m;
            }
        }

        return JsonValue.Null;
    }

    private static bool IsStartingUnregistered(JsonValue row, MemberMarker marker, string? teamName) =>
        RosterModule.StartingAgeSeconds(row, marker.Now) is not null && marker.Registered?.Invoke(teamName ?? string.Empty, marker.Name) is not true;

    private static bool HoldsNoOtherPane(JsonValue row, string? paneId) =>
        row.Member("transport_id") is not { IsText: true } t || t.AsString().Length == 0 || t.AsString() == paneId;

    private static bool HoldsNoOtherProcess(JsonValue row, MemberMarker marker) =>
        row.Member("pid") is not { IsNumber: true } pid
        || (pid.AsNumber() == marker.Pid && (row.Member("pid_start") is not { IsText: true } start || start.AsString() == marker.PidStart));

    public JsonValue ResolveSessionTeam(string dir, string? role, string? explicitTeam = null)
    {
        if (!string.IsNullOrEmpty(explicitTeam))
        {
            var team = ReadTeamRecord(dir, explicitTeam);
            return team.IsNull ? JsonValue.Null : Obj(("teamName", explicitTeam), ("team", team));
        }

        JsonObject? match = null;
        foreach (var teamName in new string?[] { null }.Concat(ListTeamNames(dir)))
        {
            var n = TeamMembersForRole(dir, role, teamName).Count;
            if (n == 0)
            {
                continue;
            }

            if (match is not null || n > 1)
            {
                return JsonValue.Null;
            }

            match = Obj(("teamName", OrNull(teamName)), ("team", ReadTeamRecord(dir, teamName)));
        }

        return match is null ? JsonValue.Null : match;
    }

    private static JsonObject Spread(JsonObject source, params (string Key, JsonValue Value)[] more)
    {
        var o = source.Clone();
        foreach (var (k, v) in more)
        {
            o[k] = v;
        }

        return o;
    }

    private JsonValue EnvOf(IEnumerable<string> homes) => EnvTeamFile(homes, environment.GetVariable("WRANGL_TEAM_FILE"));

    public JsonValue EnvTeamFileFor(IReadOnlyList<string> homes, string? value = null) =>
        EnvTeamFile(homes, value ?? environment.GetVariable("WRANGL_TEAM_FILE"));

    public JsonValue AttributeSessionTeam(string dir, string? role, string? explicitTeam = null, string? paneId = null, IReadOnlyList<string>? homes = null)
    {
        if (!string.IsNullOrEmpty(explicitTeam))
        {
            return ResolveSessionTeam(dir, role, explicitTeam);
        }

        var env = EnvOf(homes ?? [dir]);
        if (env.IsMap && env.AsObject().Get("invalid") is null)
        {
            var e = env.AsObject();
            var name = e["teamName"].IsText ? e["teamName"].AsString() : null;
            return Obj(
                ("teamName", e["teamName"]),
                ("team", ReadTeamRecord(e["home"].AsString(), name)),
                ("home", e["home"]),
                ("via", "env"));
        }

        var byPane = PaneResolver(dir)(paneId);
        if (byPane is not null)
        {
            return Obj(
                ("teamName", OrNull(byPane.TeamName)),
                ("team", byPane.Team),
                ("member", byPane.Member),
                ("via", "pane"));
        }

        var byRole = ResolveSessionTeam(dir, role);
        return byRole.IsNull ? JsonValue.Null : Spread(byRole.AsObject(), ("via", "role-scan"));
    }

    public JsonObject LocateSession(string dir, IReadOnlyList<string> homes, string? teamArg, bool teamDefaultExplicit, string? paneId, MemberMarker? marker = null)
    {
        var explicitScope = !string.IsNullOrEmpty(teamArg) || teamDefaultExplicit;
        var env = explicitScope ? JsonValue.Null : EnvOf(homes);
        var envInvalid = new JsonObject();
        if (env.IsMap && env.AsObject().Get("invalid") is { } why)
        {
            envInvalid["env_team_invalid"] = Obj(("value", env.AsObject()["value"]), ("kind", env.AsObject()["kind"]), ("why", why));
        }

        if (env.IsMap && env.AsObject().Get("invalid") is null)
        {
            var e = env.AsObject();
            var name = e["teamName"].IsText ? e["teamName"].AsString() : null;
            var team = ReadTeamRecord(e["home"].AsString(), name);
            return Obj(
                ("answeredBy", "env"),
                ("home", e["home"]),
                ("teamName", e["teamName"]),
                ("team", team),
                ("member", SessionMemberRow(team, paneId, marker, name)),
                ("reason", team.IsNull ? "no-team" : JsonValue.Null),
                ("envInvalid", envInvalid));
        }

        if (string.IsNullOrEmpty(paneId))
        {
            return Obj(("answeredBy", JsonValue.Null), ("reason", "no-pane-id"), ("envInvalid", envInvalid));
        }

        var records = new List<(string Home, string? TeamName, JsonValue Team)>();
        foreach (var home in homes.Where(h => h.Length > 0))
        {
            var names = explicitScope
                ? new List<string?> { string.IsNullOrEmpty(teamArg) ? null : teamArg }
                : new string?[] { null }.Concat(ListTeamNames(home)).ToList();
            foreach (var teamName in names)
            {
                var team = ReadTeamRecord(home, teamName);
                if (!team.IsNull)
                {
                    records.Add((home, teamName, team));
                }
            }
        }

        var distinct = new List<(string Home, string? TeamName, JsonValue Team)>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (seenPaths.Add(TeamPath(r.Home, r.TeamName)))
            {
                distinct.Add(r);
            }
        }

        if (distinct.Count == 0)
        {
            return Obj(("answeredBy", JsonValue.Null), ("reason", "no-team"), ("envInvalid", envInvalid));
        }

        var matches = new List<(string Home, string? TeamName, JsonValue Team, JsonValue Member)>();
        foreach (var r in distinct)
        {
            var members = r.Team.Member("members") is { IsArray: true } list ? list.AsArray() : new JsonArray();
            foreach (var m in members)
            {
                if (m.IsTruthy && m.Member("transport_id") is { IsText: true } t && t.AsString() == paneId)
                {
                    matches.Add((r.Home, r.TeamName, r.Team, m));
                }
            }
        }

        if (matches.Count == 0)
        {
            return Obj(("answeredBy", JsonValue.Null), ("reason", "not-a-member"), ("envInvalid", envInvalid));
        }

        if (matches.Count > 1)
        {
            var candidates = new JsonArray();
            foreach (var m in matches)
            {
                var n = m.Member.Member("name");
                candidates.Add(Obj(("team", OrNull(m.TeamName)), ("name", n is null || n.Value.IsNull ? JsonValue.Null : n.Value)));
            }

            return Obj(("answeredBy", JsonValue.Null), ("reason", "ambiguous"), ("candidates", candidates), ("envInvalid", envInvalid));
        }

        var hit = matches[0];
        return Obj(
            ("answeredBy", "pane"),
            ("home", hit.Home),
            ("teamName", OrNull(hit.TeamName)),
            ("team", hit.Team),
            ("member", hit.Member),
            ("reason", JsonValue.Null),
            ("envInvalid", envInvalid));
    }

    public JsonValue ExpectedRootFor(JsonValue team, JsonValue member, string? envRoot = null)
    {
        if (member.IsTruthy && member.Member("expected_root") is { IsTruthy: true } own)
        {
            return own;
        }

        if (!string.IsNullOrEmpty(envRoot))
        {
            return envRoot;
        }

        if (team.IsTruthy && team.Member("expected_root") is { IsTruthy: true } teams)
        {
            return teams;
        }

        return JsonValue.Null;
    }

    public JsonValue SplitTeamCopy(string dir, string? team = null)
    {
        var home = TeamHomeDir(dir);
        if (home == dir)
        {
            return JsonValue.Null;
        }

        var pool = RawTeamPath(dir, team);
        if (!TeamExistsAt(RawTeamPath(home, team)))
        {
            return JsonValue.Null;
        }

        var record = ReadTeamFile(pool);
        return record.IsNull ? JsonValue.Null : Obj(("path", pool), ("team", record));
    }

    public bool IsTeamOwnedBy(JsonValue team, TeamInvoker invoker) => TeamOwnedBy(team, invoker);

    // ---------------------------------------------------------------- history

    public string HistoryPath(string dir) => NodePath.Join(TeamHomeDir(dir), "team-history.json");

    public JsonObject ReadHistory(string dir)
    {
        JsonObject Empty() => Obj(("version", 1), ("teams", new JsonArray()));

        // the row when the database holds one; else the file, which the first history write then carries into a row (files-off-disk §12.3)
        var stored = teamAuthority?.ReadHistory(TeamHomeDir(dir));
        var path = HistoryPath(dir);
        if (stored is null && !files.Exists(path))
        {
            return Empty();
        }

        try
        {
            var data = JsonText.Parse(stored ?? files.ReadText(path));
            return data.IsMap && data.AsObject().Get("teams") is { IsArray: true } ? data.AsObject() : Empty();
        }
        catch (Exception)
        {
            return Empty();
        }
    }

    public string Fingerprint(JsonValue rosterLevel, JsonValue transport, JsonValue? members)
    {
        var canonical = new JsonObject
        {
            ["roster_level"] = rosterLevel.IsTruthy ? rosterLevel : JsonValue.Null,
            ["transport"] = transport.IsTruthy ? transport : JsonValue.Null,
        };
        if (members is { } m)
        {
            canonical["members"] = m;
        }

        var digest = ContentDigest.OfBytes(JsonText.ToUtf8(canonical, 0));
        return digest[..8];
    }

    public bool HistoryEntryIsActive(string dir, JsonValue entry, DateTimeOffset now)
    {
        var alias = entry.Member("alias") is { IsTruthy: true } a && a.IsText ? a.AsString() : null;
        var t = ReadTeamRecord(dir, alias);
        if (!TeamIsLive(t, null, now))
        {
            return false;
        }

        return KindHarness.JsStrictEquals(t.Member("team_id"), entry.Member("last_team_id"));
    }
}
