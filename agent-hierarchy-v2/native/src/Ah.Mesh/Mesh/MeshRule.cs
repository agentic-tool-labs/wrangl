using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>One end of a send: <c>member</c>, <c>orchestrator</c>, <c>external</c>, <c>system</c> or (as a recipient) <c>broadcast</c>.</summary>
public sealed record MeshParty(string Kind, string? Address = null, string? Name = null, string? Team = null, string? Role = null);

/// <summary>A team as the rule sees it: its record, and the hierarchy directory its team file lives in (null when unknown).</summary>
public sealed record MeshTeam(JsonValue Data, string? Dir);

/// <summary>A team's mesh settings: the send policy and the role pairs the daemon links without asking.</summary>
public sealed record MeshSettings(string Policy, JsonArray AutoGrant);

/// <summary>The sender's current task, named in a refusal's text.</summary>
public sealed record MeshTask(string? Id, string? Path);

/// <summary>The facts the rule needs about the mailbox that it cannot read from the team files.</summary>
public interface IMeshFacts
{
    /// <summary>Whether entry <paramref name="entryId"/> is in <paramref name="address"/>'s box and not acked.</summary>
    bool RelayLive(string entryId, string address);

    /// <summary>Whether the sender holds a brief whose sender, or <c>reply_to</c>, is the recipient and is answering it with a report or a note.</summary>
    bool RepliesToBriefer(MeshParty sender, MeshParty recipient, string? msgKind);

    /// <summary>Whether the parent chain of the message file <paramref name="messagePath"/> reaches the task <paramref name="taskId"/>.</summary>
    bool ChainReachesTask(string messagePath, string taskId, string dir, string team);

    /// <summary>Whether the process recorded by <paramref name="pid"/> and <paramref name="pidStart"/> is alive and is the one that was recorded. A source that cannot tell says no.</summary>
    bool ProcessLive(JsonValue pid, JsonValue? pidStart) => false;

    /// <summary>Whether a run-scoped link (one that carries an <c>anchor</c>) still holds: its anchor is the open one, both members are on the team and a request on its slug is open. A source with no run facts says no, so a run-scoped link never covers a message it cannot vouch for.</summary>
    bool RunLinkLive(JsonObject link, string dir, string team, Func<string, MeshTeam?> lookup) => false;

    /// <summary>
    /// What two members of <paramref name="team"/> are assigned to together, or null. A source that cannot read requests knows the team
    /// record only: a work stream both rows name.
    /// </summary>
    SharedAssignment? Assigned(MeshParty sender, MeshParty recipient, string dir, string team, Func<string, MeshTeam?> lookup) =>
        Assignments.Stream(lookup(team)?.Data, sender.Name, recipient.Name) is { } stream ? new SharedAssignment("stream:" + stream, null) : null;
}

/// <summary>One send to decide. <paramref name="Now"/> is epoch milliseconds.</summary>
public sealed record MeshDecideRequest(
    double Now,
    MeshParty Sender,
    MeshParty Recipient,
    Func<string, MeshTeam?> LookupTeam,
    string? Text,
    string? MsgKind,
    IMeshFacts Facts,
    MeshTask? Task = null,
    string? Ref = null,
    bool HoldExempt = false);

/// <summary>
/// The rule's verdict. <paramref name="Rule"/> is the rule's number (<c>3a</c> included). <paramref name="Warn"/> marks a
/// <c>star-warn</c> pass (allowed, code <c>would_refuse</c>, <paramref name="Would"/> the refusal it stands for);
/// <paramref name="Count"/> a linked message the caller counts against <paramref name="Link"/> before delivering.
/// <paramref name="Assigned"/> is the shared assignment that allows the send (rule 9a): with no <paramref name="Link"/> the caller
/// opens the assignment link, on <paramref name="Task"/>, before it counts.
/// </summary>
public sealed record MeshVerdict(
    bool Allow,
    string Rule,
    string? Code = null,
    int? Status = null,
    string? Message = null,
    bool Warn = false,
    string? Would = null,
    bool Count = false,
    bool Mirror = false,
    JsonValue? Link = null,
    JsonValue? Task = null,
    string? Team = null,
    string? Dir = null,
    string? Assigned = null);

