using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Roster;

namespace Ah.Mesh;

/// <summary>
/// One SendMessage (<paramref name="To"/> a name, a <c>uds:&lt;pid&gt;.sock</c> address, or <c>*</c>) or one <c>deliver</c>
/// (<paramref name="To"/> the pane member's name) from the session <paramref name="SessionId"/>. <paramref name="NowMs"/> defaults to the clock.
/// <paramref name="CallerPid"/> is the asking process (the hook's harness, or the CLI itself): the session's
/// <c>orchestrator@&lt;team&gt;</c> addresses count only for teams that process provably owns, and with no pid, for none.
/// </summary>
public sealed record MeshGateRequest(RequestContext Context, string? SessionId, string? To, string Text = "", string MsgKind = "sendmessage", double? NowMs = null, int? CallerPid = null);

/// <summary>
/// The gate's answer. <paramref name="After"/> is the step that counts and mirrors an allowed linked send (or only mirrors a warning); it
/// throws an <see cref="IOException"/> carrying the refusal text when the link ended before the message was counted or its record could not be written.
/// </summary>
public sealed record MeshGateVerdict(MeshVerdict Verdict, Action? After);

/// <summary>
/// The peer mesh's check for the two senders that are not the mailbox: the SendMessage gate and <c>ah roster deliver</c>. A caller whose
/// session has no registration is <c>user</c>, and so is a recipient that no team file names: the rule does not apply to either.
/// </summary>
public interface IMeshGate
{
    /// <summary>
    /// The rule's verdict for the request, or a successful outcome with a null value when the rule does not apply. A recipient that resolves
    /// to several sessions gets the most restrictive verdict. A session with several addresses and no way to choose one is a failure.
    /// </summary>
    MailboxOutcome<MeshGateVerdict> Check(MeshGateRequest request);

    /// <summary>
    /// The <c>[ah-link …]</c> line for a SendMessage that reached session <paramref name="sessionId"/> from the socket <paramref name="from"/>,
    /// when that socket is a member of the same team holding an active link to this session's member; null otherwise.
    /// </summary>
    string? LinkEnvelopeFor(RequestContext context, string? sessionId, string? from, double? nowMs = null);

    /// <summary>
    /// A member left the team file at <paramref name="teamPath"/> (or, with <paramref name="names"/> null, the whole team went): ends every
    /// active link it was on. A failed revoke leaves the link to end lazily.
    /// </summary>
    void RevokeLinksFor(string teamPath, IReadOnlyList<string>? names);
}

