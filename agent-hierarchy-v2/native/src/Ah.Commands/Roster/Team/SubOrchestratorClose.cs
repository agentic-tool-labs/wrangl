using Ah.Hierarchy;

namespace Ah.Commands;

/// <summary>
/// What a sub-orchestrator needs before it closes live sessions of its child team: a set to close, and the managing orchestrator's green
/// light for exactly this plan. The managing orchestrator itself is unaffected.
/// </summary>
internal static class SubOrchestratorClose
{
    /// <summary>
    /// The sub-orchestrator the caller is for the target team, from registered state: a carved marked shell whose marker agrees, or any
    /// caller the walk proves owns the target as the child of a stream whose orchestrate row is registered to it. The marker never
    /// decides whether a restriction holds; it only ever refuses.
    /// </summary>
    internal static SoCarve? SoOf(RosterContext c)
    {
        var proved = c.Proved();
        if (proved.Carve is { Disagreement: null } carve)
        {
            return carve;
        }

        return c.RegisteredSoOfTarget();
    }

    /// <summary>Whether the caller is a sub-orchestrator for the target team, and so held to the sub-orchestrator's close rules.</summary>
    internal static bool Applies(RosterContext c) => SoOf(c) is not null;

    /// <summary>Refuses <c>--ignore-release</c> and <c>--kill</c> on a close by a sub-orchestrator, whatever the grant says.</summary>
    internal static void RefuseForcing(RosterContext c, string verb, bool force = false)
    {
        if (verb is "dismiss" or "disband" && (c.Opts.Has("ignore-release") || c.Opts.IsTrue("kill")) && (force || Applies(c)))
        {
            throw new OwnerRefusal(
                "close-flag-refused",
                "member-marker",
                $"ah roster {verb}: a sub-orchestrator may not close with --ignore-release or --kill; nothing was changed.",
                "owner proof: --ignore-release and --kill are refused for a sub-orchestrator, whatever the grant says.");
        }
    }

    /// <summary>Refuses a sub-orchestrator's close that has nothing to close: the record goes with <c>untrack</c>.</summary>
    internal static void RequireSomethingToClose(RosterContext c, string verb, int closable)
    {
        if (Applies(c) && closable == 0)
        {
            throw Refusal(verb, "close-empty-set", $"ah roster {verb}: nothing to close; use untrack; nothing was changed.", "owner proof: a green-light close needs at least one member to close.");
        }
    }

    /// <summary>
    /// Refuses the close unless the managing team holds a matching, unexpired grant from its current orchestrator. A dismiss of members
    /// that <paramref name="released"/> finds not held needs no grant: only a member working now is held, and work left behind stays in its tree.
    /// </summary>
    internal static void RequireGreenLight(RosterContext c, string verb, Func<bool>? released = null)
    {
        if (!Applies(c) || (verb == "dismiss" && released?.Invoke() == true))
        {
            return;
        }

        var carve = SoOf(c)!;
        var manager = SubOrchestratorCarve.ReadTeam(c, carve.Team);
        var child = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        var childName = c.TeamFile ?? string.Empty;
        var stream = child.Member("parent")?.Member("stream") is { IsText: true } ws ? ws.AsString() : string.Empty;
        var token = c.Opts.Text("plan-token") ?? string.Empty;
        var verdict = HierarchyModule.EvaluateCloseGreenLight(manager, stream, verb, childName, token, c.Services.Clock.UtcNow);
        switch (verdict.Kind)
        {
            case GreenLightKind.Granted:
                return;
            case GreenLightKind.Expired:
                throw Refusal(
                    verb,
                    "close-green-light-expired",
                    $"ah roster {verb}: the green light for token {token} expired at {verdict.ExpiredAt}; ask again; nothing was changed.",
                    "owner proof: the grant for this plan has expired.");
            case GreenLightKind.ReplacedOrchestrator:
                throw Refusal(
                    verb,
                    "close-green-light-replaced",
                    $"ah roster {verb}: the green light was given by a previous orchestrator of {carve.Team}; ask again; nothing was changed.",
                    "owner proof: the grant was given by an orchestrator that is no longer the one of the managing team.");
            default:
                throw Refusal(
                    verb,
                    "close-no-green-light",
                    $"ah roster {verb}: no green light from {carve.Team}'s orchestrator for this close plan (token {token}); ask it to run: ah roster close-approve --team {childName} --verb {verb} …; nothing was changed.",
                    "owner proof: no grant covers this verb, team and plan token.");
        }
    }

    private static OwnerRefusal Refusal(string verb, string code, string first, string why) => new(code, "member-marker", first, why);
}
