using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;
using Ah.Roster;

namespace Ah.Hierarchy;

/// <summary>The session that owns a lane's child team: the sub-orchestrator that creates it.</summary>
public sealed record LaneChildOwner(string? SessionId, int Pid, string? PidStart);

/// <summary>
/// What <see cref="LaneChildTeam.Ensure"/> did: the child team's name, whether this call wrote it, and why it could not. When the
/// call took an orphaned child over, <see cref="AdoptedFrom"/> names the session (or pid) that held it.
/// </summary>
public sealed record LaneChildResult(string? Team, bool Created, string? Refusal, string? AdoptedFrom = null)
{
    public bool Adopted => AdoptedFrom is not null;
}

/// <summary>
/// The child team of a lane (<c>&lt;manager&gt;-&lt;stream&gt;</c>): born empty, <c>parent</c>-marked, with a copy of the manager's stream and a
/// workstream status. Both <c>create --parent</c> and a sub-orchestrator's session start build it here.
/// </summary>
internal static class LaneChildTeam
{
    public static string NameOf(string manager, string stream) => $"{manager}-{stream}";

    /// <summary>
    /// Whether <paramref name="managerRecord"/>, the record of team <paramref name="manager"/>, has a stream entry, open or done, whose
    /// lane child is <paramref name="childTeam"/>. It reads only the manager's side: the child's own record is not consulted.
    /// </summary>
    public static bool ManagerNames(string manager, JsonValue managerRecord, string childTeam) =>
        managerRecord.Member("streams") is { IsMap: true } streams && streams.AsObject().Keys.Any(stream => NameOf(manager, stream) == childTeam);

    /// <summary>What the team document carries because it is a child: its parent, its copy of the stream, and a workstream status.</summary>
    public static void AddRecords(JsonObject team, string parent, string stream, JsonValue streamRecord, string nowIso, string? parentTeamId = null)
    {
        var link = new JsonObject { ["team"] = parent, ["stream"] = stream };
        if (parentTeamId is { Length: > 0 })
        {
            link["team_id"] = parentTeamId;
        }

        team["parent"] = link;
        team["streams"] = new JsonObject
        {
            [stream] = new JsonObject
            {
                ["state"] = "open",
                ["branch"] = OrNull(streamRecord, "branch"),
                ["base"] = OrNull(streamRecord, "base"),
                ["worktree"] = OrNull(streamRecord, "worktree"),
                ["tab_id"] = OrNull(streamRecord, "tab_id"),
                ["opened"] = nowIso,
            },
        };
        team["workstream"] = new JsonObject { ["state"] = "working", ["summary"] = string.Empty, ["updated"] = nowIso };
    }

    private static JsonValue OrNull(JsonValue record, string key) => record.Member(key) is { } v ? v : JsonValue.Null;