internal sealed partial class MeshGate(
    Mailbox mailbox,
    MeshSend send,
    IMeshRule rule,
    ILinks links,
    HierarchyServices hierarchy,
    IClock clock,
    IMailCore core) : IMeshGate
{
    [GeneratedRegex(@"\Auds:.*/([0-9]+)\.sock\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex UdsRe();

    private static string Js(JsonValue? value) => JsConvert.ToJsString(value);

    private static long? UdsPid(string to) => UdsRe().Match(to) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var pid) && pid != 0 ? pid : null;

    private static bool SamePid(JsonValue? value, long pid) => value is { IsNumber: true } n && n.AsNumber() == pid;

    /// <summary>What session pid <paramref name="pid"/> is: a visible team's Orchestrator, else the member addresses of a live registration.</summary>
    private List<MeshParty> RecipientsOfPid(long pid, RequestContext context, Func<string, MeshTeam?> lookup)
    {
        var owner = mailbox.VisibleTeams(context).FirstOrDefault(t => t.Data.AsObject().Get("orchestrator") is { IsMap: true } o && SamePid(o.AsObject().Get("pid"), pid));
        if (owner is not null)
        {
            return [new MeshParty("orchestrator", HierarchyModule.OrchestratorAddress(owner.Team), "orchestrator", owner.Team, "orchestrator")];
        }

        var found = new List<MeshParty>();
        foreach (var reg in mailbox.LiveRegistrations().Where(r => SamePid(r.Get("pid"), pid)))
        {
            var addresses = reg.Get("addresses") is { IsArray: true } list && list.AsArray().Count > 0
                ? list.AsArray().Select(a => Js(a)).ToList()
                : [Js(reg.Get("address"))];
            var placed = addresses.Select(a => rule.Classify(a, lookup)).Where(c => c.Kind != "external").ToList();
            found.AddRange(placed.Count > 0 ? placed : [new MeshParty("external", Js(reg.Get("address")))]);
        }

        return found;
    }

    /// <summary>The recipients a bare session name can be: team members of that name, else the live sessions peer records heard it from.</summary>
    private (List<MeshParty> Found, bool ByName) RecipientsOfName(string name, RequestContext context, Func<string, MeshTeam?> lookup)
    {
        var members = mailbox.MembersNamed(context, name);
        if (members.Count > 0)
        {
            return (members.Select(m => new MeshParty("member", m.Address, m.Name, m.Team, m.Role)).ToList(), true);
        }

        var pids = new List<long>();
        using var decisions = hierarchy.Core.DecisionReads();
        foreach (var r in name.Length == 0 ? hierarchy.Core.PeerRecordsShared() : hierarchy.Core.PeerRecordsWhere(new PeerQuery("from_name", name)))
        {
            if (!r.IsMap || !(r.AsObject().Get("from_name") is { IsText: true } fn && fn.AsString() == name))
            {
                continue;
            }

            if (r.AsObject().Get("from") is { IsText: true } from && UdsPid(from.AsString()) is { } pid && hierarchy.Core.PidAlive(pid) && !pids.Contains(pid))
            {
                pids.Add(pid);
            }
        }

        return (pids.SelectMany(p => RecipientsOfPid(p, context, lookup)).ToList(), false);
    }

    /// <summary>Rule 3a for a SendMessage: the session holds a peer brief (pending, or answered inside the window) whose briefer is one of <paramref name="names"/>.</summary>
    private bool BriefedBy(string sessionId, List<string> names, double nowMs)
    {
        foreach (var r in hierarchy.Peers.LatestByKey(hierarchy.Core.PeerRecordsLatestByKeyFor(JsonValue.FromString(sessionId))))
        {
            if (!r.IsMap)
            {
                continue;
            }

            var o = r.AsObject();
            if (!(o.Get("session_id") is { IsText: true } sid && sid.AsString() == sessionId))
            {
                continue;
            }

            var briefer = new[] { o.Get("reply_to"), o.Get("from_name"), o.Get("from") }
                .Any(x => x is { IsTruthy: true } v && v.IsText && names.Contains(v.AsString()));
            if (!briefer)
            {
                continue;
            }

            var pending = o.Get("status") is { IsText: true } st && st.AsString() == "pending";
            var ts = o.Get("ts") is { IsText: true } t && Timestamps.TryParseMs(t.AsString(), out var ms) ? ms : double.NaN;
            if (pending || nowMs - ts <= IMeshRule.ReplyWindowMs)
            {
                return true;
            }
        }

        return false;
    }

    public MailboxOutcome<MeshGateVerdict> Check(MeshGateRequest request)
    {
        try
        {
            return new MailboxOutcome<MeshGateVerdict>(CheckCore(request), null);
        }
        catch (MailboxError e)
        {
            return new MailboxOutcome<MeshGateVerdict>(null, e.ToFailure());
        }
    }

    /// <summary>A relay names the address it is for: a session that answers to several addresses is judged as the one the relay names, when it is among them.</summary>
    internal static List<MeshParty> NarrowToRelay(List<MeshParty> candidates, string? text) =>
        EntryMarkerText.Parse(text).FirstOrDefault(m => m.Address is not null) is { } relay
        && candidates.Where(c => c.Address == relay.Address).ToList() is { Count: > 0 } named
            ? named
            : candidates;

    private MeshGateVerdict? CheckCore(MeshGateRequest r)
    {
        if (string.IsNullOrEmpty(r.SessionId) || string.IsNullOrEmpty(r.To))
        {
            return null;
        }

        if (mailbox.ReadRegistration(r.SessionId) is null)
        {
            return null;
        }

        var context = r.Context;
        var nowMs = r.NowMs ?? clock.UtcNow.ToUnixTimeMilliseconds();
        var lookup = mailbox.TeamLookup(context);
        var asker = new JsonObject { ["session_id"] = r.SessionId };
        var (first, _) = send.SenderOfCore(asker, lookup, null, context, gate: true, callerPid: r.CallerPid);
        if (first.Kind == "system")
        {
            return null;
        }

        var to = r.To;
        List<MeshParty> candidates;
        if (to == "*")
        {
            candidates = [new MeshParty("broadcast", "*", Team: first.Team)];
        }
        else if (to.Contains('@', StringComparison.Ordinal))
        {
            candidates = [rule.Classify(to, lookup)];
            candidates = candidates.Where(c => c.Kind != "external").ToList();
        }
        else if (UdsPid(to) is { } pid)
        {
            candidates = RecipientsOfPid(pid, context, lookup);
        }
        else
        {
            var (found, byName) = RecipientsOfName(to, context, lookup);
            candidates = byName ? [found.FirstOrDefault(m => m.Team == first.Team) ?? found[0]] : found;
        }

        if (candidates.Count == 0)
        {
            if (!to.StartsWith("uds:", StringComparison.Ordinal))
            {
                NoteUnclassified(first, to, context, lookup, nowMs);
            }

            return OrchestratorMoved(first, to, context);
        }

        candidates = NarrowToRelay(candidates, r.Text);
        MeshGateVerdict? worst = null;
        var worstRank = -1;
        foreach (var candidate in candidates)
        {
            var verdict = VerdictFor(candidate, r, nowMs, context, lookup, first, asker);
            var rank = !verdict.Verdict.Allow ? 2 : verdict.Verdict.Warn ? 1 : 0;
            if (worst is null || rank > worstRank)
            {
                worst = verdict;
                worstRank = rank;
            }
        }

        return worst;
    }

    /// <summary>
    /// A member sending to the socket of a process that has exited, while its team's Orchestrator runs under another pid: most likely the session
    /// that briefed it came back under a new process, so the send is refused and names the address that reaches the Orchestrator now.
    /// </summary>
    private MeshGateVerdict? OrchestratorMoved(MeshParty sender, string to, RequestContext context)
    {
        if (sender.Kind != "member" || sender.Team is null || UdsPid(to) is not { } gone || mailbox.ProcessStart(gone) is not null)
        {
            return null;
        }

        try
        {
            var address = HierarchyModule.OrchestratorAddress(sender.Team);
            var resolved = mailbox.ResolveCore(address, context);
            if (resolved.Orchestrator?.Member("pid") is not { IsNumber: true } pid || (long)pid.AsNumber() == gone || mailbox.ProcessStart((long)pid.AsNumber()) is null)
            {
                return null;
            }

            var message = $"ah: that session has exited. Your Orchestrator now answers at {Mailbox.SendTarget(resolved, null, context.SocketDirectory)}; send it there, and send a report with ah msg send {address}.";
            return new MeshGateVerdict(new MeshVerdict(false, "orchestrator-moved", Message: message), null);
        }
        catch (MailboxError)
        {
            return null;
        }
    }

    /// <summary>A member sending under <c>star</c> or <c>star-warn</c> to a name nothing could place: allowed, with one log line in its Orchestrator's box.</summary>
    private void NoteUnclassified(MeshParty sender, string to, RequestContext context, Func<string, MeshTeam?> lookup, double nowMs)
    {
        if (sender.Kind != "member" || rule.SettingsOf(lookup(sender.Team!)?.Data).Policy == "open")
        {
            return;
        }

        try
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds((long)nowMs);
            var dir = mailbox.ResolveCore(HierarchyModule.OrchestratorAddress(sender.Team), context).Dir;
            mailbox.LogUnclassified(
                dir,
                $"uc-{mailbox.NewIdAt(at)}",
                new JsonObject { ["from"] = sender.Address!, ["to"] = to, ["at"] = Timestamps.ToIsoString(at) });
        }
        catch (MailboxError)
        {
            // evidence only
        }
    }

    private MeshGateVerdict VerdictFor(MeshParty incoming, MeshGateRequest r, double nowMs, RequestContext context, Func<string, MeshTeam?> lookup, MeshParty first, JsonObject asker)
    {
        var sender = !string.IsNullOrEmpty(incoming.Team) && incoming.Kind != "broadcast"
            ? send.SenderOfCore(asker, lookup, incoming.Team, context, gate: true, callerPid: r.CallerPid).Sender
            : first;
        var recipient = incoming.Kind == "broadcast" ? incoming with { Team = sender.Team } : incoming;
        var names = new[] { recipient.Name, recipient.Address, r.To }.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
        var mail = send.FactsFor(context, nowMs);
        var facts = new GateFacts(mail, () => BriefedBy(r.SessionId!, names, nowMs));
        var verdict = rule.Decide(new MeshDecideRequest(nowMs, sender, recipient, lookup, r.Text, r.MsgKind, facts, send.CurrentTask(sender, context)));
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)nowMs);
        var entryId = mailbox.NewIdAt(at);
        Action? after = null;
        if (verdict.Warn)
        {
            try
            {
                mailbox.LogWouldRefuse(mailbox.ResolveCore(recipient.Address, context).Dir, $"sm-{entryId}", verdict.Would);
            }
            catch (MailboxError)
            {
                // evidence only
            }

            if (verdict.Mirror)
            {
                after = () =>
                {
                    var t = send.CurrentTask(sender, context);
                    if (t is null)
                    {
                        return;
                    }

                    try
                    {
                        var entry = new JsonObject { ["id"] = entryId, ["text"] = r.Text };
                        send.MirrorLinked(null, t.Id, NodePath.Dirname(NodePath.Dirname(t.Path!)), sender, recipient, null, entry, context);
                    }
                    catch (Exception e) when (e is MailboxError or IOException or ArgumentException or InvalidOperationException)
                    {
                        // a warning-mode mirror is evidence; it does not stop the send
                    }
                };
            }
        }

        if (!verdict.Allow || !verdict.Count)
        {
            return new MeshGateVerdict(verdict, after);
        }

        after = () =>
        {
            // every send the gate sees is one ahd does not carry, so rule 9a never allows it here: no assignment link is opened or counted
            var used = send.LinksUse(
                new JsonObject { ["dir"] = verdict.Dir!, ["team"] = verdict.Team!, ["from"] = sender.Address!, ["to"] = recipient.Address!, ["now"] = nowMs },
                context,
                $"use-{entryId}",
                hook: true);
            if (used.Member("none") is { IsTruthy: true })
            {
                send.AnnounceEnded(context, verdict.Team, used.Member("ended"), at);
                throw new IOException("the link covering this message ended before it was counted");
            }

            var linkId = used.Member("id");
            try
            {
                var taskHome = send.TaskHomeOf(verdict.Dir!, verdict.Team!, linkId) ?? verdict.Dir!;
                var entry = new JsonObject { ["id"] = entryId, ["text"] = r.Text };
                send.MirrorLinked(linkId, used.Member("task") is { } task ? Js(task) : null, taskHome, sender, recipient, used.Member("n"), entry, context);
            }
            catch (Exception e) when (e is MailboxError or IOException or ArgumentException or InvalidOperationException)
            {
                send.LinksRelease(new JsonObject { ["dir"] = verdict.Dir!, ["team"] = verdict.Team!, ["id"] = linkId ?? JsonValue.Null }, context, $"release-{entryId}", hook: true);
                send.AnnounceEnded(context, verdict.Team, MeshSend.EndedOtherThan(used.Member("ended"), linkId), at);
                throw new IOException($"the message was not delivered: its record could not be written ({e.Message})");
            }

            send.AnnounceEnded(context, verdict.Team, used.Member("ended"), at);
        };
        return new MeshGateVerdict(verdict, after);
    }

    /// <summary>The mailbox facts, with rule 3a also satisfied by a peer brief the session holds from the recipient.</summary>
    private sealed class GateFacts(IMeshFacts mail, Func<bool> briefed) : IMeshFacts
    {
        public bool ProcessLive(JsonValue pid, JsonValue? pidStart) => mail.ProcessLive(pid, pidStart);

        public bool RelayLive(string entryId, string address) => mail.RelayLive(entryId, address);

        public bool RunLinkLive(JsonObject link, string dir, string team, Func<string, MeshTeam?> lookup) => mail.RunLinkLive(link, dir, team, lookup);

        public SharedAssignment? Assigned(MeshParty sender, MeshParty recipient, string dir, string team, Func<string, MeshTeam?> lookup) => mail.Assigned(sender, recipient, dir, team, lookup);

        public bool ChainReachesTask(string messagePath, string taskId, string dir, string team) => mail.ChainReachesTask(messagePath, taskId, dir, team);

        public bool RepliesToBriefer(MeshParty sender, MeshParty recipient, string? msgKind) =>
            briefed() || mail.RepliesToBriefer(sender, recipient, msgKind == "sendmessage" ? "note" : msgKind);
    }

    public string? LinkEnvelopeFor(RequestContext context, string? sessionId, string? from, double? nowMs = null)
    {
        var pid = from is null ? null : UdsPid(from);
        var reg = pid is not null && !string.IsNullOrEmpty(sessionId) ? mailbox.ReadRegistration(sessionId) : null;
        if (reg is null)
        {
            return null;
        }

        var now = nowMs ?? clock.UtcNow.ToUnixTimeMilliseconds();
        var lookup = mailbox.TeamLookup(context);
        var mine = reg.Get("addresses") is { IsArray: true } list && list.AsArray().Count > 0
            ? list.AsArray().Select(a => Js(a)).ToList()
            : [Js(reg.Get("address"))];
        foreach (var sender in RecipientsOfPid(pid!.Value, context, lookup))
        {
            var team = sender.Kind == "member" ? lookup(sender.Team!) : null;
            if (team?.Dir is null)
            {
                continue;
            }

            foreach (var address in mine.Where(a => a.EndsWith($"@{sender.Team}", StringComparison.Ordinal)))
            {
                var covering = links.ActiveCovering(team.Dir, sender.Team!, JsonValue.FromString(sender.Address!), JsonValue.FromString(address), now);
                if (covering.Count > 0)
                {
                    var link = covering[0];
                    return core.EnvelopeLine(link.Member("id"), JsonValue.FromString(sender.Address!));
                }
            }
        }

        return null;
    }

    public void RevokeLinksFor(string teamPath, IReadOnlyList<string>? names)
    {
        try
        {
            var name = NodePath.Basename(teamPath);
            var team = name.EndsWith(".json", StringComparison.Ordinal) ? name[..^".json".Length] : name;
            var dir = RosterModule.TeamFileHome(teamPath);
            if (dir is null)
            {
                return;
            }

            var context = new RequestContext(dir, dir, string.Empty, string.Empty);
            var rows = links.ReadLinks(dir, team).Get("links") is { IsArray: true } list ? list.AsArray().ToList() : [];
            foreach (var l in rows)
            {
                if (!l.IsMap || !(l.AsObject().Get("state") is { IsText: true } st && st.AsString() == "active"))
                {
                    continue;
                }

                var o = l.AsObject();
                var on = new List<string> { Js(o.Get("requester")) };
                if (o.Get("peers") is { IsArray: true } peers)
                {
                    on.AddRange(peers.AsArray().Select(p => Js(p)));
                }

                if (names is not null && !on.Any(a => names.Contains(a.Split('@')[0])))
                {
                    continue;
                }

                var id = Js(o.Get("id"));
                var result = send.LinksRevoke(
                    new JsonObject { ["dir"] = dir, ["team"] = team, ["id"] = id, ["reason"] = "member_left" },
                    context,
                    $"revoke-{id}-left");
                var ended = new JsonArray();
                if (result.Member("ended") is { IsArray: true } e)
                {
                    foreach (var row in e.AsArray())
                    {
                        ended.Add(row);
                    }
                }

                if (result.Member("revoked") is { IsTruthy: true })
                {
                    var row = new JsonArray();
                    row.Add(id);
                    row.Add("member_left");
                    row.Add(o.Get("used") ?? JsonValue.Null);
                    ended.Add(row);
                }

                send.AnnounceEnded(context, team, ended, clock.UtcNow);
            }
        }
        catch (Exception e) when (e is MailboxError or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // a failed revoke leaves the link to end lazily, when its peer is found gone
        }
    }
}
