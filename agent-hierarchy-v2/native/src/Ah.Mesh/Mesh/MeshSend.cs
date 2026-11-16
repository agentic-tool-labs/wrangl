using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>Who sends: the party the rule sees, and the address the entry is stamped <c>from</c>.</summary>
public sealed record MeshSender(MeshParty Sender, string From);

/// <summary>
/// The mailbox send under the peer mesh: it resolves who is sending from the caller's registration, asks the one rule, counts and
/// mirrors a linked message, then appends the entry. A broadcast (<c>*@team</c>) fans out here too.
/// </summary>
public interface IMeshSender
{
    /// <summary>
    /// Who sends. The caller's registration names its addresses; a <c>from</c> that is a member's or an Orchestrator's address must be
    /// one of them, and any other <c>from</c> is only an attribution. A caller with no registration is <c>user</c>.
    /// </summary>
    MailboxOutcome<MeshSender> SenderOf(JsonObject request, Func<string, MeshTeam?> lookup, string? toTeam);

    /// <summary>Sends one message under the mesh policy; the result is the <c>send --json</c> object.</summary>
    MailboxOutcome<JsonObject> Send(JsonObject request, DateTimeOffset? now = null, TimeSpan? lockWait = null);

    /// <summary><see cref="Send"/>, telling <paramref name="mark"/> the name of each step as it ends (resolve, append, describe), for the daemon's slow-send log.</summary>
    MailboxOutcome<JsonObject> SendMarked(JsonObject request, Action<string> mark) => Send(request);

    /// <summary>
    /// Whether <paramref name="sender"/> may message <paramref name="recipient"/> (both <c>name@team</c> addresses, or <c>user</c>), decided by the
    /// rule and the facts a real send uses. A <paramref name="slug"/> adds the newest request filed under it as the message's ref. Nothing is written.
    /// </summary>
    MeshVerdict Explain(RequestContext context, string sender, string recipient, string? slug) => throw new NotSupportedException();
}