    /// <summary>
    /// Makes sure the lane's child team exists, empty and owned by <paramref name="owner"/>; a child that already exists is left as it is,
    /// unless <paramref name="adoptOrphan"/> says the caller has proved it is the lane's sub-orchestrator, in which case an existing
    /// child is re-owned when its owner is gone (see <see cref="Adopt"/>). Nothing is written when it is refused.
    /// </summary>
    public static LaneChildResult Ensure(
        IHierarchyStateCalls calls,
        IRosterCore roster,
        JsonValue managerTeam,
        IHierarchyProgress? progress,
        string dir,
        string progressPool,
        string manager,
        string stream,
        LaneChildOwner owner,
        JsonValue teamId,
        string nowIso,
        StateCallOptions? options = null,
        bool adoptOrphan = false)
    {
        if (OpenStreamRefusal(managerTeam, manager, stream) is { } closed)
        {
            return new LaneChildResult(null, false, closed);
        }

        var found = managerTeam.Member("streams")!.Value.Member(stream)!.Value;

        if (roster.FindChildTeam(dir, manager, stream) is { } existing)
        {
            return adoptOrphan ? Adopt(calls, roster, dir, manager, stream, existing, owner, options) : new LaneChildResult(existing, false, null);
        }

        if (managerTeam.Member("parent") is { IsTruthy: true })
        {
            return new LaneChildResult(null, false, $"stream {stream}: team {manager} is itself a sub-team, and sub-orchestrators go one level deep");
        }

        var name = NameOf(manager, stream);
        if (!roster.ReadTeamRecord(dir, name).IsNull)
        {
            return new LaneChildResult(null, false, $"stream {stream}: team {name} already exists and is not the child of stream {stream}");
        }

        var orchestrator = new JsonObject { ["session_id"] = owner.SessionId is { Length: > 0 } sid ? sid : JsonValue.Null, ["pid"] = owner.Pid };
        if (owner.PidStart is { Length: > 0 } start)
        {
            orchestrator["pid_start"] = start;
        }

        var team = new JsonObject
        {
            ["version"] = 1,
            ["team_id"] = teamId,
            ["created"] = nowIso,
            ["roster_level"] = OrNull(managerTeam, "roster_level"),
            ["transport"] = OrNull(managerTeam, "transport"),
            ["orchestrator"] = orchestrator,
            ["members"] = new JsonArray(),
            ["expected_root"] = OrNull(managerTeam, "expected_root"),
            ["roster"] = JsonValue.Null,
            ["layout"] = OrNull(managerTeam, "layout"),
        };
        if (managerTeam.Member(Ah.Core.Mux.HerdrFlavour.Field) is { } flavour)
        {
            team[Ah.Core.Mux.HerdrFlavour.Field] = flavour;
        }

        AddRecords(team, manager, stream, found, nowIso, managerTeam.Member("team_id") is { IsText: true } managerId ? managerId.AsString() : null);

        var called = false;
        var wrote = false;
        _ = calls.UpdateTeam(
            dir,
            name,
            doc =>
            {
                called = true;
                wrote = !doc.IsMap;
                return wrote ? TeamFileOutcome.Write(team) : null;
            },
            options);
        if (!called)
        {
            return new LaneChildResult(null, false, $"stream {stream}: the team update for {name} did not run, so nothing was written");
        }

        if (!wrote)
        {
            return roster.FindChildTeam(dir, manager, stream) is { } raced
                ? new LaneChildResult(raced, false, null)
                : new LaneChildResult(null, false, $"stream {stream}: team {name} appeared while the lane's child team was being created and is not the child of stream {stream}");
        }

        EnsureGrants(calls, roster, dir, manager, name, options);
        EnsureChecklist(progress, progressPool, name, stream, found);
        return new LaneChildResult(name, true, null);
    }

    /// <summary>Why the lane has no stream to hold a child team: its manager is missing, or the manager has no stream of that name that is not done; null when it does.</summary>
    private static string? OpenStreamRefusal(JsonValue managerTeam, string manager, string stream)
    {
        if (managerTeam.IsNull)
        {
            return $"stream {stream}: team {manager} does not exist";
        }

        var streamRecord = managerTeam.Member("streams") is { IsMap: true } streams ? streams.Member(stream) : null;
        return streamRecord is not { IsMap: true } found || found.Member("state") is { IsText: true } state && state.AsString() == "done"
            ? $"stream {stream}: team {manager} has no open stream named {stream}"
            : null;
    }