/// <summary>
/// The peer mesh's one send rule: members of a team talk to each other through the Orchestrator unless they are linked: by the
/// Orchestrator for one task, or by being assigned together (one work stream, or open requests on one item); Orchestrators talk to
/// each other freely; members never leave their team.
/// </summary>
public interface IMeshRule
{
    /// <summary>How long after a brief is answered a reply to the briefer is still the same conversation.</summary>
    const double ReplyWindowMs = 30 * 60 * 1000;

    /// <summary>The team's mesh settings: no <c>mesh</c> field means <c>star</c>.</summary>
    MeshSettings SettingsOf(JsonValue? teamData);

    /// <summary><c>name@team</c> split in two, or null.</summary>
    AddressParts? SplitAddress(string? address);

    /// <summary>What an address is, given the team files.</summary>
    MeshParty Classify(string? address, Func<string, MeshTeam?> lookupTeam);

    /// <summary>The refusal text for a peer send with no link.</summary>
    string NotLinkedText(string team, string? recipient, MeshTask? task);

    /// <summary>The refusal text of a send a link on <paramref name="task"/> would not carry: <c>link_off_task</c> or <c>link_needs_ref</c>.</summary>
    string LinkRefusalText(string code, string team, string task);

    /// <summary>Decides one send.</summary>
    MeshVerdict Decide(MeshDecideRequest request);
}

internal sealed partial class MeshRule(ILinks links, IAhEnvironment environment) : IMeshRule
{
    private static readonly string[] Policies = ["open", "star-warn", "star"];

    /// <summary>The longest `msg send --text` note a linked member may send without --ref; anything longer goes as a message file.</summary>
    internal const int LinkNoteMaxChars = 280;

    private static readonly string[][] DefaultAutoGrant =
    [
        .. ConfigModule.PeerEligibleBuiltinRolesOfClass("implement").Where(role => role != "qa").SelectMany(role => new[] { new[] { role, "reviewer" }, new[] { "architect", role } }),
    ];

    public MeshSettings SettingsOf(JsonValue? teamData)
    {
        var mesh = teamData is { IsMap: true } d && d.AsObject().Get("mesh") is { IsMap: true } m ? m.AsObject() : null;
        var policy = mesh?.Get("policy") is { IsText: true } p && Policies.Contains(p.AsString(), StringComparer.Ordinal) ? p.AsString() : "star";
        var grant = new JsonArray();
        if (mesh?.Get("auto_grant") is { IsArray: true } list)
        {
            foreach (var pair in list.AsArray())
            {
                if (pair.IsArray && pair.AsArray().Count == 2)
                {
                    grant.Add(pair);
                }
            }
        }
        else
        {
            foreach (var pair in DefaultAutoGrant)
            {
                var row = new JsonArray();
                row.Add(pair[0]);
                row.Add(pair[1]);
                grant.Add(row);
            }
        }

        return new MeshSettings(policy, grant);
    }

    public AddressParts? SplitAddress(string? address) => Mailbox.Split(address);

    public MeshParty Classify(string? address, Func<string, MeshTeam?> lookupTeam)
    {
        var parts = Mailbox.Split(address);
        if (parts is null)
        {
            return new MeshParty("external", address ?? string.Empty);
        }

        var team = lookupTeam(parts.Team);
        if (team is null || !team.Data.IsTruthy)
        {
            return new MeshParty("external", address, parts.Name, parts.Team);
        }

        if (parts.Name == "orchestrator")
        {
            return new MeshParty("orchestrator", address, parts.Name, parts.Team, "orchestrator");
        }

        if (team.Data.IsMap && team.Data.AsObject().Get("members") is { IsArray: true } members)
        {
            foreach (var m in members.AsArray())
            {
                if (m.IsMap && m.AsObject().Get("name") is { IsText: true } n && n.AsString() == parts.Name)
                {
                    var role = m.AsObject().Get("role") is { IsTruthy: true } r ? JsConvert.ToJsString(r) : null;
                    return new MeshParty("member", address, parts.Name, parts.Team, role);
                }
            }
        }

        return new MeshParty("external", address, parts.Name, parts.Team);
    }

