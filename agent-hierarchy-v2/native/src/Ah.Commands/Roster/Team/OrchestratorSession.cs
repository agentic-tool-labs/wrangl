using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// Which session a team's Orchestrator is, as far as a process can prove it. A session id counts only when something tied to
/// the live process names it: a bound registration, or a session-pids row written since the process started. An id the caller
/// merely states counts only when one of those holds the same id for that process.
/// </summary>
internal static class OrchestratorSession
{
    internal const string UnknownWarning =
        "orchestrator session unknown: the mesh gate will not treat this session as the team's Orchestrator until it registers; run ah roster checkin --team <team>";

    /// <summary>The process-tied session id of the live process <paramref name="pid"/>, or null when none is proven.</summary>
    internal static string? ProcessTied(RosterContext c, double pid)
    {
        if (!RosterContext.IsInteger(pid) || !c.PidAlive(pid) || c.Services.Callers is not { } callers)
        {
            return null;
        }

        var p = (int)pid;
        // every proof carries the instant it was made, and the newest one wins: after /clear the same process holds the old
        // session's registration and the new session's row, and only the instant tells them apart
        var proofs = callers.BoundSessions(p).Select(b => (b.SessionId, b.At)).ToList();
        if (callers.ProcessStart(p) is { } started)
        {
            // a cross-repo Orchestrator's hooks write their rows in the directory of their own cwd, which a registration
            // of the process records; the row itself, not the registration, is the proof
            var dirs = new List<string?> { c.Dir, c.MainDir };
            foreach (var cwd in callers.RegistrationCwds(p))
            {
                dirs.Add(c.Services.Config.HierarchyDir(cwd));
                dirs.Add(c.Services.Config.MainHierarchyDir(cwd));
            }

            foreach (var dir in dirs.Where(d => !string.IsNullOrEmpty(d)).DistinctBy(d => RealOf(c, d!), StringComparer.Ordinal))
            {
                proofs.AddRange(c.Services.Hierarchy.Status.SessionsOfPid(dir!, p, started).Select(r => (r.SessionId, r.At)));
            }
        }

        var claimed = c.Opts.Text("session") ?? c.EnvText("CLAUDE_CODE_SESSION_ID");
        if (!string.IsNullOrEmpty(claimed))
        {
            return proofs.Any(x => x.SessionId == claimed) ? claimed : null;
        }

        return proofs.Count == 0 ? null : proofs.MaxBy(x => x.At).SessionId;
    }

    private static string RealOf(RosterContext c, string path)
    {
        try
        {
            return c.Services.Files.RealPath(path) is { Length: > 0 } real ? real : path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>
    /// Records the caller's session on <paramref name="team"/> when its Orchestrator pid is the pid this caller claims and a
    /// process-tied session is proven for it. A team owned by another pid is left alone. When nothing is proven the caller is
    /// told once, through <see cref="Warn"/>.
    /// </summary>
    internal static void Backfill(RosterContext c, JsonObject team)
    {
        var pid = c.OwnerPid();
        if (team.Get("orchestrator") is not { IsMap: true } held
            || held.Member("pid") is not { IsNumber: true } owner || !RosterContext.IsInteger(pid) || owner.AsNumber() != pid)
        {
            return;
        }

        if (ProcessTied(c, pid) is not { } session)
        {
            c.OrchestratorSessionUnknown = true;
            return;
        }

        var next = held.AsObject().Clone();
        next["session_id"] = session;
        team["orchestrator"] = next;
    }

    /// <summary>
    /// Repairs the Orchestrator record of the teams this process and its process-tied session own, before a verb reads them: a
    /// team left holding the session before <c>/clear</c>, or the pid before <c>--resume</c>. Nothing is written without a proven
    /// session, and a dry run writes nothing.
    /// </summary>
    internal static void Rebind(RosterContext c) => Rebind(c, c.OwnerPid());

    /// <summary>Rebinds for the process <paramref name="pid"/> the caller has already proved, not the one the environment names.</summary>
    internal static void Rebind(RosterContext c, double pid)
    {
        if (c.Opts.IsTrue("dry-run") || c.Services.Callers is not { } callers)
        {
            return;
        }

        if (RosterContext.IsInteger(pid) && ProcessTied(c, pid) is { } session)
        {
            callers.RebindTeams(c.Cwd, session, (int)pid);
        }
    }

    /// <summary>
    /// Whether a team record's <c>orchestrator</c> carries any claim on the team. Unclaimed is an absent or null orchestrator, or an
    /// object with no non-empty <c>session_id</c> and a <c>pid</c> that is absent, null or 0; anything else is claimed, a malformed
    /// session or pid included, so a record that cannot be read is judged by its verdict (indeterminate) and not taken unchecked.
    /// </summary>
    internal static bool IsClaimed(JsonValue? orchestrator) => RosterModule.OrchestratorIsClaimed(orchestrator);

    /// <summary>The start time to record beside <paramref name="pid"/> in an Orchestrator record, or null when it cannot be read.</summary>
    internal static string? StartOf(RosterContext c, double pid) =>
        RosterContext.IsInteger(pid) && c.Services.Callers is { } callers ? callers.ProcessStartText((int)pid) : null;

    /// <summary>Adds the unknown-session warning to <paramref name="output"/> when a step could not prove the session.</summary>
    internal static void Warn(RosterContext c, JsonObject output)
    {
        if (!c.OrchestratorSessionUnknown)
        {
            return;
        }

        var warnings = output.Get("warnings") is { IsArray: true } existing ? existing.AsArray() : [];
        warnings.Add(UnknownWarning);
        output["warnings"] = warnings;
    }
}
