using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Roster;

namespace Ah.Mesh;

public static partial class MeshModule
{
    /// <summary>The request that sends one mailbox event to <paramref name="to"/>: the part every event send shares. A caller adds what is its own (<c>data</c>, <c>to_session</c>, <c>id</c>).</summary>
    public static JsonObject EventRequest(string to, string from, string cwd, string hierarchyDir, string eventName, string text) => new()
    {
        ["to"] = to,
        ["from"] = from,
        ["cwd"] = cwd,
        ["hierarchy_dir"] = hierarchyDir,
        ["kind"] = "event",
        ["event"] = eventName,
        ["text"] = text,
    };

    /// <summary>
    /// Whether the <c>question-answered</c> event <paramref name="eventId"/> in team <paramref name="team"/>'s Orchestrator box was seen:
    /// <c>{seen, seen_at?, state}</c>, or null when that box holds no such event. Seen is a delivery into the Orchestrator's context or an
    /// ack, the earlier of the two; a superseding or resolving ack, or a declined, withdrawn or superseded entry, is not seen. Nothing is
    /// written.
    /// </summary>
    public static JsonObject? AnswerSeen(IMailbox mailbox, string team, string eventId)
    {
        static string Text(JsonValue? v, string key) => v?.Member(key) is { IsText: true } t ? t.AsString() : string.Empty;
        static long? Ms(string at) => DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUnixTimeMilliseconds() : null;

        var orchestrator = HierarchyModule.OrchestratorAddress(team);
        if (mailbox.ListBoxes().FirstOrDefault(b => b.Address == orchestrator
                && mailbox.ReadEntries(b.Dir).Entries.Any(e => Text(e, "id") == eventId && Text(e, "kind") == "event" && Text(e, "event") == "question-answered")) is not { } box
            || mailbox.EntryStatus(eventId, [new CallerBox(orchestrator, box.Dir)]) is not { } status)
        {
            return null;
        }

        var state = Text(status, "state");
        var ack = mailbox.ReadAck(box.Dir, eventId);
        var voided = state is "declined" or "withdrawn" or "superseded"
            || Text(ack, "by") == "superseded"
            || Text(ack, "reason") is "superseded" or EventRevalidation.ResolvedReason;
        var seen = !voided && state is "delivered" or "acked";
        var reply = new JsonObject { ["seen"] = seen, ["state"] = state };
        if (seen && new[] { Ms(Text(status.Get("delivered"), "at")), Ms(Text(status.Get("acked"), "at")) }.Min() is { } at)
        {
            reply["seen_at"] = at;
        }

        return reply;
    }

    /// <summary>
    /// Whether a team-file member is a QA member: its <c>role</c> is <c>qa</c>. A box address is <c>&lt;member name&gt;@&lt;team&gt;</c>
    /// and a member's name need not start with its role, so the role, not the address, says which boxes are QA's.
    /// </summary>
    public static bool IsQaMember(JsonObject? member) =>
        member is not null && member.Get("role") is { IsText: true } role && role.AsString() == "qa";

    /// <summary>Whether this call chain is inside a <see cref="BeginHoldExempt"/> scope.</summary>
    public static bool HoldExemptActive => HoldExemptScope.Active;

    /// <summary>
    /// Opens a scope in which every <c>ah msg send</c> carries <c>hold_exempt</c> and goes to the mailbox files directly, never
    /// through (or starting) the daemon. Only the quit and resume verbs open one; no command-line flag can.
    /// </summary>
    public static IDisposable BeginHoldExempt() => HoldExemptScope.Begin();

    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <summary>The mailbox rules that need no I/O (marker parsing, due entries, the prompt a session is handed).</summary>
    public static IMailCore CreateMailCore(IClock clock) => new MailCore(clock);

    /// <summary>The decision of the Claude mod's mail poll, over <paramref name="mailbox"/> (made by <see cref="CreateMailbox"/>).</summary>
    public static IMailDue CreateMailDue(IMailbox mailbox, IMailCore core, IFileReader files, IAhEnvironment environment, IMailboxHooks? hooks = null) =>
        new MailDue(mailbox, core, files, environment, hooks, CreateMailDoorbell(mailbox, core, files, environment, hooks));

    /// <summary>The digest block of <paramref name="entries"/> then <paramref name="extraLines"/>, and the entries it shows (the caller acks them); null when empty.</summary>
    public static string? DigestText(IReadOnlyList<DigestEntry> entries, IReadOnlyList<string> extraLines, out IReadOnlyList<DigestEntry> shown) =>
        MailDoorbell.DigestText(entries, extraLines, out shown);

    /// <summary>The doorbell and the pull behind <c>ah msg next</c>, over <paramref name="mailbox"/> (made by <see cref="CreateMailbox"/>).</summary>
    public static IMailDoorbell CreateMailDoorbell(IMailbox mailbox, IMailCore core, IFileReader files, IAhEnvironment environment, IMailboxHooks? hooks = null) =>
        new MailDoorbell(mailbox as Mailbox ?? throw new ArgumentException("the doorbell needs the mailbox made by CreateMailbox", nameof(mailbox)), core, files, environment, hooks);

    /// <summary>The peer mesh's send rule over the links library.</summary>
    public static IMeshRule CreateMeshRule(ILinks links, IAhEnvironment environment) => new MeshRule(links, environment);

    /// <summary>The mailbox send under the mesh policy: who sends, the rule, counting and mirroring a linked message, broadcast fan-out.</summary>
    public static IMeshSender CreateMeshSender(
        IMailbox mailbox,
        IMeshRule rule,
        ILinks links,
        HierarchyServices hierarchy,
        IClock clock,
        IStateWriter state,
        IFileReader files,
        Func<string, string, DateTimeOffset?>? removedAt = null,
        IRunFacts? runFacts = null) =>
        new MeshSend(mailbox as Mailbox ?? throw new ArgumentException("not a mailbox made by CreateMailbox", nameof(mailbox)), rule, links, hierarchy, clock, state, removedAt, runFacts);

