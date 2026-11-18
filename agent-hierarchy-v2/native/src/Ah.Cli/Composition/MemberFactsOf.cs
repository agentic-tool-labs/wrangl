using Ah.Hierarchy;
using Ah.Watch.Model;

namespace Ah.Cli.Composition;

/// <summary>
/// The evidence the processes lane cannot read for itself, by the same reading the stall watch uses: <c>ForMember</c>
/// and the herdr answer through <c>JudgeMember</c>. Herdr is asked only for a member with a pane whose registration is not live.
/// </summary>
internal static class MemberFactsOf
{
    // entriesOf: the held checklist entries of (directory, progress team), folded again only when the plan counter moves; null folds each poll
    // reuse: the herdr answers kept across polls for members that are not live (ah2-52 item 3); null asks herdr each time
    internal static MemberFacts Dir(ServiceGraph g, string dir, Func<string, string, IReadOnlyList<ChecklistEntry>>? entriesOf = null, StoppedProbeReuse? reuse = null)
    {
        var asked = new HashSet<(string Team, string Member, string Pane)>();
        var heartbeats = new List<string>();
        var panes = new Dictionary<string, string>(StringComparer.Ordinal);
        var starting = new List<string>();
        var queueWaiting = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var team in g.Roster.ListTeamNames(dir))
        {
            // ponytail: reads each team's slot files once per poll; the checklist fold is held per plan counter (b459 step 11)
            if (QueueRows.Of(g, dir, team, g.Io.Clock.UtcNow.ToUnixTimeMilliseconds(), entriesOf).Count(static r => r.Eligible.Count == 0) is > 0 and var unclaimed)
            {
                queueWaiting[team] = unclaimed;
            }

            var teamRecord = g.Roster.ReadTeamRecord(dir, team);
            if (teamRecord.Member("members") is not { IsArray: true } rows)
            {
                continue;
            }

            // the team's record as the reuse compares it: the store's teams counter, or the record itself where there is none; read at the team's first probe
            string? stamp = null;
            string TeamStamp() => stamp ??= g.Roster.TeamsChangeToken()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Ah.Core.Json.JsonText.Stringify(teamRecord, 0);

            // with the reuse on, one answer that a herdr server is not running stands for this team's other members on that server in the pass
            var serverDown = new Dictionary<string, Ah.Launch.HerdrAgentState>(StringComparer.Ordinal);

            foreach (var row in rows.AsArray())
            {
                if (row.Member("name") is not { IsText: true } named)
                {
                    continue;
                }

                var member = named.AsString();
                if (global::Ah.Roster.RosterModule.StartingAgeSeconds(row, g.Io.Clock.UtcNow) is not null)
                {
                    starting.Add(member);
                }

                var registration = g.Hierarchy.Core.Liveness?.ForMember(member, team);
                if (registration is { Live: true })
                {
                    if (g.Mailbox.ReadRegistration(registration.SessionId)?.Get("pid") is not { IsNumber: true })
                    {
                        heartbeats.Add(registration.SessionId);
                    }

                    continue;
                }

                var pane = row.Member("transport_id") is { IsText: true } transport ? global::Ah.Abstractions.HeadlessTransport.PaneOf(transport.AsString()) : null;
                // ah2-156: the server this member lives on, by its registration, its row and its team; ahd's own env is never the fallback
                string Flavour() => Ah.Core.Mux.HerdrFlavour.Resolve(
                    registration is null ? null : g.Mailbox.ReadRegistration(registration.SessionId), teamRecord, null, row) ?? Ah.Core.Mux.HerdrFlavour.Tui;

                PaneProbe Ask()
                {
                    var state = g.Launch.HerdrFor(Flavour()).AgentState(member);
                    return new PaneProbe(state.Indeterminate, state.AgentStatus, state.AgentType, state.Why);
                }

                (PaneProbe Answer, bool Holdable) AskOnce()
                {
                    var flavour = Flavour();
                    if (!serverDown.TryGetValue(flavour, out var state))
                    {
                        state = g.Launch.HerdrFor(flavour).AgentState(member);
                        if (state.ServerDown)
                        {
                            serverDown[flavour] = state;
                        }
                    }

                    return (new PaneProbe(state.Indeterminate, state.AgentStatus, state.AgentType, state.Why), !state.Indeterminate || state.ServerDown);
                }

                Func<PaneProbe> probe = Ask;
                if (reuse is { } held && pane is { Length: > 0 } paneId)
                {
                    _ = asked.Add((team, member, paneId));
                    probe = () => held.Probe(dir, team, member, paneId, registration, TeamStamp, AskOnce);
                }

                var verdict = HierarchyModule.JudgeMember(registration, pane, probe);
                if (verdict.Status == Verdict.Live)
                {
                    panes[member] = verdict.Why;
                }
            }
        }

        reuse?.Keep(dir, asked);
        return new MemberFacts(heartbeats, panes, starting, queueWaiting);
    }
}
