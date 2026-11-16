using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// Registers a team's Orchestrator and its members with the mailbox as soon as the team file names them, so the daemon knows
/// them without a manual <c>checkin</c>. A session that has not started yet is skipped. A registration that fails is reported on
/// standard error and in the verb's output as <c>registration_warnings</c>; the verbs this runs in do not fail on it, since the team is
/// written by then.
/// </summary>
internal static class TeamRegistration
{
    /// <summary>
    /// Registers the calling Orchestrator (every team it owns, see the mailbox's caller resolution) and each member of
    /// <paramref name="team"/> whose pane has a live <c>up</c> roster row. Returns the addresses the registrations hold.
    /// </summary>
    internal static IReadOnlyList<string> RegisterTeam(RosterContext c, string dir, JsonValue team)
    {
        var teamName = c.TeamFile;
        if (string.IsNullOrEmpty(teamName) || c.Services.Callers is not { } callers)
        {
            return [];
        }

        var written = new List<string>();

        void Add(IReadOnlyList<string> addresses)
        {
            foreach (var address in addresses)
            {
                if (!written.Contains(address))
                {
                    written.Add(address);
                }
            }
        }

        // A session whose live registration already holds its address here is not claimed again: each claim resolves the
        // caller's owned teams across every visible repo, many state reads per session (b279 follow-up, atp-914).
        IReadOnlyList<string>? AlreadyHeld(string? session, int pid, string? pane, string address) =>
            Attempt(c, address, () => callers.HeldAddresses(session, pid, pane), null) is { } held && held.Contains(address) ? held : null;

        var pid = c.OwnerPid();
        if (RosterContext.IsInteger(pid) && c.PidAlive(pid))
        {
            var recorded = team.Member("orchestrator") is { IsMap: true } held && held.Member("pid") is { IsNumber: true } heldPid
                && heldPid.AsNumber() == pid && held.Member("session_id") is { IsText: true } heldSession && heldSession.AsString().Length > 0
                ? heldSession.AsString()
                : null;
            var session = recorded ?? c.Opts.Text("session") ?? c.EnvText("CLAUDE_CODE_SESSION_ID");
            var pane = c.SessionPaneId(null);
            Add(AlreadyHeld(session, (int)pid, pane, $"orchestrator@{teamName}")
                ?? Attempt(c, $"the Orchestrator of {teamName}", () => callers.Register(c.Cwd, session, (int)pid, pane, null, false, Ah.Core.Mux.HerdrFlavour.SessionFlavour(c.EnvText))));
        }

        if (team.Member("members") is not { IsArray: true } members)
        {
            return written;
        }

        var rows = Attempt(c, $"the roster of {teamName}", () => c.Services.Hierarchy.Roster.LatestRoster(dir).ToList(), []);
        foreach (var member in members.AsArray())
        {
            if (member.Member("transport_id") is not { IsText: true } transport || transport.AsString().Length == 0)
            {
                continue;
            }

            if (NewestLiveRow(c, rows, transport.AsString()) is not { } row)
            {
                continue;
            }

            var session = row.Member("session_id") is { IsText: true } sid && sid.AsString().Length > 0 ? sid.AsString() : null;
            var rowPid = (int)row.Member("pid")!.Value.AsNumber();
            var memberAddress = member.Member("name") is { IsText: true } memberName ? $"{memberName.AsString()}@{teamName}" : null;
            Add((memberAddress is null ? null : AlreadyHeld(session, rowPid, transport.AsString(), memberAddress))
                ?? Attempt(c, $"the member on {transport.AsString()}", () => callers.Register(c.Cwd, session, rowPid, transport.AsString(), teamName, false)));
        }

        return written;
    }

    /// <summary>The most recent <c>up</c> roster row of a pane whose process is alive, or null.</summary>
    private static JsonValue? NewestLiveRow(RosterContext c, List<JsonValue> rows, string pane)
    {
        JsonValue? best = null;
        var bestTs = string.Empty;
        foreach (var row in rows)
        {
            if (row.Member("status") is not { IsText: true } status || status.AsString() != "up"
                || row.Member("pane_id") is not { IsText: true } rowPane || rowPane.AsString() != pane
                || row.Member("pid") is not { IsNumber: true } pid || !RosterContext.IsInteger(pid.AsNumber()) || !c.PidAlive(pid.AsNumber()))
            {
                continue;
            }

            var ts = row.Member("ts") is { IsText: true } t ? t.AsString() : string.Empty;
            if (best is null || string.CompareOrdinal(ts, bestTs) >= 0)
            {
                best = row;
                bestTs = ts;
            }
        }

        return best;
    }

    private static T Attempt<T>(RosterContext c, string what, Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var text = $"could not register {what}: {(string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message)}";
            c.RegistrationWarnings.Add(text);
            c.Warn(text);
            return fallback;
        }
    }

    private static IReadOnlyList<string> Attempt(RosterContext c, string what, Func<IReadOnlyList<string>> action) => Attempt(c, what, action, []);

    /// <summary>Adds <c>registered</c> to a verb's output when the step wrote any address, and <c>registration_warnings</c> when a registration failed.</summary>
    internal static void Report(JsonObject output, IReadOnlyList<string> addresses, RosterContext c)
    {
        if (c.RegistrationWarnings.Count > 0)
        {
            var warnings = new JsonArray();
            warnings.AddRange(c.RegistrationWarnings.Select(w => (JsonValue)w));
            output["registration_warnings"] = warnings;
        }

        if (addresses.Count == 0)
        {
            return;
        }

        var list = new JsonArray();
        list.AddRange(addresses.Select(a => (JsonValue)a));
        output["registered"] = list;
    }
}
