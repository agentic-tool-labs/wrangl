using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Roster;

/// <summary>The team-file reads of the Node <c>lib-roster</c> that every other area depends on, and the <see cref="ITeamFiles"/> implementation.</summary>
internal sealed partial class RosterCore(
    IFileReader files,
    IAhEnvironment environment,
    IConfigCore config,
    IProcessInspector processes,
    IProcessTable? processTable = null,
    SessionProofs? proofs = null,
    ITeamAuthority? teamAuthority = null,
    IHoldSource? holds = null) : IRosterCore
{
    private static string Resolve(string path) => NodePath.Resolve("/", path);

    // ---------------------------------------------------------------- paths

    internal string TeamHomeDir(string dir)
    {
        return AhPaths.RepoRootOf(dir) is { } root ? config.MainHierarchyDir(root) ?? dir : dir;
    }

    private static string RawTeamPath(string dir, string? team) =>
        string.IsNullOrEmpty(team) ? NodePath.Join(dir, "team.json") : NodePath.Join(dir, "teams", $"{team}.json");

    public string TeamPathAt(string dir, string team) => RawTeamPath(dir, team);

    public string TeamPath(string dir, string? team = null)
    {
        var home = TeamHomeDir(dir);
        var homePath = RawTeamPath(home, team);
        if (home == dir || TeamExistsAt(homePath))
        {
            return homePath;
        }

        var legacy = RawTeamPath(dir, team);
        return TeamExistsAt(legacy) ? legacy : homePath;
    }

    public bool TeamExistsAt(string path) =>
        teamAuthority is { IsDatabase: true } authority ? authority.ReadStored(path) is not null : files.Exists(path);

    public long? TeamsChangeToken() => teamAuthority is { IsDatabase: true } authority ? authority.ChangeToken() : null;

    public string TeamStamp(string path) =>
        TeamsChangeToken() is { } token ? "db:" + token.ToString(System.Globalization.CultureInfo.InvariantCulture) : Ah.Core.Files.FileStamps.Of(files, path);

    public IReadOnlyList<string>? TeamNamesIn(string dir) => teamAuthority is { IsDatabase: true } authority ? authority.ListTeams(dir) : null;

    internal static string? TeamFileHome(string path) =>
        NodePath.Basename(path) == "team.json" ? NodePath.Dirname(path)
        : NodePath.Basename(NodePath.Dirname(path)) == "teams" ? NodePath.Dirname(NodePath.Dirname(path))
        : null;

    // ---------------------------------------------------------------- reading

    private JsonValue ReadTeamFile(string path)
    {
        // The database first and on its own: a busy store throws here, where the fence check below would read it as absent.
        if (teamAuthority is { IsDatabase: true } authority)
        {
            return authority.ReadStored(path) is { } stored ? HeldTeamDocument(path, stored.Span) : JsonValue.Null;
        }

        if (!files.Exists(path))
        {
            return JsonValue.Null;
        }

        try
        {
            var data = JsonText.Parse(files.ReadText(path));
            return data.IsMap && data.AsObject().Get("members") is { IsArray: true } ? data : JsonValue.Null;
        }
        catch (Exception)
        {
            return JsonValue.Null;
        }
    }

    public JsonValue ReadTeamRecord(string dir, string? team = null) => ReadTeamFile(TeamPath(dir, team));

    public ReadOnlyMemory<byte>? ReadTeam(string hierarchyDirectory, string? team = null)
    {
        var record = ReadTeamRecord(hierarchyDirectory, team);
        return record.IsNull ? null : JsonText.ToUtf8(record, 0);
    }

    public IReadOnlyList<string> ListTeamNames(string dir)
    {
        var names = new List<string>();
        var seenDirs = new List<string>();
        foreach (var d in new[] { TeamHomeDir(dir), dir })
        {
            if (seenDirs.Contains(d))
            {
                continue;
            }

            seenDirs.Add(d);
            if (TeamNamesIn(d) is { } rows)
            {
                names.AddRange(rows.Where(n => config.IsTeamAliasShape(n) && !names.Contains(n)));
                continue;
            }

            IReadOnlyList<string> entries;
            try
            {
                entries = files.List(NodePath.Join(d, "teams"));
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var f in entries)
            {
                var n = f.Length >= 5 ? f[..^5] : string.Empty;
                if (f.EndsWith(".json", StringComparison.Ordinal) && config.IsTeamAliasShape(n) && !names.Contains(n))
                {
                    names.Add(n);
                }
            }
        }

        return names;
    }

    // ---------------------------------------------------------------- names and prefixes

    private const string SubOrchestratorRole = "sub-orchestrator";

    private const string SubOrchestratorInfix = "-so-";

    /// <summary>
    /// The prefix of a derived member name <c>&lt;prefix&gt;-&lt;role&gt;[-N]</c>, or null when the name does not end in its role. A
    /// sub-orchestrator's name carries no role suffix, so it has no prefix by this rule; <see cref="SubOrchestratorTeams"/> reads it.
    /// </summary>
    internal static string? MemberNamePrefix(JsonValue? name, JsonValue? role)
    {
        if (name is not { IsText: true } n || role is not { IsText: true } r || r.AsString().Length == 0)
        {
            return null;
        }

        var text = n.AsString();
        var suffix = "-" + r.AsString();
        // `^(.+)-<role>(?:-\d+)?$`: the longest prefix of one or more non-terminator characters.
        for (var cut = text.Length - suffix.Length; cut >= 1; cut--)
        {
            if (string.CompareOrdinal(text, cut, suffix, 0, suffix.Length) != 0)
            {
                continue;
            }

            var after = cut + suffix.Length;
            var ok = after == text.Length
                || (text[after] == '-' && after + 1 < text.Length && text.AsSpan(after + 1).IndexOfAnyExceptInRange('0', '9') < 0);
            if (ok && text.AsSpan(0, cut).IndexOfAny(['\n', '\r', '\u2028', '\u2029']) < 0)
            {
                return text[..cut];
            }
        }

        return null;
    }

    public string? FindChildTeam(string hierarchyDirectory, string manager, string stream)
    {
        var found = new List<(string Name, JsonValue Record)>();
        foreach (var name in ListTeamNames(hierarchyDirectory))
        {
            var record = ReadTeamRecord(hierarchyDirectory, name);
            if (record.Member("parent") is { IsMap: true } parent
                && parent.Member("team") is { IsText: true } team && team.AsString() == manager
                && parent.Member("stream") is { IsText: true } ws && ws.AsString() == stream)
            {
                found.Add((name, record));
            }
        }

        return found
            .OrderBy(f => TeamIsOrphaned(f.Record) ? 1 : 0)
            .ThenByDescending(f => f.Record.Member("created") is { IsText: true } created ? created.AsString() : string.Empty, StringComparer.Ordinal)
            .Select(f => f.Name)
            .FirstOrDefault();
    }

    public IReadOnlyList<string> SubOrchestratorTeams(string hierarchyDirectory, string memberName)
    {
        var teams = new List<string>();
        var at = memberName.IndexOf(SubOrchestratorInfix, 1, StringComparison.Ordinal);
        while (at >= 1 && at + SubOrchestratorInfix.Length < memberName.Length)
        {
            var team = memberName[..at];
            var stream = memberName[(at + SubOrchestratorInfix.Length)..];
            var record = ReadTeamRecord(hierarchyDirectory, team);
            if (record.Member("streams") is { IsMap: true } streams && streams.AsObject().Get(stream) is { IsMap: true } entry
                && !(entry.Member("state") is { IsText: true } state && state.AsString() == "done"))
            {
                teams.Add(team);
            }

            at = memberName.IndexOf(SubOrchestratorInfix, at + 1, StringComparison.Ordinal);
        }

        return teams;
    }

    public string? LegacyTeamPrefix(string hierarchyDirectory, string? expectedPrefix = null)
    {
        var team = ReadTeamRecord(hierarchyDirectory, null);
        if (team.IsNull)
        {
            return null;
        }

        foreach (var m in team.AsObject()["members"].AsArray())
        {
            var prefix = m.IsTruthy ? MemberNamePrefix(m.Member("name"), m.Member("role")) : null;
            if (!string.IsNullOrEmpty(expectedPrefix) && m.IsTruthy && m.Member("name") is { IsText: true } cappedName && m.Member("role") is { IsText: true } cappedRole
                && Ah.Core.Text.MemberNames.Ordinal(cappedName.AsString(), expectedPrefix, cappedRole.AsString()) > 0)
            {
                return expectedPrefix;
            }

            if (prefix is null && m.Member("role") is { IsText: true } soRole && soRole.AsString() == SubOrchestratorRole && m.Member("name") is { IsText: true } soName
                && SubOrchestratorTeams(hierarchyDirectory, soName.AsString()) is [var only])
            {
                prefix = only;
            }

            if (!string.IsNullOrEmpty(prefix))
            {
                return prefix;
            }
        }

        return null;
    }

    public RosterBlockKey? TeamRosterEntry(string hierarchyDirectory, string? team)
    {
        var t = ReadTeamRecord(hierarchyDirectory, team);
        if (t.IsNull)
        {
            return null;
        }

        if (!t.AsObject().ContainsKey("roster"))
        {
            return new RosterBlockKey(string.IsNullOrEmpty(team) ? null : team);
        }

        return new RosterBlockKey(t.AsObject()["roster"] is { IsText: true } r && r.AsString().Length > 0 ? r.AsString() : null);
    }

    // ---------------------------------------------------------------- ownership and attribution

    /// <summary>
    /// The invoker owns the team by one of two proofs: the team's recorded Orchestrator session is one the invoker's process
    /// provably holds (its registration's pid and start are a live process in the invoker's line), or the invoker's process is the
    /// recorded Orchestrator pid with the recorded start time. A claimed session id, or a pid without a start, owns nothing.
    /// </summary>
    private bool TeamOwnedBy(JsonValue team, TeamInvoker invoker)
    {
        if (team.IsNull || invoker.Pid is not { } pid || pid <= 1)
        {
            return false;
        }

        var orch = team.Member("orchestrator") is { IsTruthy: true } o ? o : (JsonValue)new JsonObject();
        if (orch.Member("session_id") is { IsText: true } session && session.AsString().Length > 0
            && proofs?.CallerSessions(pid) is { } held && held.Contains(session.AsString()))
        {
            return true;
        }

        return (JsConvert.ToNumber(orch.Member("pid")) == pid && StartProves(pid, invoker.PidStart, orch.Member("pid_start")))
            || ProvesAsDeputy(team, pid, invoker.PidStart);
    }

    /// <summary>
    /// The on-duty deputy owns the team too: the invoker proves as the member row <c>duty.deputy</c> names, by the same two proofs
    /// (the row's session is one the invoker's process holds, or the row's pid with its recorded start). Either proof is of a running
    /// process, so a dead deputy proves nothing and the chief, still the recorded owner, is the only owner again.
    /// </summary>
    private bool ProvesAsDeputy(JsonValue team, int pid, string? pidStart)
    {
        if (team.Member("duty")?.Member("deputy") is not { IsText: true } deputy || deputy.AsString().Length == 0
            || team.Member("members") is not { IsArray: true } members
            || members.AsArray().FirstOrDefault(m => m.Member("name") is { IsText: true } n && n.AsString() == deputy.AsString()) is not { IsMap: true } row
            || row.Member("role") is not { IsText: true } role || config.RoleClass(role.AsString(), null) != "orchestrate")
        {
            // a row whose role is not of the orchestrate class is no deputy, whatever duty names it
            return false;
        }

        if (row.Member("session_id") is { IsText: true } session && session.AsString().Length > 0
            && proofs?.CallerSessions(pid) is { } held && held.Contains(session.AsString()))
        {
            return true;
        }

        return JsConvert.ToNumber(row.Member("pid")) == pid && StartProves(pid, pidStart, row.Member("pid_start"));
    }

    /// <summary>
    /// Whether the recorded start time is the start time of the process now holding <paramref name="pid"/> (the invoker's known
    /// start, else the table's). A missing start on either side proves nothing: a pid alone may be a reused one.
    /// </summary>
    private bool StartProves(int pid, string? current, JsonValue? recorded)
    {
        current = string.IsNullOrEmpty(current) ? processTable?.StartTime(pid) : current;
        if (string.IsNullOrEmpty(current) || recorded is not { IsText: true } text || text.AsString().Length == 0)
        {
            return false;
        }

        return ProcessStart.IsSame(ProcessStart.Compare(current, () => processTable?.LocalStartTime(pid), text.AsString()));
    }

    public IReadOnlyList<string?> OwnedTeams(string hierarchyDirectory, TeamInvoker invoker) =>
        [.. ImplicitTeams.WithoutYielded(OwnedTeamRecords(hierarchyDirectory, invoker), static t => t.Team).Select(static t => t.Name)];

    public IReadOnlyList<string?> AllOwnedTeams(string hierarchyDirectory, TeamInvoker invoker) =>
        [.. OwnedTeamRecords(hierarchyDirectory, invoker).Select(static t => t.Name)];

    private List<(string? Name, JsonValue Team)> OwnedTeamRecords(string hierarchyDirectory, TeamInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        if (invoker.Pid is null && string.IsNullOrEmpty(invoker.SessionId))
        {
            return [];
        }

        var names = ListTeamNames(hierarchyDirectory).ToList();
        names.Sort(StringComparer.Ordinal);
        var result = new List<(string? Name, JsonValue Team)>();
        foreach (var name in new string?[] { null }.Concat(names))
        {
            var team = ReadTeamRecord(hierarchyDirectory, name);
            if (TeamOwnedBy(team, invoker))
            {
                result.Add((name, team));
            }
        }

        return result;
    }

    public IReadOnlyList<string?> TeamsOfSession(string hierarchyDirectory, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || processTable is null || proofs?.LiveRegistration(sessionId) is not { } registration
            || !ProcessIdentity.Same(processTable, registration.Pid, registration.Start))
        {
            return [];
        }

        var names = ListTeamNames(hierarchyDirectory).ToList();
        names.Sort(StringComparer.Ordinal);
        var result = new List<(string? Name, JsonValue Team)>();
        foreach (var name in new string?[] { null }.Concat(names))
        {
            var team = ReadTeamRecord(hierarchyDirectory, name);
            if (team.IsNull)
            {
                continue;
            }

            var orch = team.Member("orchestrator") is { IsTruthy: true } o ? o : (JsonValue)new JsonObject();
            var bySession = orch.Member("session_id") is { IsText: true } s && s.AsString() == sessionId;
            var byPid = JsConvert.ToNumber(orch.Member("pid")) == registration.Pid
                && orch.Member("pid_start") is { IsText: true } recorded && recorded.AsString().Length > 0
                && ProcessStart.IsSame(ProcessStart.Compare(registration.Start, () => processTable.LocalStartTime(registration.Pid), recorded.AsString()));
            if (bySession || byPid)
            {
                result.Add((name, team));
            }
        }

        return [.. ImplicitTeams.WithoutYielded(result, static t => t.Team).Select(static t => t.Name)];
    }

    public TeamLife TeamLifeOf(JsonValue team) => TeamLifeWhy(team).Life;

    public (TeamLife Life, string Why) TeamLifeWhy(JsonValue team) =>
        TeamLives.Judge(team, proofs is null ? null : proofs.LiveRegistration, processTable, DateTimeOffset.UtcNow, Environment.ProcessId, proofs?.RegisteredProcesses?.Invoke());

    public bool TeamIsOrphaned(JsonValue team) => TeamLifeOf(team) == TeamLife.Orphaned;

    private (string? TeamName, string Home, string Via)? ResolveTeamByPane(string dir, string? paneId, bool liveOnly)
    {
        if (string.IsNullOrEmpty(paneId))
        {
            return null;
        }

        var candidates = new List<(string? Name, JsonValue Team)>();
        foreach (var name in new string?[] { null }.Concat(ListTeamNames(dir)))
        {
            var team = ReadTeamRecord(dir, name);
            if (!liveOnly || (!team.IsNull && !TeamIsOrphaned(team)))
            {
                candidates.Add((name, team));
            }
        }

        string? matchName = null;
        var matched = false;
        foreach (var (name, team) in candidates)
        {
            if (team.IsNull)
            {
                continue;
            }

            var rows = team.AsObject()["members"].AsArray()
                .Count(m => m.IsTruthy && m.Member("transport_id") is { IsText: true } t && t.AsString() == paneId);
            if (rows == 0)
            {
                continue;
            }

            if (matched || rows > 1)
            {
                return null;
            }

            matched = true;
            matchName = name;
        }

        return matched ? (matchName, dir, "pane") : null;
    }

    private string RealPrefixPath(string path)
    {
        try
        {
            return files.RealPath(path);
        }
        catch (Exception)
        {
            var parent = NodePath.Dirname(path);
            return parent == path ? path : NodePath.Join(RealPrefixPath(parent), NodePath.Basename(path));
        }
    }

    /// <summary>
    /// <c>envTeamFile</c>: the team file this session was launched into (<c>WRANGL_TEAM_FILE</c>), accepted only as
    /// <c>&lt;H&gt;/teams/&lt;name&gt;.json</c> or <c>&lt;H&gt;/team.json</c> for one of <paramref name="homes"/>.
    /// Null when unset; otherwise <c>{invalid, kind, value}</c> or <c>{home, teamName}</c>.
    /// </summary>
    internal JsonValue EnvTeamFile(IEnumerable<string> homes, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return JsonValue.Null;
        }

        JsonValue Malformed(string why) => new JsonObject { ["invalid"] = why, ["kind"] = "malformed", ["value"] = value };

        if (!NodePath.IsAbsolute(value))
        {
            return Malformed("it is not an absolute path");
        }

        if (value.Split('/').Contains(".."))
        {
            return Malformed("it climbs directories with `..`");
        }

        var path = Resolve(value);
        var home = TeamFileHome(path);
        if (home is null)
        {
            return Malformed("it is not a team file path (<hierarchy dir>/teams/<name>.json or <hierarchy dir>/team.json)");
        }

        var legacy = NodePath.Basename(path) == "team.json";
        string? teamName = legacy ? null : JsonJsonSuffix().Replace(NodePath.Basename(path), string.Empty, 1);
        if (!legacy && (!path.EndsWith(".json", StringComparison.Ordinal) || !config.ValidateTeamAlias(teamName!).Get("ok")!.Value.AsBool()))
        {
            return Malformed("it does not name a team file with a valid team name");
        }

        var realHome = RealPrefixPath(home);
        var candidates = homes.Where(h => h.Length > 0).ToList();
        var match = candidates.FirstOrDefault(h => RealPrefixPath(Resolve(h)) == realHome);
        if (match is not null)
        {
            return new JsonObject { ["home"] = match, ["teamName"] = teamName is null ? JsonValue.Null : teamName };
        }

        var viaHome = candidates.Select(TeamHomeDir).FirstOrDefault(h => RealPrefixPath(Resolve(h)) == realHome);
        if (viaHome is not null)
        {
            return new JsonObject { ["home"] = viaHome, ["teamName"] = teamName is null ? JsonValue.Null : teamName };
        }

        static bool IsHierarchyDir(string h) =>
            AhPaths.RepoRootOf(h) is not null || AhPaths.IsUserProjectDir(h);

        if (!IsHierarchyDir(home))
        {
            return Malformed("it is not inside a hierarchy dir");
        }

        return new JsonObject
        {
            ["invalid"] = $"it is a team file of another repo's hierarchy dir ({home}), not this one's",
            ["kind"] = "other-repo",
            ["value"] = value,
        };
    }

    public MemberTeamMatch? MemberTeam(string hierarchyDirectory, IReadOnlyList<string> homes, string? paneId)
    {
        var env = EnvTeamFile(homes, environment.GetVariable("WRANGL_TEAM_FILE"));
        if (env.IsMap && env.AsObject().Get("invalid") is null)
        {
            var o = env.AsObject();
            return new MemberTeamMatch(o["teamName"].IsText ? o["teamName"].AsString() : null, o["home"].AsString(), "env");
        }

        var byPane = ResolveTeamByPane(hierarchyDirectory, paneId, liveOnly: true);
        return byPane is null ? null : new MemberTeamMatch(byPane.Value.TeamName, hierarchyDirectory, "pane");
    }

    [GeneratedRegex("\\.json\\z", RegexOptions.CultureInvariant)]
    private static partial Regex JsonJsonSuffix();
}