internal sealed partial class MeshSend(
    Mailbox mailbox,
    IMeshRule rule,
    ILinks links,
    HierarchyServices hierarchy,
    IClock clock,
    IStateWriter state,
    Func<string, string, DateTimeOffset?>? removedAt = null,
    IRunFacts? runFacts = null) : IMeshSender
{
    /// <summary>How long after a known removal a member that is still alive may send its closing report.</summary>
    // TODO: when the removal ledger lands, pass removedAt at every construction site (DaemonServices, ServiceGraph, MeshModule.D4); until then it is unknown and only the narrow set applies.
    private static readonly TimeSpan RemovedWindow = TimeSpan.FromMinutes(30);

    private Mailbox Box => mailbox;

    [GeneratedRegex(@"\A\*@([A-Za-z0-9._-]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex BroadcastRe();

    [GeneratedRegex(@"\A[a-z-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleRe();

    private static string Js(JsonValue? value) => JsConvert.ToJsString(value);

    private static JsonValue? V(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return JsonValue.FromString(text);
    }

    private static string Falsy(JsonValue? value) => value is { IsTruthy: true } v ? JsConvert.ToJsString(v) : string.Empty;

    // ---------------------------------------------------------------- who sends

    private bool IsMemberAddress(string address, Func<string, MeshTeam?> lookup) => rule.Classify(address, lookup).Kind is "member" or "orchestrator";

    public MailboxOutcome<MeshSender> SenderOf(JsonObject request, Func<string, MeshTeam?> lookup, string? toTeam)
    {
        try
        {
            var (sender, from) = SenderOfCore(request, lookup, toTeam, Mailbox.ContextOf(request));
            return new MailboxOutcome<MeshSender>(new MeshSender(sender, from), null);
        }
        catch (MailboxError e)
        {
            return new MailboxOutcome<MeshSender>(null, e.ToFailure());
        }
    }

    /// <summary>
    /// Who sends: the registration's addresses, plus <c>orchestrator@&lt;team&gt;</c> for every team whose file names this session
    /// as its Orchestrator (when <paramref name="context"/> is given), so a stale registration does not hide a team the session
    /// owns. Reads only; nothing is registered.
    /// </summary>
    internal (MeshParty Sender, string From) SenderOfCore(JsonObject req, Func<string, MeshTeam?> lookup, string? toTeam, RequestContext? context = null, bool gate = false, int? callerPid = null)
    {
        var reg = req.Get("session_id") is { IsTruthy: true } sid ? mailbox.ReadRegistration(Js(sid)) : null;
        var addresses = new List<string>();
        if (reg is not null)
        {
            if (reg.Get("addresses") is { IsArray: true } list && list.AsArray().Count > 0)
            {
                addresses.AddRange(list.AsArray().Select(a => Js(a)));
            }
            else
            {
                addresses.Add(Js(reg.Get("address")));
            }
        }

        if (reg is not null && context is { } where && req.Get("session_id") is { IsText: true } session)
        {
            // ownership is proven from the asking process, never from the session id the request names
            foreach (var owned in mailbox.OwnedTeamsAnywhere(where, new TeamInvoker(callerPid, session.AsString())))
            {
                // a team name resolves to the first team of that name the gate sees; the address counts only when that is this file
                var home = Ah.Roster.RosterModule.TeamFileHome(owned.TeamFile);
                if (home is null || lookup(owned.Team) is not { Dir: { } resolved } || !mailbox.SamePlace(resolved, home))
                {
                    continue;
                }

                var ownedAddress = HierarchyModule.OrchestratorAddress(owned.Team);
                if (!addresses.Contains(ownedAddress))
                {
                    addresses.Add(ownedAddress);
                }
            }
        }

        var from = req.Get("from") is { IsText: true } f && f.AsString().Length > 0 ? f.AsString() : null;
        string Mismatch() => $"--from {from} is not one of this session's addresses ({string.Join(", ", addresses)})";
        if (addresses.Count > 0 && (from == "user" || from == "ahd"))
        {
            throw new MailboxError("from_mismatch", Mismatch());
        }

        var claimsMember = from is not null && IsMemberAddress(from, lookup);
        if (claimsMember && addresses.Count > 0 && !addresses.Contains(from!))
        {
            throw new MailboxError("from_mismatch", Mismatch());
        }

        if (addresses.Count == 0)
        {
            if (from is not null && IsMemberAddress(from, lookup))
            {
                throw new MailboxError("from_mismatch", $"{from} is a team member's address; this session has no registration for it");
            }

            return (new MeshParty("system", from ?? "user"), from ?? "user");
        }

        string address;
        if (claimsMember)
        {
            address = from!;
        }
        else
        {
            if (gate)
            {
                // the gate classifies and never stamps: of several identities the session really holds, the one in the
                // recipient's team if exactly one is, else the first, and the rules then judge that one
                var inTeam = toTeam is null ? [] : addresses.Where(a => a.EndsWith("@" + toTeam, StringComparison.Ordinal)).ToList();
                address = inTeam.Count == 1 ? inTeam[0] : addresses[0];
            }
            else
            {
                var chosen = mailbox.ChooseFrom(addresses, toTeam);
                address = chosen.Failure is { } failure ? throw new MailboxError(failure.Code, failure.Message, failure.Candidates) : chosen.Value!;
            }
        }

        var classified = rule.Classify(address, lookup);
        if (!gate && (classified.Kind == "external" || (reg is not null && classified.Kind is "orchestrator" or "member" && RecordsNamedTeamFile(reg, address))))
        {
            // a registered address is held only by the process its team file records, whether or not the request's context sees that team
            return (RegisteredExternal(req, reg, address, lookup), address);
        }

        return (classified, from is not null && !claimsMember ? from : address);
    }

    /// <summary>
    /// A registered session whose proven address the team lookup by the request's context does not place. The registration's own team
    /// file decides, never the team name alone (a team is a hierarchy directory and a name): a member that file still lists sends as a
    /// member; one it no longer lists, and whose session is alive, may only send a report or a note to that team's live Orchestrator,
    /// for 30 minutes after a known removal. Anything unresolvable is refused, never allowed.
    /// </summary>
    private MeshParty RegisteredExternal(JsonObject req, JsonObject? reg, string address, Func<string, MeshTeam?> lookup)
    {
        var parts = rule.SplitAddress(address);
        if (reg is null || parts is null)
        {
            throw new MailboxError("from_mismatch", $"this session's address {address} is not a member or Orchestrator of a known team; a registered session does not send as an outside party");
        }

        var path = RegisteredTeamFile(reg, address);
        if (string.IsNullOrEmpty(path))
        {
            throw new MailboxError("team_unresolved", $"this session's registration names no team file for {address}");
        }

        // a registrant names its own team file: it counts only as the named team file of the team in the address
        var fileName = NodePath.Basename(path);
        if (!mailbox.IsNamedTeamPath(path) || fileName != parts.Team + ".json")
        {
            throw new MailboxError("team_unresolved", $"{path} is not the team file of {parts.Team}");
        }

        var home = Ah.Roster.RosterModule.TeamFileHome(path);
        if (lookup(parts.Team) is { Dir: { } visible } && home is not null && !mailbox.SamePlace(visible, home))
        {
            throw new MailboxError("team_mismatch", $"this session is registered in the team {parts.Team} kept at {path}, not in the team of that name this request's context sees");
        }

        JsonValue data;
        var gone = false;
        try
        {
            gone = !mailbox.TeamExistsAt(path);
            data = gone ? JsonValue.Null : mailbox.ReadTeamAt(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new MailboxError("team_unreadable", $"the team file {path} cannot be read: {e.Message}");
        }

        if (gone)
        {
            throw new MailboxError("team_gone", $"the team file {path} of {address} is gone");
        }

        if (!data.IsMap)
        {
            throw new MailboxError("team_unreadable", $"the team file {path} is not a team record");
        }

        var team = data.AsObject();
        if (parts.Name == "orchestrator")
        {
            // the registration must be the very process the team file names as its Orchestrator, else a stale session would hold its rights
            if (team.Get("orchestrator") is not { IsMap: true } holder || !Holds(holder.AsObject(), reg))
            {
                throw new MailboxError("team_mismatch", $"this session is not the live Orchestrator the team file {path} names for {parts.Team}");
            }

            return new MeshParty("orchestrator", address, parts.Name, parts.Team, "orchestrator");
        }

        var row = data.AsObject().Get("members") is { IsArray: true } members
            ? members.AsArray().FirstOrDefault(m => m.IsMap && m.AsObject().Get("name") is { IsText: true } n && n.AsString() == parts.Name)
            : (JsonValue?)null;
        if (row is { IsMap: true } listed)
        {
            // a row that records a pid or a session names the one session that is this member; a respawned member leaves the old session
            // out. A matching pid and start proves it whatever the session id says (the id lags after a clear or a resume until the next
            // rebind). With a pid on the row the session id proves nothing; with none, it must match. A row that records neither cannot
            // tell sessions apart and is accepted.
            if (!RowIsThisSession(listed.AsObject(), reg) && !RebindBareRelaunch(listed.AsObject(), reg, path, home, parts.Name))
            {
                var held = listed.AsObject();
                var holder = held.Get("pid") is { IsNumber: true } hp ? ((long)hp.AsNumber()).ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";
                var remedy = HeldRow.Remedy(held, Running(held), parts.Team, parts.Name);
                var mine = reg?.Get("pid") is { IsNumber: true } rp ? ((long)rp.AsNumber()).ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";
                var starts = $"row start {(held.Get("pid_start") is { IsText: true } hs ? hs.AsString() : "none")}, registration start {(reg?.Get("pid_start") is { IsText: true } gs ? gs.AsString() : "none")}";
                throw new MailboxError("team_mismatch", $"{address} is held by another session in {path}; this session is not that member (row pid {holder}, registration pid {mine}; {starts}); {remedy}");
            }

            return new MeshParty("member", address, parts.Name, parts.Team, listed.AsObject().Get("role") is { IsTruthy: true } r ? JsConvert.ToJsString(r) : null);
        }

        if (removedAt?.Invoke(path, parts.Name) is { } removed && clock.UtcNow - removed > RemovedWindow)
        {
            throw new MailboxError("removed_member", $"{address} was removed from its team more than 30 minutes ago");
        }

        var kind = req.Get("kind") is { IsTruthy: true } k ? Js(k) : Mailbox.KindFromPayload(req);
        if (Falsy(req.Get("to")) != HierarchyModule.OrchestratorAddress(parts.Team) || kind is not ("report" or "note"))
        {
            throw new MailboxError("removed_member", $"{address} was removed from its team; it may only send a report or a note to orchestrator@{parts.Team}");
        }

        if (team.Get("orchestrator") is not { IsMap: true } orchestrator || !Running(orchestrator.AsObject()))
        {
            throw new MailboxError("team_gone", $"the Orchestrator of {parts.Team} is not live; there is nothing to deliver {address}'s message to");
        }

        return new MeshParty("member", address, parts.Name, parts.Team, null);
    }

    /// <summary>Whether the process a record names (<c>pid</c>, and <c>pid_start</c> when recorded) is running: alive and not a reused pid.</summary>
    private bool Running(JsonObject record) =>
        record.Get("pid") is { IsNumber: true } pid && hierarchy.Core.PidRunning(pid, record.Get("pid_start"), Ah.Core.Time.LivenessPolicy.RunningOrUnknown);

    /// <summary>
    /// Whether the registration is the live process <paramref name="holder"/> names: the same pid (and start, when both record one).
    /// The session id is never proof here: it is readable from the team file, so any process of the user could copy it into a registration.
    /// </summary>
    private bool Holds(JsonObject holder, JsonObject? reg) => reg is not null && Running(holder) && HeldRow.SamePid(holder, reg);

    /// <summary>
    /// A member relaunched over a row the bare watch marked <c>bare</c>, whose recorded process is gone: the same session id is
    /// accepted, as the Orchestrator rebind accepts it, and the row is rebound to the registration's pid and start. Anything else, or
    /// a row that changed under the lock, is false; a failed write still accepts, and the next send tries the rebind again.
    /// </summary>
    private bool RebindBareRelaunch(JsonObject row, JsonObject? reg, string path, string? dir, string name)
    {
        bool Stale(JsonObject r) =>
            reg is not null && HeldRow.MarkedBare(r) && r.Get("pid") is { IsNumber: true } && !Running(r) && SameSession(r, reg) && reg.Get("pid") is { IsNumber: true };

        if (!Stale(row))
        {
            return false;
        }

        if (dir is null)
        {
            return true;
        }

        try
        {
            _ = hierarchy.StateCalls.UpdateTeamFile(path, dir, doc =>
            {
                if (doc.Member("members") is not { IsArray: true } members)
                {
                    return null;
                }

                var next = new JsonArray();
                var changed = false;
                foreach (var m in members.AsArray())
                {
                    if (!changed && m.IsMap && m.Member("name") is { IsText: true } n && n.AsString() == name && Stale(m.AsObject()))
                    {
                        var copy = m.AsObject().Clone();
                        copy["pid"] = reg!.Get("pid")!.Value;
                        if (reg.Get("pid_start") is { IsText: true } start)
                        {
                            copy["pid_start"] = start;
                        }
                        else
                        {
                            copy.Remove("pid_start");
                        }

                        next.Add(JsonValue.FromObject(copy));
                        changed = true;
                    }
                    else
                    {
                        next.Add(m);
                    }
                }

                if (!changed)
                {
                    return null;
                }

                var team = doc.AsObject().Clone();
                team["members"] = JsonValue.FromArray(next);
                return Ah.Hierarchy.StateCalls.TeamFileOutcome.Write(JsonValue.FromObject(team));
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the acceptance stands; the row stays stale until a later send rebinds it
        }

        return true;
    }

    private static bool SameSession(JsonObject record, JsonObject reg) =>
        record.Get("session_id") is { IsText: true } rs && reg.Get("session_id") is { IsText: true } gs && rs.AsString() == gs.AsString();

    /// <summary>
    /// A row that records a pid is held by the process with that pid and start, whatever the session id says (it lags after a clear or a
    /// resume). A row that records no pid cannot tell processes apart: its session id, when it records one, must be the registration's.
    /// </summary>
    private static bool RowIsThisSession(JsonObject row, JsonObject reg) =>
        row.Get("pid") is { IsNumber: true }
            ? HeldRow.SamePid(row, reg)
            : row.Get("session_id") is not { IsText: true } || SameSession(row, reg);

    /// <summary>Whether the registration's own team file for <paramref name="address"/> is the named team file of that address's team: what a registration made through the daemon always records.</summary>
    private bool RecordsNamedTeamFile(JsonObject reg, string address) =>
        rule.SplitAddress(address) is { } parts && RegisteredAddressesOf(reg).Contains(address) && RegisteredTeamFile(reg, address) is { Length: > 0 } path
        && mailbox.IsNamedTeamPath(path) && NodePath.Basename(path) == parts.Team + ".json";

    private static List<string> RegisteredAddressesOf(JsonObject reg) =>
        reg.Get("addresses") is { IsArray: true } list && list.AsArray().Count > 0
            ? [.. list.AsArray().Select(a => Js(a))]
            : [Js(reg.Get("address"))];

    private static string? RegisteredTeamFile(JsonObject reg, string address) =>
        reg.Get("team_files") is { IsMap: true } byAddress && byAddress.AsObject().Get(address) is { IsText: true } mapped && mapped.AsString().Length > 0
            ? mapped.AsString()
            : reg.Get("team_file") is { IsText: true } single && single.AsString().Length > 0 ? single.AsString() : null;

    // ---------------------------------------------------------------- facts the rule needs

    /// <summary>The facts the rule needs about the mailbox.</summary>
    internal IMeshFacts FactsFor(RequestContext context, double nowMs) => new MailFacts(this, context, nowMs);

    public MeshVerdict Explain(RequestContext context, string sender, string recipient, string? slug)
    {
        var lookup = mailbox.TeamLookup(context);
        MeshParty from;
        MeshParty to;
        try
        {
            from = ResolvedParty(sender, context, lookup, allowUser: true);
            to = ResolvedParty(recipient, context, lookup, allowUser: false);
        }
        catch (MailboxError e)
        {
            return new MeshVerdict(false, "resolve", Code: e.Code, Message: e.Message);
        }

        var reference = string.IsNullOrEmpty(slug) ? null : NewestRequestFor(from.Team ?? to.Team, slug, lookup);
        var nowMs = (double)clock.UtcNow.ToUnixTimeMilliseconds();
        return rule.Decide(new MeshDecideRequest(nowMs, from, to, lookup, string.Empty, "note", FactsFor(context, nowMs), CurrentTask(from, context), reference));
    }

    /// <summary>
    /// A principal resolved the way a send resolves its recipient: a role address names its one member, an unknown one is refused.
    /// <c>user</c> is exempt only as a sender, where a send without a registration is the user.
    /// </summary>
    private MeshParty ResolvedParty(string text, RequestContext context, Func<string, MeshTeam?> lookup, bool allowUser)
    {
        if (allowUser && text == "user")
        {
            return rule.Classify(text, lookup);
        }

        var resolved = mailbox.ResolveCore(text, context);
        return rule.Classify(resolved.Address, lookup) with { Address = resolved.Address };
    }

    /// <summary>The path of the newest request filed in the team's message pool under <paramref name="slug"/>, or null when there is none.</summary>
    private string? NewestRequestFor(string? team, string slug, Func<string, MeshTeam?> lookup)
    {
        if (team is null || lookup(team)?.Dir is not { } dir)
        {
            return null;
        }

        try
        {
            string? newest = null;
            string? newestId = null;
            foreach (var e in hierarchy.Core.ListSlugExchanges([dir], slug))
            {
                if (e.Member("request") is { IsMap: true } request && request.Member("meta") is { IsMap: true } meta
                    && meta.Member("slug") is { IsText: true } s && s.AsString() == slug
                    && meta.Member("id") is { IsText: true } id && request.Member("path") is { IsText: true } path
                    && (newestId is null || string.CompareOrdinal(id.AsString(), newestId) > 0))
                {
                    (newestId, newest) = (id.AsString(), path.AsString());
                }
            }

            return newest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ends a round on the loop link a reviewer's report belongs to: the link whose task the report's ref reaches, else the one whose
    /// task the brief it answers reaches, else the only active loop link covering the pair. Returns the link's <c>round</c> result, or
    /// null when no such link is active.
    /// </summary>
    private JsonValue? CountReportRound(RequestContext context, Func<string, MeshTeam?> lookup, MeshParty sender, MeshParty recipient, string reference, string entryId, double nowMs)
    {
        try
        {
            var team = sender.Team ?? string.Empty;
            var dir = lookup(team)?.Dir;
            if (dir is null)
            {
                return null;
            }

            var loops = links.ActiveCovering(dir, team, JsonValue.FromString(sender.Address ?? string.Empty), JsonValue.FromString(recipient.Address ?? string.Empty), nowMs)
                .Where(l => Ah.Mesh.Links.RoundCap(l) is not null)
                .ToList();
            JsonValue? chosen = null;
            foreach (var candidate in new[] { reference }.Concat(BriefRefs(context, sender, recipient, nowMs)).Where(r => r.Length > 0))
            {
                chosen = loops.Cast<JsonValue?>().FirstOrDefault(l => l!.Value.Member("task") is { IsText: true } task && ChainReaches(candidate, task.AsString(), dir, team));
                if (chosen is not null)
                {
                    break;
                }
            }

            if (chosen is null && loops.Count == 1)
            {
                chosen = loops[0];
            }

            if (chosen is null)
            {
                return null;
            }

            var round = State("links", "round", new JsonObject { ["dir"] = dir, ["team"] = team, ["id"] = chosen.Value.Member("id")!.Value, ["now"] = nowMs }, context, $"round-{entryId}");
            return round.Member("none") is { IsTruthy: true } ? (JsonValue?)null : round;
        }
        catch (Exception e) when (e is MailboxError or IOException or InvalidOperationException)
        {
            // A round that cannot be counted leaves the link as it was; the report is already delivered.
            return null;
        }
    }

    /// <summary>The refs of the fresh briefs the sender holds from the recipient, newest first.</summary>
    private List<string> BriefRefs(RequestContext context, MeshParty sender, MeshParty recipient, double nowMs)
    {
        var dir = Box.ResolveCore(sender.Address, context).Dir;
        return Box.ReadEntries(dir).Entries
            .Select(e => e.AsObject())
            .Where(o => o.Get("kind") is { IsText: true } k && k.AsString() == "brief"
                && (o.Get("from") is { IsText: true } f && f.AsString() == recipient.Address || o.Get("reply_to") is { IsText: true } rt && rt.AsString() == recipient.Address)
                && BriefFresh(o, nowMs))
            .Reverse()
            .Select(o => o.Get("ref") is { IsText: true } r ? r.AsString() : string.Empty)
            .ToList();
    }

    private bool ChainReaches(string messagePath, string taskId, string dir, string team)
    {
        try
        {
            return hierarchy.Messages.ChainReaches(messagePath, taskId, dir, team);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool ProcessLive(JsonValue pid, JsonValue? pidStart) => hierarchy.Core.IdentityLive(pid, pidStart);

    private bool RunLinkLive(JsonObject link, string dir, string team, Func<string, MeshTeam?> lookup) =>
        RunLinks.Live(runFacts, rule, lookup, dir, team, link);

    private SharedAssignment? Assigned(MeshParty sender, MeshParty recipient, string dir, string team, Func<string, MeshTeam?> lookup) =>
        Assignments.Shared(hierarchy.Messages, hierarchy.Core, lookup, dir, team, sender.Name, recipient.Name);

    private sealed class MailFacts(MeshSend owner, RequestContext context, double nowMs) : IMeshFacts
    {
        public bool RelayLive(string entryId, string address)
        {
            try
            {
                var dir = owner.Box.ResolveCore(address, context).Dir;
                return owner.Box.ReadEntries(dir).Entries.Any(e => e.AsObject().Get("id") is { IsText: true } id && id.AsString() == entryId)
                    && !owner.Box.IsAcked(dir, entryId);
            }
            catch (Exception e) when (e is MailboxError or IOException)
            {
                return false;
            }
        }

        /// <summary>Rule 3a: the sender holds a brief whose sender, or <c>reply_to</c>, is the recipient, and answers it with a report or a note.</summary>
        public bool RepliesToBriefer(MeshParty sender, MeshParty recipient, string? msgKind)
        {
            if (msgKind is not ("report" or "note"))
            {
                return false;
            }

            string dir;
            try
            {
                dir = owner.Box.ResolveCore(sender.Address, context).Dir;
            }
            catch (MailboxError)
            {
                return false;
            }

            return owner.Box.ReadEntries(dir).Entries.Any(e =>
            {
                var o = e.AsObject();
                return o.Get("kind") is { IsText: true } k && k.AsString() == "brief"
                    && (Same(o.Get("from"), recipient.Address) || Same(o.Get("reply_to"), recipient.Address))
                    && owner.BriefFresh(o, nowMs);
            });
        }

        public bool ChainReachesTask(string messagePath, string taskId, string dir, string team)
        {
            return owner.ChainReaches(messagePath, taskId, dir, team);
        }

        public bool ProcessLive(JsonValue pid, JsonValue? pidStart) => owner.ProcessLive(pid, pidStart);

        public bool RunLinkLive(JsonObject link, string dir, string team, Func<string, MeshTeam?> lookup) =>
            owner.RunLinkLive(link, dir, team, lookup);

        public SharedAssignment? Assigned(MeshParty sender, MeshParty recipient, string dir, string team, Func<string, MeshTeam?> lookup) =>
            owner.Assigned(sender, recipient, dir, team, lookup);

        private static bool Same(JsonValue? value, string? address) => value is { IsText: true } t && address is not null && t.AsString() == address;
    }

    /// <summary>Whether a brief whose exchange is its <c>ref</c> is still the conversation: open, or answered inside the reply window.</summary>
    private bool BriefFresh(JsonObject entry, double nowMs)
    {
        double Age() => nowMs - ParseMs(entry.Get("created"));

        var reference = entry.Get("ref") is { IsText: true } r ? r.AsString() : null;
        var meta = reference is null ? null : hierarchy.Core.ParseMsgFilename(reference);
        if (meta is null)
        {
            return Age() <= IMeshRule.ReplyWindowMs;
        }

        try
        {
            var exchange = HierarchyModule.CreateExchangeIndex(hierarchy.Core, hierarchy.Messages.PoolsOfRequest(reference!)).Find(meta.Id);
            if (!exchange.IsMap)
            {
                return Age() <= IMeshRule.ReplyWindowMs;
            }

            var ex = exchange.AsObject();
            if (ex.Get("open") is { IsTruthy: true })
            {
                return true;
            }

            if (ex.Get("response") is { IsTruthy: true } response)
            {
                var path = response.Member("path") is { IsText: true } p ? p.AsString() : null;
                return path is not null && hierarchy.Core.MsgTimeMs(path) is { } written && nowMs - written <= IMeshRule.ReplyWindowMs;
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static double ParseMs(JsonValue? at) =>
        at is { IsText: true } t && Timestamps.TryParseMs(t.AsString(), out var ms) ? ms : double.NaN;

    /// <summary>The sender's open task, named in the refusal text when one can be found: its newest brief.</summary>
    internal MeshTask? CurrentTask(MeshParty sender, RequestContext context)
    {
        try
        {
            var dir = mailbox.ResolveCore(sender.Address, context).Dir;
            var brief = mailbox.ReadEntries(dir).Entries
                .Select(e => e.AsObject())
                .LastOrDefault(e => e.Get("kind") is { IsText: true } k && k.AsString() == "brief" && e.Get("ref") is { IsText: true });
            var reference = brief?.Get("ref") is { IsText: true } r ? r.AsString() : null;
            var meta = reference is null ? null : hierarchy.Core.ParseMsgFilename(reference);
            return meta is null ? null : new MeshTask(meta.Id, reference);
        }
        catch (MailboxError)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- the record of a linked message

    /// <summary>The text of a <c>link-note</c> file: the frontmatter the record needs, then the message.</summary>
    internal static string NoteFile(IEnumerable<(string Key, JsonValue? Value)> fields, string text)
    {
        var lines = new List<string> { "---" };
        foreach (var (key, value) in fields)
        {
            lines.Add($"{key}: {(value is null || value.Value.IsNull ? "null" : Js(value))}");
        }

        lines.Add("---");
        lines.Add(string.Empty);
        lines.Add(text);
        lines.Add(string.Empty);
        return string.Join("\n", lines);
    }

    /// <summary>Writes the mirror of a linked message beside its task. Idempotent: a retry of the same entry id finds the file already there.</summary>
    internal void MirrorLinked(JsonValue? link, string? task, string taskHome, MeshParty from, MeshParty to, JsonValue? n, JsonObject entry, RequestContext context)
    {
        var entryId = Js(entry.Get("id"));
        var id = hierarchy.Messages.IsMessageId(entryId) ? entryId : mailbox.NewIdAt(clock.UtcNow);
        var toRole = !string.IsNullOrEmpty(to.Role) && RoleRe().IsMatch(to.Role) ? to.Role : "member";
        var slugFull = $"ln-{task}";
        var slug = slugFull.Length > 32 ? slugFull[..32] : slugFull;
        var name = hierarchy.Messages.MsgFilename(new JsonObject { ["id"] = id, ["to"] = toRole, ["slug"] = slug, ["type"] = "link-note" });
        var text = entry.Get("ref") is { IsTruthy: true } r
            ? MsgPointer.ShowLine(Js(r))
            : entry.Get("text") is { IsTruthy: true } t ? Js(t) : Falsy(entry.Get("event"));
        var content = NoteFile(
            [
                ("id", id),
                ("type", "link-note"),
                ("to", toRole),
                ("from", V(from.Address)),
                ("slug", slug),
                ("parent", V(task)),
                ("link", link),
                ("n", n),
                ("to_address", V(to.Address)),
                ("created", hierarchy.Messages.LocalIso(clock.UtcNow)),
            ],
            text);
        var args = new JsonObject
        {
            ["dir"] = taskHome,
            ["path"] = NodePath.Join(taskHome, "msgs", name),
            ["content"] = content,
        };
        State("msgs", "create", args, context, $"mirror-{Js(entry.Get("id"))}");
    }

    private JsonValue State(string domain, string op, JsonObject args, RequestContext context, string operationId, bool hook = false)
    {
        var result = state.Invoke(domain, op, JsonText.ToUtf8(args, 0), context, new StateCallOptions(OperationId: operationId, Hook: hook));
        if (!result.Ok)
        {
            throw new MailboxError(result.ErrorCode ?? "op_failed", result.Error ?? $"{domain}.{op} failed");
        }

        return result.ResultJson.IsEmpty ? JsonValue.Null : JsonText.Parse(result.ResultJson.Span);
    }

    /// <summary>The hierarchy directory that holds a link's task (its <c>task_home</c>), or null when the link is gone.</summary>
    internal string? TaskHomeOf(string dir, string team, JsonValue? linkId)
    {
        foreach (var l in links.ReadLinks(dir, team).Get("links") is { IsArray: true } list ? list.AsArray() : new JsonArray())
        {
            if (l.IsMap && StrictEqual(l.AsObject().Get("id"), linkId))
            {
                return l.AsObject().Get("task_home") is { IsTruthy: true } home ? Js(home) : dir;
            }
        }

        return null;
    }

    private static bool StrictEqual(JsonValue? a, JsonValue? b) =>
        a is { } x && b is { } y
            && ((x.IsText && y.IsText && x.AsString() == y.AsString())
                || (x.IsNumber && y.IsNumber && x.AsNumber() == y.AsNumber())
                || (x.IsBool && y.IsBool && x.AsBool() == y.AsBool()));

    /// <summary>The ended rows of a <c>use</c> without the counted link's own: a failed send releases that link's count, but the others have lapsed for good.</summary>
    internal static JsonArray EndedOtherThan(JsonValue? ended, JsonValue? id)
    {
        var rows = new JsonArray();
        if (ended is { IsArray: true } list)
        {
            foreach (var row in list.AsArray())
            {
                if (!(row.IsArray && row.AsArray().Count > 0 && StrictEqual(row.AsArray()[0], id)))
                {
                    rows.Add(row);
                }
            }
        }

        return rows;
    }

    /// <summary>One <c>link-ended</c> event to the team's Orchestrator for each link a <c>use</c> just found lapsed.</summary>
    internal void AnnounceEnded(RequestContext context, string? team, JsonValue? ended, DateTimeOffset now)
    {
        if (ended is not { IsArray: true } rows)
        {
            return;
        }

        foreach (var row in rows.AsArray())
        {
            try
            {
                var parts = row.AsArray();
                var req = RequestObject(context);
                req["to"] = HierarchyModule.OrchestratorAddress(team);
                req["from"] = "ahd";
                req["kind"] = "event";
                req["event"] = "link-ended";
                req["text"] = $"{Js(parts[0])} {Js(parts[1])} used={Js(parts[2])}";
                mailbox.SendCore(req, now);
                if (mailbox.TeamLookup(context)(team ?? string.Empty)?.Dir is { } dir && links.LoopClosedText(dir, team!, row) is { } closed)
                {
                    var note = RequestObject(context);
                    note["to"] = HierarchyModule.OrchestratorAddress(team);
                    note["from"] = "ahd";
                    note["kind"] = "note";
                    note["text"] = closed;
                    mailbox.SendCore(note, now);
                }
            }
            catch (Exception e) when (e is MailboxError or IOException or InvalidOperationException or ArgumentOutOfRangeException)
            {
                // the event is a courtesy; the link file holds the fact
            }
        }
    }

    /// <summary>
    /// The one note to the Orchestrator for a delivered entry of a review loop: which link, which way, the task, the round, and the first line
    /// of the entry's own tldr (a reviewer's report leads with its verdict). Best effort, like every announcement.
    /// </summary>
    private void AnnounceHandoff(RequestContext context, string? team, JsonValue? used, MeshParty from, MeshParty to, string kind, string reference, bool roundEnd, DateTimeOffset now)
    {
        if (used is not { IsMap: true } counted || string.IsNullOrEmpty(team))
        {
            return;
        }

        try
        {
            var rounds = counted.Member("rounds") is { IsNumber: true } r ? r.AsNumber() : 0;
            var cap = counted.Member("rounds_cap") is { IsNumber: true } c ? c.AsNumber() : 0;
            var round = roundEnd ? rounds : Math.Min(rounds + 1, cap);
            var slug = reference.Length > 0 ? hierarchy.Core.ParseMsgFilename(reference)?.Slug : null;
            var text = reference.Length > 0 && hierarchy.Core.ReadMsgFile(reference).Member("text") is { IsText: true } body ? TldrLine(body.AsString()) : null;
            var req = RequestObject(context);
            req["to"] = HierarchyModule.OrchestratorAddress(team);
            req["from"] = "ahd";
            req["kind"] = "note";
            req["text"] = $"[loop {Js(counted.Member("id") ?? JsonValue.Null)} {from.Name ?? from.Address}→{to.Name ?? to.Address} task {Js(counted.Member("task") ?? JsonValue.Null)} round {Js(round)}/{Js(cap)}] {kind} {slug ?? "-"}: {text ?? "(no tldr)"}";
            mailbox.SendCore(req, now);
        }
        catch (Exception e) when (e is MailboxError or IOException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // the note is a courtesy; the entry is already delivered and mirrored
        }
    }

    /// <summary>The first line under the <c>## [0] tldr</c> heading of a message, without its bullet.</summary>
    internal static string? TldrLine(string text)
    {
        var inTldr = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inTldr)
                {
                    return null;
                }

                inTldr = line.StartsWith("## [0]", StringComparison.Ordinal);
                continue;
            }

            if (inTldr && line.Length > 0)
            {
                return line.StartsWith("- ", StringComparison.Ordinal) ? line[2..].Trim() : line;
            }
        }

        return null;
    }

    private static JsonObject RequestObject(RequestContext context)
    {
        var o = new JsonObject
        {
            ["cwd"] = context.Cwd,
            ["hierarchy_dir"] = context.HierarchyDirectory,
        };
        o["main_hierarchy_dir"] = string.IsNullOrEmpty(context.MainHierarchyDirectory) ? JsonValue.Null : context.MainHierarchyDirectory;
        o["messaging_socket_dir"] = string.IsNullOrEmpty(context.SocketDirectory) ? JsonValue.Null : context.SocketDirectory;
        return o;
    }

    internal JsonValue LinksUse(JsonObject args, RequestContext context, string operationId, bool hook = false) => State("links", "use", args, context, operationId, hook);

    /// <summary>
    /// The verdict with its link: a rule 9a verdict that names no link yet has the assignment link opened here, by the links domain's
    /// own <c>grant</c>, and the team's Orchestrator told of it once. Any other verdict comes back as it is.
    /// </summary>
    internal MeshVerdict WithAssignmentLink(MeshVerdict verdict, MeshParty sender, MeshParty recipient, RequestContext context, double nowMs, string entryId, bool hook = false)
    {
        if (verdict is not { Allow: true, Assigned: { } key, Link: null })
        {
            return verdict;
        }

        var task = verdict.Task is { IsText: true } named ? named.AsString() : TaskToken(key);
        var link = new JsonObject
        {
            ["task"] = task,
            ["peers"] = new JsonArray { recipient.Address! },
            ["direction"] = "both",
            ["requester"] = sender.Address!,
            ["granted_by"] = "assignment",
            ["assigned"] = key,
        };
        var granted = State("links", "grant", new JsonObject { ["dir"] = verdict.Dir!, ["team"] = verdict.Team!, ["link"] = link, ["now"] = nowMs }, context, $"assign-{entryId}", hook);
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)nowMs);
        AnnounceEnded(context, verdict.Team, granted.Member("ended"), at);
        if (granted.Member("revoked") is { IsTruthy: true })
        {
            throw new MailboxError("not_linked", "the Orchestrator revoked this pair's link for this assignment; ask it for a link");
        }

        var id = granted.Member("id");
        if (granted.Member("existed") is not { IsTruthy: true })
        {
            try
            {
                var req = RequestObject(context);
                req["to"] = HierarchyModule.OrchestratorAddress(verdict.Team);
                req["from"] = "ahd";
                req["kind"] = "event";
                req["event"] = "link-granted";
                req["text"] = $"{Js(id)} {sender.Address} -> {recipient.Address} task {task} (assigned together: {key})";
                mailbox.SendCore(req, at);
            }
            catch (Exception e) when (e is MailboxError or IOException or InvalidOperationException or ArgumentOutOfRangeException)
            {
                // the event is a courtesy; the link record holds the fact
            }
        }

        return verdict with { Link = id, Task = task };
    }

    /// <summary>A task name for a link whose assignment has no request: the key, in the characters a message slug takes.</summary>
    private static string TaskToken(string key) =>
        new(key.ToLowerInvariant().Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-').ToArray());

    internal JsonValue LinksRevoke(JsonObject args, RequestContext context, string operationId) => State("links", "revoke", args, context, operationId, hook: true);

    internal JsonValue LinksRelease(JsonObject args, RequestContext context, string operationId, bool hook = false) => State("links", "release", args, context, operationId, hook);

    // ---------------------------------------------------------------- send

    public MailboxOutcome<JsonObject> Send(JsonObject request, DateTimeOffset? now = null, TimeSpan? lockWait = null) => Send(request, now, lockWait, null);

    public MailboxOutcome<JsonObject> SendMarked(JsonObject request, Action<string> mark) => Send(request, null, null, mark);

    private MailboxOutcome<JsonObject> Send(JsonObject request, DateTimeOffset? now, TimeSpan? lockWait, Action<string>? mark)
    {
        try
        {
            return new MailboxOutcome<JsonObject>(SendCore(request, now ?? clock.UtcNow, lockWait, mark), null);
        }
        catch (MailboxError e)
        {
            return new MailboxOutcome<JsonObject>(null, e.ToFailure());
        }
    }

    /// <summary>What the recipient address is: a broadcast address <c>*@team</c> is its own kind.</summary>
    private (MeshParty Party, ResolvedAddress? Resolved) RecipientOf(JsonValue? to, RequestContext context, Func<string, MeshTeam?> lookup)
    {
        var text = Falsy(to);
        if (BroadcastRe().Match(text) is { Success: true } b)
        {
            return (new MeshParty("broadcast", text, Team: b.Groups[1].Value), null);
        }

        var resolved = mailbox.ResolveCore(text, context);
        var party = rule.Classify(resolved.Address, lookup);
        return (party with { Address = resolved.Address }, resolved);
    }

    private JsonObject SendCore(JsonObject raw, DateTimeOffset now, TimeSpan? lockWait, Action<string>? mark = null)
    {
        var req = new JsonObject();
        foreach (var key in raw.Keys)
        {
            if (key is not ("link" or "post_link"))
            {
                req[key] = raw[key];
            }
        }

        var postLink = raw.Get("post_link");
        var context = Mailbox.ContextOf(req);
        var lookup = mailbox.TeamLookup(context);
        var nowMs = (double)now.ToUnixTimeMilliseconds();
        var to = Falsy(req.Get("to"));
        var broadcast = to.StartsWith("*@", StringComparison.Ordinal);
        var toTeam = broadcast ? to[2..] : rule.SplitAddress(to)?.Team;
        var (sender, from) = SenderOfCore(req, lookup, toTeam);
        if (sender.Kind == "system" && !broadcast)
        {
            var plain = Copy(req);
            plain["from"] = from;
            return mailbox.SendCore(plain, now, lockWait, mark);
        }

        var (recipient, resolved) = RecipientOf(req.Get("to"), context, lookup);
        var kind = req.Get("kind") is { IsTruthy: true } k ? Js(k) : Mailbox.KindFromPayload(req);
        var verdict = rule.Decide(new MeshDecideRequest(nowMs, sender, recipient, lookup, Falsy(req.Get("text")), kind, FactsFor(context, nowMs), CurrentTask(sender, context), Falsy(req.Get("ref")) is { Length: > 0 } reference ? reference : null, req.Get("hold_exempt") is { IsBool: true } exempt && exempt.AsBool()));
        if (!verdict.Allow)
        {
            throw new MailboxError(verdict.Code ?? "refused", verdict.Message ?? string.Empty);
        }

        if (recipient.Kind == "broadcast")
        {
            var fan = Copy(req);
            fan["from"] = from;
            return FanOut(fan, context, lookup, sender, recipient, now, lockWait);
        }

        JsonValue entryId = req.Get("id") is { IsTruthy: true } given ? given : mailbox.NewIdAt(now);
        JsonValue? countedId = null;
        string? countedDir = null;
        string? countedTeam = null;
        JsonValue? endedByUse = null;
        (JsonValue Id, JsonValue? N, JsonValue? Task)? link = null;
        if (postLink is { IsTruthy: true } wanted && sender.Kind == "orchestrator" && recipient.Kind == "member" && recipient.Team == sender.Team)
        {
            var team = lookup(sender.Team!);
            var found = team is null || team.Dir is null
                ? null
                : links.ReadLinks(team.Dir, sender.Team!).Get("links") is { IsArray: true } list
                    ? list.AsArray().FirstOrDefault(x => x.IsMap && StrictEqual(x.AsObject().Get("id"), wanted))
                    : (JsonValue?)null;
            if (found is { IsMap: true } l)
            {
                var o = l.AsObject();
                var on = new List<string> { Js(o.Get("requester")) };
                if (o.Get("peers") is { IsArray: true } peers)
                {
                    on.AddRange(peers.AsArray().Select(p => Js(p)));
                }

                if (on.Contains(recipient.Address!))
                {
                    link = (o.Get("id") ?? JsonValue.Null, null, o.Get("task"));
                }
            }
        }

        var roundEnd = kind == "report" && sender.Role == "reviewer";
        JsonValue? loopRounds = null;
        verdict = WithAssignmentLink(verdict, sender, recipient, context, nowMs, Js(entryId));
        if (verdict.Count)
        {
            var used = LinksUse(
                new JsonObject { ["dir"] = verdict.Dir!, ["team"] = verdict.Team!, ["from"] = sender.Address!, ["to"] = recipient.Address!, ["now"] = nowMs, ["round_end"] = roundEnd, ["id"] = verdict.Link ?? JsonValue.Null },
                context,
                $"use-{Js(entryId)}");
            if (used.Member("none") is { IsTruthy: true })
            {
                AnnounceEnded(context, verdict.Team, used.Member("ended"), now);
                throw new MailboxError("not_linked", "the link covering this message ended before it was counted");
            }

            endedByUse = used.Member("ended");
            countedDir = verdict.Dir;
            countedTeam = verdict.Team;
            countedId = used.Member("id");
            link = (used.Member("id") ?? JsonValue.Null, used.Member("n"), used.Member("task"));
            loopRounds = used.Member("rounds_cap") is { IsNumber: true } ? (JsonValue?)used : null;
        }

        void ReleaseAndAnnounce()
        {
            if (countedId is not null)
            {
                LinksRelease(new JsonObject { ["dir"] = countedDir!, ["team"] = countedTeam!, ["id"] = countedId.Value, ["round_end"] = roundEnd }, context, $"release-{Js(entryId)}");
            }

            AnnounceEnded(context, verdict.Team, EndedOtherThan(endedByUse, countedId), now);
        }

        var withId = Copy(req);
        withId["id"] = entryId;
        try
        {
            if (verdict.Mirror && link is { } counted)
            {
                var taskHome = TaskHomeOf(verdict.Dir!, verdict.Team!, counted.Id) ?? verdict.Dir!;
                MirrorLinked(counted.Id, counted.Task is { IsText: true } t ? t.AsString() : Js(counted.Task), taskHome, sender, recipient, counted.N, withId, context);
            }
        }
        catch (Exception e) when (e is MailboxError or IOException or ArgumentException or InvalidOperationException)
        {
            ReleaseAndAnnounce();
            throw new MailboxError("mirror_failed", $"the message was not delivered: its record could not be written ({e.Message})");
        }

        if (verdict.Warn && verdict.Mirror && link is null && CurrentTask(sender, context) is { } task)
        {
            try
            {
                MirrorLinked(null, task.Id, NodePath.Dirname(NodePath.Dirname(task.Path!)), sender, recipient, null, withId, context);
            }
            catch (Exception e) when (e is MailboxError or IOException or ArgumentException or InvalidOperationException)
            {
                // a warning-mode mirror is evidence; it does not stop the send
            }
        }

        JsonObject result;
        try
        {
            var outgoing = Copy(req);
            outgoing["id"] = entryId;
            outgoing["from"] = from;
            if (link is { } l2)
            {
                outgoing["link"] = new JsonObject { ["id"] = l2.Id, ["from"] = sender.Address! };
            }

            result = mailbox.SendCore(outgoing, now, lockWait, mark);
        }
        catch (MailboxError)
        {
            ReleaseAndAnnounce();
            throw;
        }

        // A reviewer's report answers the implementer's brief under rule 3a, so it never passes through the link; it still ends a round.
        var team0 = verdict.Team ?? sender.Team;
        if (verdict.Rule == "3a" && roundEnd)
        {
            var round = CountReportRound(context, lookup, sender, recipient, Falsy(req.Get("ref")), Js(entryId), nowMs);
            loopRounds = round;
            endedByUse = round?.Member("ended");
        }

        AnnounceHandoff(context, team0, loopRounds, sender, recipient, kind, Falsy(req.Get("ref")), roundEnd, now);
        AnnounceEnded(context, team0, endedByUse, now);
        if (verdict.Warn)
        {
            mailbox.LogWouldRefuse(resolved!.Dir, Js(result.Get("entry")!.Value.AsObject().Get("id")), verdict.Would);
        }

        return result;
    }

    private static JsonObject Copy(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var key in source.Keys)
        {
            copy[key] = source[key];
        }

        return copy;
    }

    /// <summary><c>*@team</c> from the team's own Orchestrator: one entry in every member's box but the sender's, and one note file in the record.</summary>
    private JsonObject FanOut(JsonObject req, RequestContext context, Func<string, MeshTeam?> lookup, MeshParty sender, MeshParty recipient, DateTimeOffset now, TimeSpan? lockWait)
    {
        var team = lookup(recipient.Team!) ?? throw new MailboxError("unknown_address", $"no team {recipient.Team}");
        var kind = req.Get("kind") is { IsTruthy: true } k ? Js(k) : req.Get("event") is { IsTruthy: true } ? "event" : "note";
        if (kind is not ("note" or "event"))
        {
            throw new MailboxError("bad_request", "a broadcast is a note or an event, not a brief or a report");
        }

        JsonValue id = req.Get("id") is { IsTruthy: true } given ? given : mailbox.NewIdAt(now);
        var names = new List<string>();
        if (team.Data.IsMap && team.Data.AsObject().Get("members") is { IsArray: true } members)
        {
            foreach (var m in members.AsArray())
            {
                if (m.IsMap && m.AsObject().Get("name") is { IsText: true } n && n.AsString().Length > 0 && $"{n.AsString()}@{recipient.Team}" != sender.Address)
                {
                    names.Add(n.AsString());
                }
            }
        }

        var delivered = new List<string>();
        JsonObject? first = null;
        foreach (var name in names)
        {
            var one = Copy(req);
            one["to"] = $"{name}@{recipient.Team}";
            one["id"] = id;
            one["from"] = sender.Address!;
            var r = mailbox.SendCore(one, now, lockWait);
            first ??= r;
            delivered.Add(Js(r.Get("address")));
        }

        var idText = Js(id);
        var noteId = hierarchy.Messages.IsMessageId(idText) ? idText : mailbox.NewIdAt(now);
        var text = req.Get("text") is { IsTruthy: true } t ? Js(t) : Falsy(req.Get("event"));
        var fileName = hierarchy.Messages.MsgFilename(new JsonObject { ["id"] = noteId, ["to"] = "all", ["slug"] = "broadcast", ["type"] = "note" });
        var content = NoteFile(
            [
                ("id", noteId),
                ("type", "note"),
                ("to", "all"),
                ("from", V(sender.Address)),
                ("slug", "broadcast"),
                ("team", V(recipient.Team)),
                ("recipients", string.Join(",", delivered)),
                ("created", hierarchy.Messages.LocalIso(now)),
            ],
            text);
        try
        {
            State(
                "msgs",
                "create",
                new JsonObject { ["dir"] = team.Dir!, ["path"] = NodePath.Join(team.Dir!, "msgs", fileName), ["content"] = content },
                context,
                $"broadcast-{idText}");
        }
        catch (MailboxError)
        {
            // the deliveries went; the record of them is the entries themselves
        }

        var list = new JsonArray();
        foreach (var d in delivered)
        {
            list.Add(d);
        }

        return new JsonObject
        {
            ["entry"] = first is null ? new JsonObject { ["id"] = id } : first.Get("entry")!.Value,
            ["address"] = $"*@{recipient.Team}",
            ["delivery"] = "pull",
            ["broadcast"] = new JsonObject { ["delivered"] = list },
        };
    }
}