    /// <summary>
    /// Re-owns the existing child of a lane for its sub-orchestrator, under the child's team lock. The child must be the lane's own
    /// (named <c>&lt;manager&gt;-&lt;stream&gt;</c>, its <c>parent</c> naming the manager and stream). A child already held by the caller's
    /// session or process only has its recorded process brought up to date. Otherwise it is taken over only when its owner is
    /// provably gone; a live owner or an owner that cannot be checked is refused. Only <c>orchestrator{}</c> is rewritten, with the old
    /// owner kept as <c>adopted_from</c>.
    /// </summary>
    internal static LaneChildResult Adopt(IHierarchyStateCalls calls, IRosterCore roster, string dir, string manager, string stream, string existing, LaneChildOwner owner, StateCallOptions? options)
    {
        if (OpenStreamRefusal(roster.ReadTeamRecord(dir, manager), manager, stream) is { } closed)
        {
            return new LaneChildResult(null, false, closed);
        }

        LaneChildResult? outcome = null;
        _ = calls.UpdateTeam(
            dir,
            existing,
            doc =>
            {
                outcome = new LaneChildResult(existing, false, null);
                if (!doc.IsMap)
                {
                    return null;
                }

                if (existing != NameOf(manager, stream) || doc.Member("parent") is not { IsMap: true } link
                    || Text(link, "team") != manager || Text(link, "stream") != stream)
                {
                    outcome = new LaneChildResult(null, false, $"not-child-team: {existing} is not the child team of stream {stream} of team {manager}");
                    return null;
                }

                var current = doc.Member("orchestrator") is { IsMap: true } held ? held.AsObject() : new JsonObject();
                var heldSession = Text(current, "session_id");
                var heldPid = Ah.Core.Json.JsConvert.AsInteger(current.Get("pid"));
                var heldStart = Text(current, "pid_start");
                var sameSession = owner.SessionId is { Length: > 0 } sid && heldSession == sid;
                var sameProcess = heldPid == owner.Pid && owner.PidStart is { Length: > 0 } start && heldStart == start;
                var life = sameSession || sameProcess ? TeamLife.OwnedLive : roster.TeamLifeOf(doc);
                if (life == TeamLife.Indeterminate)
                {
                    outcome = new LaneChildResult(null, false, $"lane-child-owner-unknown: whether {existing} is still held cannot be checked, so it was not taken over");
                    return null;
                }

                if (life == TeamLife.OwnedLive && !sameSession && !sameProcess)
                {
                    outcome = new LaneChildResult(null, false, $"lane-child-owned: {existing} is held by live session {heldSession ?? "(unknown)"} (pid {heldPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"})");
                    return null;
                }

                var taken = sameSession || sameProcess;
                if (taken && sameSession && sameProcess)
                {
                    return null;
                }

                var next = current.Clone();
                next["session_id"] = owner.SessionId is { Length: > 0 } newSession ? newSession : JsonValue.Null;
                next["pid"] = owner.Pid;
                if (owner.PidStart is { Length: > 0 } newStart)
                {
                    next["pid_start"] = newStart;
                }

                if (!taken)
                {
                    next["adopted_from"] = new JsonObject
                    {
                        ["session_id"] = heldSession is null ? JsonValue.Null : heldSession,
                        ["pid"] = heldPid is { } oldPid ? (double)oldPid : JsonValue.Null,
                    };
                    outcome = new LaneChildResult(existing, false, null, heldSession ?? $"pid {heldPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}");
                }

                var copy = doc.AsObject().Clone();
                copy["orchestrator"] = next;
                return TeamFileOutcome.Write(copy);
            },
            options);
        return outcome ?? new LaneChildResult(null, false, $"the team update for {existing} did not run, so it was not taken over");
    }

    private static string? Text(JsonValue? record, string key) => record?.Member(key) is { IsText: true } text && text.AsString().Length > 0 ? text.AsString() : null;

    private static void EnsureGrants(IHierarchyStateCalls calls, IRosterCore roster, string dir, string manager, string child, StateCallOptions? options)
    {
        try
        {
            var home = roster.TeamHome(dir);
            var parent = calls.Grants("status", new JsonObject { ["dir"] = home, ["team"] = manager }, options);
            if (!parent.IsMap || parent.Member("present") is not { IsTruthy: true } || parent.Member("mode") is not { IsText: true } mode || mode.AsString().Length == 0)
            {
                return;
            }

            var own = calls.Grants("status", new JsonObject { ["dir"] = home, ["team"] = child }, options);
            if (own.IsMap && own.Member("present") is { IsTruthy: true })
            {
                return;
            }

            _ = calls.Grants("gate", new JsonObject { ["dir"] = home, ["team"] = child, ["mode"] = mode.AsString() }, options);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the child's grants document is best-effort: the team works without it and the gate mode of the parent still applies
        }
    }

    private static void EnsureChecklist(IHierarchyProgress? progress, string pool, string team, string stream, JsonValue streamRecord)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            _ = progress.EnsureTeam(pool, team, new ProgressStreamInfo("orchestrator", "—", "—", null));
            var tree = streamRecord.Member("worktree") is { IsText: true } w ? w.AsString() : null;
            _ = progress.EnsureStream(
                pool,
                team,
                stream,
                new ProgressStreamInfo(
                    "—",
                    streamRecord.Member("branch") is { IsText: true, } b && b.AsString().Length > 0 ? b.AsString() : "—",
                    tree is { Length: > 0 } ? Path.GetFileName(tree.TrimEnd('/')) : "—",
                    null));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the checklist is a record, not a gate
        }
    }
}
