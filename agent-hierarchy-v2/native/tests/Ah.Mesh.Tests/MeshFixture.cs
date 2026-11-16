using Ah.Abstractions;
using Ah.Config;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// The libraries a mesh test drives, wired over the real file system of one sandbox. Writes run the registered domains' op bodies
/// directly against the file system, the way the state engine runs them under its lock.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class MeshFixture
{
    /// <summary>The plugin root the fake environment names; a table writes it as <c>$AHROOT</c>.</summary>
    internal const string PluginRoot = "/ah-plugin-root";

    private MeshFixture()
    {
    }

    internal FakeClock Clock { get; private init; } = null!;

    internal FakeEnvironment Environment { get; private init; } = null!;

    internal IFileReader Files { get; private init; } = null!;

    internal IRosterCore Roster { get; private init; } = null!;

    internal HierarchyServices Hierarchy { get; private init; } = null!;

    internal IMailbox Mailbox { get; private init; } = null!;

    internal IMailCore Core { get; private init; } = null!;

    internal ILinks Links { get; private init; } = null!;

    internal IMeshRule Rule { get; private init; } = null!;

    internal IMeshSender Sender { get; private init; } = null!;

    internal IMeshGate Gate { get; private init; } = null!;

    internal IRelayObligations Relay { get; private init; } = null!;

    internal IMailboxHooks Hooks { get; private init; } = null!;

    internal IMailInspect Inspect { get; private init; } = null!;

    internal IMailCallers Callers { get; private init; } = null!;

    internal Ah.Config.IConfigCore Config { get; private init; } = null!;

    internal DirectDomainWriter Writer { get; private init; } = null!;

    internal IProcessTable Table { get; private init; } = null!;

    /// <summary>This test process's start time as the table reads it: the start a registration of the test's own pid must record to prove it.</summary>
    internal string? OwnStart => Table.StartTime(System.Environment.ProcessId);

    /// <summary>
    /// Registers <paramref name="session"/> as this test process (its pid and start time), so a route call whose peer is this process
    /// is that session.
    /// </summary>
    internal void Prove(string session, string? teamFile, params BoxRef[] boxes) =>
        Assert.True(Mailbox.WriteRegistration(new RegistrationWrite(session, boxes, teamFile, "claude", System.Environment.ProcessId, PidStart: OwnStart, Bound: true)));

    /// <summary>
    /// <paramref name="singleCarrier"/> false (the default) writes the user-level switch off, so a test of the delivery ladder runs
    /// the ladder it was written for; the single-carrier tests ask for true.
    /// </summary>
    internal static MeshFixture Create(IReadOnlyDictionary<string, string> variables, DateTimeOffset now, string cwd, bool singleCarrier = false, IProcessTable? table = null, Func<IFileReader, IFileReader>? wrapMailboxFiles = null, ISessionAuthority? sessions = null, PeerRecordsRoute? peerRoute = null, MsgsRoute? msgsRoute = null, IStatusEntryStore? statusEntries = null, bool withHolds = false)
    {
        if (!singleCarrier && variables.TryGetValue("AH_HOME", out var ahHome))
        {
            Directory.CreateDirectory(ahHome);
            File.WriteAllText(Path.Combine(ahHome, "agent-hierarchy.json"), "{\"single_carrier\":false}\n");
        }

        var env = new FakeEnvironment(variables, new PluginInfo(PluginRoot, "wrangl", "0.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        var clock = new FakeClock(now);
        var random = new SequenceRandom();
        IRosterCore? roster = null;
        var facts = new ProcessFacts(cwd, 4242);
        var config = ConfigModule.CreateCore(io.Files, env, () => roster!, facts);
        var processTable = table ?? new ClaudeSelfTable(Ah.Platform.PlatformModule.CreateProcessTable(Ah.Platform.PlatformModule.CreateProcessRunner()));
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, processTable, holds: withHolds ? Ah.StateDb.StateDbModule.CreateHolds(env.AhHome) : null);
        var writer = new DirectDomainWriter { AhHome = env.AhHome, SyncRoot = msgsRoute is null ? cwd : null };
        var hierarchy = HierarchyModule.Create(new HierarchyEnvironment(io.Files, env, clock, random, io.Processes, config, roster, writer, facts, PeerRoute: peerRoute, MsgsRoute: msgsRoute ?? TestMsgStore.SyncedRoute(env.AhHome, cwd), StatusEntries: statusEntries, Activity: Ah.StateDb.StateDbModule.CreateActivity(env.AhHome), Gates: Ah.StateDb.StateDbModule.CreateGates(env.AhHome), ProgressLogs: Ah.StateDb.StateDbModule.CreateProgressLogs(env.AhHome), Checkpoints: Ah.StateDb.StateDbModule.CreateCheckpoints(env.AhHome)));
        var links = new Links(Ah.StateDb.StateDbModule.CreateLinks(env.AhHome), env, roster, hierarchy.Core, hierarchy.Messages, clock, random);
        var mailbox = MeshModule.CreateMailbox(wrapMailboxFiles?.Invoke(io.Files) ?? io.Files, env, clock, random, hierarchy, roster, writer, processTable, sessions: sessions);
        if (statusEntries is not null)
        {
            hierarchy.Core.Liveness = MeshModule.CreateMemberLiveness(mailbox, processTable, io.Processes);
            writer.RunsAfterWrite = true;
        }

        writer.Add(mailbox.StateDomain());
        writer.Add(links.StateDomain());
        writer.Add(RosterModule.CreateStateDomain(roster, clock, random, null));
        foreach (var domain in hierarchy.StateDomains)
        {
            writer.Add(domain);
        }

        var rule = MeshModule.CreateMeshRule(links, env);
        var relayObligations = MeshModule.CreateRelayObligations(mailbox, hierarchy);
        return new MeshFixture
        {
            Sender = MeshModule.CreateMeshSender(mailbox, rule, links, hierarchy, clock, writer, io.Files),
            Gate = MeshModule.CreateMeshGate(mailbox, rule, links, hierarchy, clock, writer, io.Files),
            Relay = relayObligations,
            Hooks = MeshModule.CreateMailboxHooks(mailbox, relayObligations, hierarchy, clock),
            Inspect = MeshModule.CreateMailInspect(mailbox, io.Files, env, clock),
            Config = config,
            Callers = MeshModule.CreateMailCallers(mailbox, config, roster, hierarchy, relayObligations, env, processTable),
            Clock = clock,
            Environment = env,
            Files = io.Files,
            Roster = roster,
            Hierarchy = hierarchy,
            Mailbox = mailbox,
            Core = MeshModule.CreateMailCore(clock),
            Links = links,
            Rule = rule,
            Writer = writer,
            Table = processTable,
        };
    }
}

/// <summary>
/// The real process table, except that this test process has a terminal of its own and no process reads as the Claude harness: the
/// user at a terminal, as the caller-identity predicate sees one.
/// </summary>
internal sealed class UserTerminalTable(IProcessTable real) : IProcessTable
{
    internal const string OwnTty = "ttys-test";

    public IReadOnlyList<int>? Ancestors(int pid) => real.Ancestors(pid);

    public bool? HasAncestor(int pid, int candidate) => real.HasAncestor(pid, candidate);

    public string? StartTime(long pid) => real.StartTime(pid);

    public string? LocalStartTime(long pid) => real.LocalStartTime(pid);

    public string? Tty(int pid) => pid == System.Environment.ProcessId ? OwnTty : null;

    // a readable command that is not the harness: an unreadable one makes the caller indeterminate, never the user
    public string? Command(int pid) => "zsh";
}

/// <summary>The real process table, except that the command line of this test process and of the processes it started is <c>claude</c>: the Claude harness itself.</summary>
internal sealed class ClaudeSelfTable(IProcessTable real) : IProcessTable
{
    public IReadOnlyList<int>? Ancestors(int pid) => real.Ancestors(pid);

    public bool? HasAncestor(int pid, int candidate) => real.HasAncestor(pid, candidate);

    public string? StartTime(long pid) => real.StartTime(pid);

    public string? LocalStartTime(long pid) => real.LocalStartTime(pid);

    public string? Tty(int pid) => real.Tty(pid);

    public string? Command(int pid) => pid == System.Environment.ProcessId || real.HasAncestor(pid, System.Environment.ProcessId) == true ? "claude" : real.Command(pid);
}

/// <summary>An <see cref="IStateWriter"/> that runs a registered domain's write op body directly, holding no lock.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class DirectDomainWriter : IStateWriter
{
    private readonly List<IStateDomain> _domains = [];

    internal void Add(IStateDomain domain) => _domains.Add(domain);

    /// <summary>The home whose store a message write goes to.</summary>
    internal required string AhHome { get; init; }

    /// <summary>The root the synced message route mirrors, when it is in use: a db-only write syncs it once first, as the route's sync on a read inside the write's transaction would wait on that transaction.</summary>
    internal string? SyncRoot { get; init; }

    /// <summary>Whether a write runs its domain's AfterWrite, as the state client does; off by default so the tests that predate it see no side effects.</summary>
    internal bool RunsAfterWrite { get; set; }

    /// <summary>Every call made through this writer, with the options it carried.</summary>
    internal List<(string Domain, string Op, StateCallOptions? Options)> Calls { get; } = [];

    public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null)
    {
        Calls.Add((domain, op, options));
        var owner = _domains.FirstOrDefault(d => d.Name == domain);
        var spec = owner?.Ops.FirstOrDefault(o => o.Name == op);
        if (spec is null)
        {
            return new StateResult(false, ReadOnlyMemory<byte>.Empty, $"unknown state operation {domain}.{op}", "bad_request");
        }

        OpResult outcome;
        try
        {
            // a db-only domain's write (links, per-dir-files-to-db S11b) runs its DbBody, as the engine runs it
            // and so does an op whose mode key is on the database (the progress logs, per-dir-files-to-db S11c)
            outcome = (domain == "msgs" || owner!.DbOnly || spec.ModeKey is not null) && spec.Kind == OpKind.Write && spec.DbBody is { } dbBody
                ? InDatabase(spec.ModeKey ?? domain, new OpCall(op, argsJson, context, Guid.NewGuid().ToString()), dbBody, owner!.DbOnly)
                : spec.Body(new OpCall(op, argsJson, context), spec.Kind == OpKind.Write ? new DirectStoreWriteContext() : null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new StateResult(false, ReadOnlyMemory<byte>.Empty, e.Message, "op_failed");
        }

        if (RunsAfterWrite && outcome.Ok && spec.Kind == OpKind.Write)
        {
            owner!.AfterWrite(new OpCall(op, argsJson, context), outcome);
        }

        return outcome.Ok
            ? new StateResult(true, outcome.ResultJson)
            : new StateResult(false, ReadOnlyMemory<byte>.Empty, outcome.Error, "op_failed");
    }

    private OpResult InDatabase(string domain, OpCall call, Func<OpCall, IDbOpTransaction, OpResult> body, bool dbOnly)
    {
        using var held = dbOnly && SyncRoot is { } root ? TestMsgStore.HoldSync(AhHome, root) : null;
        var outcome = Ah.StateDb.StateDbModule.CreateOpRunner(AhHome).Run(new DbOpRequest(domain, call.OperationId, "direct", tx => body(call, tx), DbOnly: dbOnly));
        return outcome.Status == DbOpStatus.Done ? new OpResult(true, outcome.ResultJson) : new OpResult(false, default, outcome.Error);
    }

    public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null)
    {
        var document = plan.Read();
        if (plan.Mutate(document) is not { } outcome)
        {
            return new StateUpdateResult(false, new StateResult(true, ReadOnlyMemory<byte>.Empty));
        }

        var spec = plan.Build(outcome, document.Digest);
        var result = Invoke(domain, spec.Op, spec.ArgsJson, context, options);
        return new StateUpdateResult(result.Ok, result);
    }
}