    private static readonly string[] DesignRoles = ["architect", "reviewer", "ultra-advisor"];

    private static string? Str(JsonValue? v) => v is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;

    /// <summary>The manager team and stream a lane's child team records as its parent, or null when the team records none.</summary>
    private static (string Manager, string Stream)? ParentOf(MeshDecideRequest p, string team) =>
        p.LookupTeam(team)?.Data.Member("parent") is { IsMap: true } parent && Str(parent.Member("team")) is { } manager && Str(parent.Member("stream")) is { } stream
            ? (manager, stream)
            : null;

    /// <summary>The sub-orchestrator the manager team records for <paramref name="stream"/>, and whether the stream is done.</summary>
    private static (string? SubOrchestrator, bool Done) StreamOf(MeshDecideRequest p, string manager, string stream)
    {
        var record = p.LookupTeam(manager)?.Data.Member("streams") is { IsMap: true } streams ? streams.AsObject().Get(stream) : null;
        return record is { IsMap: true } r ? (Str(r.Member("sub_orchestrator")), Str(r.Member("state")) == "done") : (null, false);
    }

    /// <summary>
    /// The addresses of the lead a member's row names while it is a valid, live lead (<see cref="HierarchyModule.ValidLead"/>): the
    /// lead's own (<c>{so}@{manager}</c>, the stream's sub-orchestrator row in the lane's manager team) and the Orchestrator of the lane's team. Any other lead names nobody.
    /// </summary>
    private static IReadOnlyList<string> LeadAddresses(MeshDecideRequest p, MeshParty member)
    {
        if (member.Kind != "member" || string.IsNullOrEmpty(member.Team) || p.LookupTeam(member.Team) is not { } entry || entry.Data.Member("members") is not { IsArray: true } rows)
        {
            return [];
        }

        if (HierarchyModule.DissolvedParent(name => p.LookupTeam(name)?.Data ?? JsonValue.Null, entry.Data, member.Team) is { } dissolvedInto)
        {
            return [HierarchyModule.OrchestratorAddress(dissolvedInto)];
        }

        var row = rows.AsArray().FirstOrDefault(m => m.IsMap && m.Member("name") is { IsText: true } n && n.AsString() == member.Name);
        if (!row.IsMap || HierarchyModule.ValidLead(member.Team, row, name => p.LookupTeam(name)?.Data ?? JsonValue.Null, p.Facts.ProcessLive) is not { } lead)
        {
            return [];
        }

        return HierarchyModule.LaneManager(lead, name => p.LookupTeam(name)?.Data ?? JsonValue.Null) is { } manager
            ? [$"{lead.Name}@{manager}", HierarchyModule.OrchestratorAddress(lead.Team)]
            : [];
    }

