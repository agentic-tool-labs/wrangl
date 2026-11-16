using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// The daemon's relaunch of one member, in process, resuming a session it names: the launch path of <c>resume</c> for one member
/// without a proved Orchestrator session above it and without any checkpoint record. It never changes the team's orchestrator, its
/// ownership or any other member, and it is reachable only from code that composes it; no command line reaches it.
/// </summary>
public interface IMemberRelaunch
{
    /// <summary>
    /// Relaunches <paramref name="member"/> of <paramref name="team"/> in the hierarchy directory <paramref name="hierarchyDir"/> of the
    /// project at <paramref name="projectRoot"/>, resuming <paramref name="sessionId"/>, in the pane its row records when that pane is
    /// still there. Null when it was relaunched, else why it was not.
    /// </summary>
    string? Relaunch(string hierarchyDir, string projectRoot, string team, string member, string sessionId);

    /// <summary>
    /// The same launch as <see cref="Relaunch"/> run as a dry-run for a member that is still live, as if it were already stopped: every
    /// refusal the launch makes before it starts anything runs, and nothing is started or written. Null when the launch would go ahead,
    /// else why it would be refused.
    /// </summary>
    string? Preflight(string hierarchyDir, string projectRoot, string team, string member, string sessionId);

    /// <summary>Sends <paramref name="member"/>, and only it, its resume brief from the team's newest checkpoint. Null when sent or there is nothing to send, else why not.</summary>
    string? ReBrief(string hierarchyDir, string projectRoot, string team, string member);
}

internal sealed class DaemonMemberRelaunch(IRosterCliServices services) : IMemberRelaunch
{
    public string? Relaunch(string hierarchyDir, string projectRoot, string team, string member, string sessionId) =>
        Run(hierarchyDir, projectRoot, team, member, sessionId, preflight: false);

    public string? Preflight(string hierarchyDir, string projectRoot, string team, string member, string sessionId) =>
        Run(hierarchyDir, projectRoot, team, member, sessionId, preflight: true);

    public string? ReBrief(string hierarchyDir, string projectRoot, string team, string member)
    {
        var context = new RosterContext(services, RosterArgs.Parse(["--team", team]), "resume", projectRoot, TextWriter.Null, TextWriter.Null)
        {
            DaemonPrincipal = true,
            TeamArg = team,
            TeamFile = team,
        };
        context.PinDir(hierarchyDir);
        try
        {
            // this is the daemon: `ah msg send` must go to the mailbox files, its daemon client refuses to call itself
            using var direct = Ah.Mesh.MeshModule.BeginHoldExempt();
            return ResumeCheckpoint.ReBriefMember(context, member);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return string.IsNullOrWhiteSpace(e.Message) ? "the re-brief failed with no reason" : e.Message;
        }
    }

    private string? Run(string hierarchyDir, string projectRoot, string team, string member, string sessionId, bool preflight)
    {
        ArgumentException.ThrowIfNullOrEmpty(hierarchyDir);
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentException.ThrowIfNullOrEmpty(team);
        ArgumentException.ThrowIfNullOrEmpty(member);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var opts = RosterArgs.Parse(preflight ? ["--team", team, "--dry-run"] : ["--team", team]);
        var context = new RosterContext(services, opts, "resume", projectRoot, TextWriter.Null, TextWriter.Null)
        {
            DaemonPrincipal = true,
            PreflightStop = preflight,
            TeamArg = team,
            TeamFile = team,
        };
        context.PinDir(hierarchyDir);
        try
        {
            return RelaunchOne(context, hierarchyDir, team, member, sessionId);
        }
        catch (RosterFailure failure)
        {
            return string.IsNullOrWhiteSpace(failure.Message) ? "the relaunch was refused with no reason" : failure.Message;
        }
    }

    private string? RelaunchOne(RosterContext c, string hierarchyDir, string team, string member, string sessionId)
    {
        var launch = services.Launch ?? throw new RosterFailure("this build has no launch services");
        var dir = c.Dir;
        var doc = launch.Snapshots.BuildSnapshot(dir, team, services.Clock.UtcNow);
        if (doc is null || doc.Get("members") is not { IsArray: true } list)
        {
            return $"team {team} has no record to relaunch {member} from";
        }

        if (list.AsArray().FirstOrDefault(m => m.Member("name") is { IsText: true } n && n.AsString() == member) is not { IsMap: true } entry)
        {
            return $"{member} is not in the record of team {team}";
        }

        entry.AsObject()["session_id"] = sessionId;
        var row = services.Roster.TeamMemberByName(dir, member, team);
        if (row.IsMap && global::Ah.Abstractions.HeadlessTransport.IsId(MemberPlanning.Text(row.Member("transport_id"))))
        {
            return Ah.Watch.WatchModule.RecoveryRefusalPrefix + $"{member} runs headless, one claude -p turn with no pane; ahd does not relaunch it. Resume it with spawn-one --resume if its work is not done";
        }

        var teamRecord = services.Roster.ReadTeamRecord(dir, team);
        var transport = teamRecord is { IsMap: true } record ? MemberPlanning.Text(record.Member("transport")) : null;
        if (transport is not ("herdr" or "tmux"))
        {
            return Ah.Watch.WatchModule.RecoveryPaneUnknownPrefix + $"team {team} records transport {transport ?? "none"}; ahd relaunches only into herdr or tmux panes; resume it with ah roster team resume";
        }

        c.LaunchTransport = transport;
        string? pane = null;
        if (row.IsMap &&MemberPlanning.Text(row.Member("transport_id")) is { Length: > 0 } recorded)
        {
            var paneState = launch.PaneStateFor(teamRecord);
            paneState?.Refresh();
            var reading = paneState?.Pane(transport, recorded) ?? new PaneReading(PaneKnowledge.Unknown, null, "this build has no pane reader");
            switch (reading.State)
            {
                case PaneKnowledge.Present:
                    pane = transport == "herdr" ? recorded : null;
                    break;
                case PaneKnowledge.Unknown:
                    return Ah.Watch.WatchModule.RecoveryPaneUnknownPrefix + (string.IsNullOrWhiteSpace(reading.Reason) ? "the reading gave no reason" : reading.Reason);
            }
        }

        var results = ResumeVerb.ResumeMembers(c, JsonValue.FromObject(doc), name => name == member, _ => pane, _ => sessionId);
        var result = results.FirstOrDefault(r => r.Get("name") is { IsText: true } n && n.AsString() == member);
        var status = result?.Get("status") is { IsText: true } s ? s.AsString() : "missing";
        if (status == "resumed")
        {
            return null;
        }

        var text = result?.Get("why") is { IsText: true } w && !string.IsNullOrWhiteSpace(w.AsString()) ? w.AsString() : $"status {status}, no reason recorded";
        return result?.Get("refused") is { IsText: true } refused && refused.AsString() != Ah.Roster.RosterModule.HoldRefusalCode
            ? Ah.Watch.WatchModule.RecoveryRefusalPrefix + text
            : text;
    }
}