    /// <summary>The instruction to send <paramref name="line"/> to <paramref name="to"/> so entry <paramref name="entry"/> of <paramref name="address"/> is picked up; the daemon's prompt and the Stop hook print this one text.</summary>
    public static string RelayInstructionText(string entry, string address, string to, string line) =>
        RelayInstruction.Text(entry, address, to, line);

    /// <summary>The carrier's relay obligations: a freshly acked <c>relay-needed</c> request becomes a relay row in the peer records.</summary>
    public static Ah.Hierarchy.IMemberLiveness CreateMemberLiveness(IMailbox mailbox, IProcessTable table, IProcessInspector processes, Func<DateTimeOffset?>? bootTime = null) =>
        new MemberLive(mailbox, table, processes, bootTime);

    public static IRelayObligations CreateRelayObligations(IMailbox mailbox, HierarchyServices hierarchy) =>
        new RelayObligations(mailbox as Mailbox ?? throw new ArgumentException("not a mailbox made by CreateMailbox", nameof(mailbox)), hierarchy);

    /// <summary>The hook-side helpers: what a delivered prompt or SendMessage acks, a session's open briefs, the status summary.</summary>
    public static IMailboxHooks CreateMailboxHooks(IMailbox mailbox, IRelayObligations relay, HierarchyServices hierarchy, IClock clock)
    {
        var box = mailbox as Mailbox ?? throw new ArgumentException("not a mailbox made by CreateMailbox", nameof(mailbox));
        var hooks = new MailboxHooks(
            box,
            relay as RelayObligations ?? throw new ArgumentException("not made by CreateRelayObligations", nameof(relay)),
            hierarchy,
            new MailCore(clock),
            clock);
        box.EventStillApplies = entry => hooks.EventApplies(null, entry);
        return hooks;
    }

    /// <summary>The read-only mailbox views of the daemon's inspect endpoints and the CLI's file fallback.</summary>
    public static IMailInspect CreateMailInspect(IMailbox mailbox, IFileReader files, IAhEnvironment environment, IClock clock) =>
        new MailInspect(mailbox as Mailbox ?? throw new ArgumentException("not a mailbox made by CreateMailbox", nameof(mailbox)), files, environment, clock);

    /// <summary>The client-side caller helpers: request context, who the caller is, its registration, the join deciders, ack routing.</summary>
    public static IMailCallers CreateMailCallers(
        IMailbox mailbox,
        IConfigCore config,
        IRosterCore roster,
        HierarchyServices hierarchy,
        IRelayObligations relay,
        IAhEnvironment environment,
        IProcessTable processes,
        string? ahVersion = null,
        IClock? clock = null) =>
        new MailCallers(
            mailbox as Mailbox ?? throw new ArgumentException("not a mailbox made by CreateMailbox", nameof(mailbox)),
            config,
            roster,
            hierarchy,
            relay,
            environment,
            processes,
            ahVersion,
            clock);

    /// <summary>Decides whether a registration's claimed pid is its registrant's own or an ancestor's, reading ancestry and start time from <paramref name="processes"/>.</summary>
    public static IRegistrationBinder CreateRegistrationBinder(Func<int, bool> alive, IProcessTable processes) => new RegistrationBinder(alive, processes);

    /// <summary>The check for the SendMessage gate and <c>deliver</c>, over the same pieces as <see cref="CreateMeshSender"/>.</summary>
    public static IMeshGate CreateMeshGate(
        IMailbox mailbox,
        IMeshRule rule,
        ILinks links,
        HierarchyServices hierarchy,
        IClock clock,
        IStateWriter state,
        IFileReader files,
        IRunFacts? runFacts = null)
    {
        var concrete = mailbox as Mailbox ?? throw new ArgumentException("not a mailbox made by CreateMailbox", nameof(mailbox));
        var send = new MeshSend(concrete, rule, links, hierarchy, clock, state, null, runFacts);
        return new MeshGate(concrete, send, rule, links, hierarchy, clock, new MailCore(clock));
    }

    /// <summary>The mailbox's registrations, acks, heartbeats and routing rows; <paramref name="state"/> runs its writes.</summary>
    public static IMailbox CreateMailbox(
        IFileReader files,
        IAhEnvironment environment,
        IClock clock,
        IRandom random,
        HierarchyServices hierarchy,
        IRosterCore roster,
        IStateWriter state,
        IProcessTable? processes = null,
        Func<bool>? claimsServed = null,
        ISessionAuthority? sessions = null,
        IMailStore? mail = null,
        IHomeDocs? homeDocs = null) =>
        new Mailbox(files, environment, clock, random, hierarchy, roster, state, processes, claimsServed, sessions, mail, homeDocs);

    /// <summary>
    /// The distinct team files a registration was made under: its <c>team_files</c> values, else <c>[team_file]</c>, else none.
    /// </summary>
    public static IReadOnlyList<string> RegistrationTeamFiles(Ah.Core.Json.JsonObject? registration) => RegistrationTeams.Of(registration);

    /// <summary>The boxes a registration records, as their address and directory.</summary>
    public static IReadOnlyList<BoxRef> RegistrationBoxes(Ah.Core.Json.JsonObject? registration) => RegistrationTeams.Boxes(registration);

    /// <summary>The team file a registration records for <paramref name="address"/>: its <c>team_files</c> entry, else <c>team_file</c>, else null.</summary>
    public static string? RegistrationTeamFileOf(Ah.Core.Json.JsonObject? registration, string address) => RegistrationTeams.For(registration, address);
}