    /// <summary>
    /// Whether <paramref name="designer"/> is an architect, reviewer or ultra-advisor of a team and <paramref name="other"/> is the
    /// sub-orchestrator of a running lane of that team: its row in the team, or the Orchestrator of the lane's child team, which must
    /// record the team and stream as its parent.
    /// </summary>
    private static bool DesignPair(MeshDecideRequest p, MeshParty designer, MeshParty other)
    {
        if (designer.Kind != "member" || designer.Role is null || !DesignRoles.Contains(designer.Role) || string.IsNullOrEmpty(designer.Team) || other.Address is null
            || p.LookupTeam(designer.Team)?.Data.Member("streams") is not { IsMap: true } streams)
        {
            return false;
        }

        var map = streams.AsObject();
        foreach (var stream in map.Keys)
        {
            var (owner, done) = StreamOf(p, designer.Team, stream);
            if (owner is null || done)
            {
                continue;
            }

            var child = HierarchyModule.LaneChildName(designer.Team, stream);
            if (other.Address == $"{owner}@{designer.Team}"
                || (other.Address == HierarchyModule.OrchestratorAddress(child) && ParentOf(p, child) is { } lane && lane.Manager == designer.Team && lane.Stream == stream))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the manager's member row named <paramref name="owner"/> has a built-in role of the orchestrate class.</summary>
    private static bool OwnerIsOrchestrate(MeshDecideRequest p, string manager, string owner) =>
        p.LookupTeam(manager)?.Data.Member("members") is { IsArray: true } rows
        && rows.AsArray().FirstOrDefault(m => m.IsMap && Str(m.Member("name")) == owner) is { IsMap: true } row
        && HierarchyModule.IsOrchestrateRow(row);

    /// <summary>
    /// The lanes a party leads, from the team records: each open stream of the manager whose sub-orchestrator row is an orchestrate-class
    /// member and whose child team records the manager and stream as its parent, led by that member's row or by the Orchestrator of the
    /// child team. Empty for anyone else.
    /// </summary>
    private static List<(string Manager, string Stream, string Child)> LanesLed(MeshDecideRequest p, MeshParty party)
    {
        var led = new List<(string Manager, string Stream, string Child)>();
        if (party.Team is null || party.Name is null)
        {
            return led;
        }

        if (party.Kind == "orchestrator")
        {
            if (ParentOf(p, party.Team) is { } lane && StreamOf(p, lane.Manager, lane.Stream) is { SubOrchestrator: { } owner, Done: false }
                && HierarchyModule.LaneChildName(lane.Manager, lane.Stream) == party.Team && OwnerIsOrchestrate(p, lane.Manager, owner))
            {
                led.Add((lane.Manager, lane.Stream, party.Team));
            }

            return led;
        }

        if (party.Kind != "member" || p.LookupTeam(party.Team)?.Data.Member("streams") is not { IsMap: true } streams)
        {
            return led;
        }

        foreach (var stream in streams.AsObject().Keys)
        {
            var child = HierarchyModule.LaneChildName(party.Team, stream);
            if (StreamOf(p, party.Team, stream) is { SubOrchestrator: { } owner, Done: false } && owner == party.Name && OwnerIsOrchestrate(p, party.Team, owner)
                && ParentOf(p, child) is { } lane && lane.Manager == party.Team && lane.Stream == stream)
            {
                led.Add((party.Team, stream, child));
            }
        }

        return led;
    }

    /// <summary>
    /// Whether the Orchestrator revoked a link of the pair opened for the assignment <paramref name="key"/>: its decision outranks the
    /// default until the pair is assigned to something else. A link the tick ended (<c>assignment_ended</c>) does not count.
    /// </summary>
    private bool RevokedByOrchestrator(string dir, MeshParty sender, MeshParty recipient, string key) =>
        links.RevokedAssignment(links.ReadLinks(dir, sender.Team ?? string.Empty), key, Js(sender.Address), Js(recipient.Address));

    private static JsonValue? Js(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return JsonValue.FromString(text);
    }

    private string MsgCommand() =>
        NodePath.Join(environment.Plugin?.Root ?? string.Empty, "bin", "ah") + " msg";

    public string NotLinkedText(string team, string? recipient, MeshTask? task)
    {
        var to = string.IsNullOrEmpty(recipient) ? "<recipient>" : recipient;
        var id = task is { Id.Length: > 0 } ? task.Id : "<request id>";
        var where = task is { Path.Length: > 0 } ? $" ({task.Path})" : string.Empty;
        return string.Join(
            "\n",
            "ah: direct messages between team members need a link from the Orchestrator.",
            $"Ask for one: {MsgCommand()} link-request --to {to} --task {id} --why \"<reason>\"{where}.",
            $"Or send through orchestrator@{team}.");
    }

    public string LinkRefusalText(string code, string team, string task) =>
        code == "link_off_task" ? OffTaskText(task, HierarchyModule.OrchestratorAddress(team)) : NeedsRefText(HierarchyModule.OrchestratorAddress(team));

    /// <summary>Whether a send is a short <c>msg send --text</c> note, which rides a link with no --ref.</summary>
    internal static bool IsLinkNote(string? msgKind, string? text) => msgKind == "note" && text is { Length: > 0 and <= LinkNoteMaxChars };

    private string NeedsRefText(string orchestrator) =>
        $"ah: a message over a link must carry --ref <message file> that belongs to the linked task: send it with `{MsgCommand()} send --to <peer> --ref <id>` or a msg send --text note of at most {LinkNoteMaxChars} characters, never a bare SendMessage, or send through {orchestrator}.";

    private string AssignedBareText(string? recipient) =>
        $"ah: you and {recipient} are assigned together, so you may message each other directly, through ahd: `{MsgCommand()} send {recipient} --text \"<message>\"` (or --ref <message file>), never a bare SendMessage.";

    private static string OffTaskText(string task, string orchestrator) =>
        $"ah: this entry is not part of task {task}; new work goes through the Orchestrator {orchestrator}.";

    private static string OutsideTeamText(string team) =>
        $"ah: members of a team message only their own team. Ask orchestrator@{team}; the Orchestrators of different teams can talk to each other.";

    private static string ReachViaTheirOrchestrator(string? recipient) =>
        $"ah: reach {recipient} through its own team's Orchestrator; Orchestrators talk to Orchestrators, and members stay inside their team.";

    private static string BroadcastText(string? team) => $"ah: only the Orchestrator of {team} may broadcast to the team (to '*@{team}').";

    private string ExhaustedText(JsonValue link)
    {
        var o = link.AsObject();
        var limits = o.Get("limits") is { IsMap: true } l ? l.AsObject() : throw new InvalidOperationException("a link has no limits");
        if (o.Get("end_reason") is { IsText: true } why && why.AsString() == "cap")
        {
            return $"ah: link {JsConvert.ToJsString(o.Get("id"))} has run its {JsConvert.ToJsString(limits.Get("rounds"))} review rounds. Report to the Orchestrator with your closing report; it decides what happens next.";
        }

        var pending = o.Get("pending_extension") is { IsTruthy: true } p
            ? $" An extension ({JsConvert.ToJsString(p)}) is already pending with the Orchestrator."
            : string.Empty;
        return $"ah: link {JsConvert.ToJsString(o.Get("id"))} has used its {JsConvert.ToJsString(limits.Get("messages"))} messages or its {JsConvert.ToJsString(limits.Get("minutes"))} minutes.{pending} "
            + $"Ask for more: {MsgCommand()} link extend-request {JsConvert.ToJsString(o.Get("id"))} --why \"<reason>\", or report back to the Orchestrator.";
    }

    public MeshVerdict Decide(MeshDecideRequest p)
    {
        var sender = p.Sender;
        var recipient = p.Recipient;
        var team = !string.IsNullOrEmpty(sender.Team) ? sender.Team : recipient.Team;
        var teamEntry = !string.IsNullOrEmpty(sender.Team) ? p.LookupTeam(sender.Team) : null;
        var mesh = SettingsOf(teamEntry?.Data);

        MeshVerdict Allow(string rule) => new(true, rule);

        MeshVerdict Refuse(string rule, string code, int status, string message) =>
            mesh.Policy == "star-warn" && code != "broadcast_orchestrator_only" && code != "link_exhausted"
                ? new MeshVerdict(true, rule, Code: "would_refuse", Warn: true, Would: code, Message: message, Mirror: code == "not_linked")
                : new MeshVerdict(false, rule, Code: code, Status: status, Message: message);

        // an open team's sender, or a send with no member on one end, is today's behaviour
        if (sender.Kind == "system" || recipient.Kind == "system")
        {
            return Allow("2");
        }

        if (sender.Kind == "external")
        {
            return Allow("2");
        }

        if (mesh.Policy == "open")
        {
            return Allow("1");
        }

        var relay = EntryMarkerText.Parse(p.Text).FirstOrDefault(m => m.Address is not null);
        if (relay is not null && !string.IsNullOrEmpty(recipient.Address) && relay.Address == recipient.Address && p.Facts.RelayLive(relay.Ids[0], recipient.Address))
        {
            return Allow("3");
        }

        if (recipient.Kind == "broadcast")
        {
            if (sender.Kind == "orchestrator" && sender.Team == recipient.Team)
            {
                return Allow("8");
            }

            return Refuse("8", "broadcast_orchestrator_only", 403, BroadcastText(recipient.Team));
        }

        if (p.Facts.RepliesToBriefer(sender, recipient, p.MsgKind))
        {
            return Allow("3a");
        }

        if (sender.Kind == "orchestrator" && recipient.Kind == "orchestrator")
        {
            return Allow("4");
        }

        if (sender.Kind == "orchestrator" && recipient.Kind == "member" && recipient.Team == sender.Team)
        {
            return Allow("5");
        }

        if (sender.Kind == "member" && recipient.Kind == "orchestrator" && recipient.Team == sender.Team)
        {
            return Allow("5");
        }

        if (sender.Kind == "member" && recipient.Address is { } toLead && LeadAddresses(p, sender).Contains(toLead))
        {
            return Allow("5");
        }

        if (recipient.Kind == "member" && sender.Kind is "member" or "orchestrator" && sender.Address is { } fromLead && LeadAddresses(p, recipient).Contains(fromLead))
        {
            return Allow("5");
        }

        if (DesignPair(p, sender, recipient) || DesignPair(p, recipient, sender))
        {
            return Allow("5");
        }

        var senderLanes = LanesLed(p, sender);
        if (senderLanes.Count > 0)
        {
            if (LanesLed(p, recipient).Any(to => senderLanes.Any(from => from.Manager == to.Manager && from.Stream != to.Stream)))
            {
                return Allow("5");
            }

            if (recipient.Kind == "member" && senderLanes.Any(led => led.Child == recipient.Team))
            {
                return Allow("5");
            }
        }

        if (sender.Kind == "member" && recipient.Address is not null && LanesLed(p, recipient).Any(led => led.Child == sender.Team))
        {
            return Allow("5");
        }

        if (sender.Kind == "member" && (recipient.Team != sender.Team || recipient.Kind == "external"))
        {
            return Refuse("6", "outside_team", 403, OutsideTeamText(sender.Team ?? string.Empty));
        }

        if (sender.Kind == "orchestrator" && recipient.Kind == "member" && recipient.Team != sender.Team)
        {
            // a quit or stop briefs the whole tree from the top, whether or not the lane's lead is alive; the mark never comes from outside the process
            if (p.HoldExempt && LanesLed(p, recipient with { Kind = "orchestrator" }).Any(lane => lane.Manager == sender.Team))
            {
                return Allow("7");
            }

            return Refuse("7", "outside_team", 403, ReachViaTheirOrchestrator(recipient.Address));
        }

        if (sender.Kind == "orchestrator")
        {
            return Allow("7");
        }

        var dir = teamEntry?.Dir;
        if (!string.IsNullOrEmpty(dir))
        {
            // Being assigned together is the permission (rule 9a); read once, and only when a link or the refusal needs it.
            SharedAssignment? shared = null;
            var sharedRead = false;
            SharedAssignment? Shared()
            {
                if (!sharedRead)
                {
                    sharedRead = true;
                    shared = sender.Kind == "member" && recipient.Kind == "member" && sender.Role != "ultra-advisor" && recipient.Role != "ultra-advisor"
                        ? p.Facts.Assigned(sender, recipient, dir, sender.Team ?? string.Empty, p.LookupTeam) : null;
                }

                return shared;
            }

            // what ahd does not carry never rides a link: a bare SendMessage, and a paste into a pane (roster deliver)
            var bare = p.MsgKind is "sendmessage" or "deliver";

            // whether the send can go by the pair's assignment although no assignment link is open yet: not a bare one, and not
            // an assignment whose link the Orchestrator revoked
            bool OpensByAssignment() => !bare && Shared() is { } open && !RevokedByOrchestrator(dir, sender, recipient, open.Key);
            var covering = new JsonArray();
            foreach (var l in links.ActiveCovering(dir, sender.Team ?? string.Empty, Js(sender.Address), Js(recipient.Address), p.Now))
            {
                var anchored = l.AsObject().Get("anchor") is { IsText: true };
                var byAssignment = l.AsObject().Get("assigned") is { IsText: true };
                if ((!anchored || p.Facts.RunLinkLive(l.AsObject(), dir, sender.Team ?? string.Empty, p.LookupTeam)) && (!byAssignment || Shared() is not null))
                {
                    covering.Add(l);
                }
            }

            if (covering.Count > 0)
            {
                var orchestrator = HierarchyModule.OrchestratorAddress(sender.Team);

                // A requested link keeps its own rules and its count (a review loop's rounds); the pair's assignment link takes what
                // those rules would refuse: any message, no ref, no task to stay on.
                var requested = covering.Where(l => l.AsObject().Get("assigned") is not { IsText: true }).ToList();
                var assigned = bare ? (JsonValue?)null : covering.Cast<JsonValue?>().FirstOrDefault(l => l!.Value.AsObject().Get("assigned") is { IsText: true });
                MeshVerdict OnAssignment(JsonObject link) =>
                    new(true, "9a", Count: true, Mirror: true, Link: link.Get("id"), Task: link.Get("task"), Team: sender.Team, Dir: dir, Assigned: JsConvert.ToJsString(link.Get("assigned")));

                if (string.IsNullOrEmpty(p.Ref))
                {
                    // A short `msg send --text` note (kind "note"; a bare SendMessage is "sendmessage"), at most LinkNoteMaxChars UTF-16 units, rides the link on its first task.
                    if (requested.Count > 0 && IsLinkNote(p.MsgKind, p.Text))
                    {
                        var linkedTo = requested[0].AsObject();
                        return new MeshVerdict(true, "9", Count: true, Mirror: true, Link: linkedTo.Get("id"), Task: linkedTo.Get("task"), Team: sender.Team, Dir: dir);
                    }

                    if (assigned is { } any)
                    {
                        return OnAssignment(any.AsObject());
                    }

                    if (requested.Count > 0 && !OpensByAssignment())
                    {
                        return Refuse("9", "link_needs_ref", 403, NeedsRefText(orchestrator));
                    }
                }
                else
                {
                    var onTasks = requested.Where(l => l.AsObject().Get("task") is { IsText: true } t && p.Facts.ChainReachesTask(p.Ref, t.AsString(), dir, sender.Team ?? string.Empty)).ToList();
                    if (onTasks.Count > 0)
                    {
                        var first = onTasks[0].AsObject();
                        return new MeshVerdict(true, "9", Count: true, Mirror: true, Link: first.Get("id"), Task: first.Get("task"), Team: sender.Team, Dir: dir);
                    }

                    if (assigned is { } off)
                    {
                        return OnAssignment(off.AsObject());
                    }

                    if (requested.Count > 0 && !OpensByAssignment())
                    {
                        var linked = requested[0].AsObject().Get("task") is { IsText: true } lt ? lt.AsString() : "<task>";
                        return Refuse("9", "link_off_task", 403, OffTaskText(linked, orchestrator));
                    }
                }
            }

            // No link carries it: the assignment does, opening its link if there is none yet.
            if (Shared() is { } together && !RevokedByOrchestrator(dir, sender, recipient, together.Key))
            {
                return bare
                    ? Refuse("9a", "link_needs_ref", 403, AssignedBareText(recipient.Address))
                    : new MeshVerdict(true, "9a", Count: true, Mirror: true, Task: Js(together.Task ?? p.Task?.Id), Team: sender.Team, Dir: dir, Assigned: together.Key);
            }

            if (links.RecentlyCapped(dir, sender.Team ?? string.Empty, Js(sender.Address), Js(recipient.Address), p.Now) is { } capped)
            {
                return Refuse("9", "link_exhausted", 403, ExhaustedText(capped));
            }
        }

        return Refuse("10", "not_linked", 403, NotLinkedText(team ?? string.Empty, recipient.Address, p.Task));
    }
}
