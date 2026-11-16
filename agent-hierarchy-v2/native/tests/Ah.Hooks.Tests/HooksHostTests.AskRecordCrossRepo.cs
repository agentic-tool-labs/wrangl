using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    private sealed class AskDomainWriter : IStateWriter
    {
        internal IReadOnlyList<IStateDomain> Domains { get; set; } = [];

        public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null)
        {
            var executing = Domains.Single(d => d.Name == domain);
            if (executing.DbOnly)
            {
                // the question log writes the database, which these cross-repo tests do not read
                return new StateResult(true, "{}"u8.ToArray());
            }

            var body = executing.Ops.Single(o => o.Name == op).Body;
            var result = body(new OpCall(op, argsJson, context, options?.OperationId), new DirectStoreWriteContext());
            return new StateResult(result.Ok, result.ResultJson, result.Error, result.ErrorCode);
        }

        public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null) =>
            throw new NotSupportedException();
    }

    private sealed class AskProcessTable : IProcessTable, IProcessInspector
    {
        internal const int Owner = 4242;
        internal const string Start = "Sat Oct 10 09:00:00 2026";

        public IReadOnlyList<int>? Ancestors(int pid) => [Owner, 1];

        public bool? HasAncestor(int pid, int candidate) => candidate is Owner or 1;

        public string? StartTime(long pid) => Start;

        public bool PidAlive(int pid) => pid == Owner;

        public bool PidOwned(int pid) => pid == Owner;

        public string? CommandLine(int pid) => "claude";
    }

    private static (Rig Rig, GateServices Services, IMailbox Mailbox) CrossRepoAskRig()
    {
        var table = new AskProcessTable();
        var rig = new Rig(processTable: table);
        Directory.CreateDirectory(rig.Sandbox.Root + "/proj/.git");
        var s = rig.Services;
        var random = new SequenceRandom();
        var writer = new AskDomainWriter();
        var hierarchy = HierarchyModule.Create(new HierarchyEnvironment(
            s.Files, s.Environment, s.Clock, random, table, s.Config, s.Roster, writer, s.Process, s.Bootstrap));
        writer.Domains = hierarchy.StateDomains;

        var mailbox = MeshModule.CreateMailbox(s.Files, s.Environment, s.Clock, random, hierarchy, s.Roster, writer, table);
        var relay = MeshModule.CreateRelayObligations(mailbox, hierarchy);
        var callers = MeshModule.CreateMailCallers(mailbox, s.Config, s.Roster, hierarchy, relay, s.Environment, table);
        return (rig, s with
        {
            Hierarchy = hierarchy,
            State = writer,
            Advisory = new AdvisoryServices(mailbox, null!, callers, null!, null!, null!, null!, null!, null!, null!, null!),
        }, mailbox);
    }

    private static string SeedAskTeam(GateServices s, IMailbox mailbox, string cwd, string team, bool bound = true,
        int pid = AskProcessTable.Owner, string? start = AskProcessTable.Start, string session = "sess-1", string? registeredCwd = null)
    {
        if (!File.Exists(Path.Combine(cwd, ".git")))
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        }
        var dir = s.Config.HierarchyDir(cwd);
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        File.WriteAllText(s.Roster.TeamPath(dir, team), JsonText.Stringify(new JsonObject
        {
            ["members"] = new JsonArray(),
            ["orchestrator"] = new JsonObject { ["pid"] = pid, ["session_id"] = session },
        }, 0));
        var registration = mailbox.RegistrationFile(team);
        Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
        File.WriteAllText(registration, JsonText.Stringify(new JsonObject
        {
            ["address"] = "orchestrator@" + team,
            ["boxes"] = new JsonObject { ["orchestrator@" + team] = dir },
            ["pid"] = pid,
            ["pid_start"] = start is null ? JsonValue.Null : start,
            ["bound"] = bound,
            ["session_id"] = session,
            ["cwd"] = registeredCwd ?? cwd,
        }, 0));
        return dir;
    }

    [Fact]
    public void AnAskFromRepoAIsWrittenOnlyInTheRegisteredTeamRepoB()
    {
        var (rig, services, mailbox) = CrossRepoAskRig();
        var dir = SeedAskTeam(services, mailbox, rig.Sandbox.Root + "/repo-b", "b");
        Assert.Empty(services.Advisory!.Callers.JoinDeciderBoxes(rig.Sandbox.Root + "/proj", "sess-1"));

        rig.Run("posttooluse-ask-record", AskEventIn(rig), services: () => services);

        var row = JsonText.Parse(Assert.Single(File.ReadAllLines(Path.Combine(dir, "asks.jsonl"))));
        Assert.Equal("rec:toolu_1", row.Member("op_id")!.Value.AsString());
        Assert.False(File.Exists(Path.Combine(services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "asks.jsonl")));
        Assert.True(File.Exists(Path.Combine(dir, ".gitignore")));
        Assert.Empty(rig.Errors.Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAskIsWrittenOnceInEachOwnedDirectory(bool includeRepoB)
    {
        var (rig, services, mailbox) = CrossRepoAskRig();
        var dirs = new List<string> { SeedAskTeam(services, mailbox, rig.Sandbox.Root + "/proj", "a") };
        if (includeRepoB)
        {
            dirs.Add(SeedAskTeam(services, mailbox, rig.Sandbox.Root + "/repo-b", "a"));
        }

        rig.Run("posttooluse-ask-record", AskEventIn(rig), services: () => services);

        foreach (var dir in dirs)
        {
            var row = JsonText.Parse(Assert.Single(File.ReadAllLines(Path.Combine(dir, "asks.jsonl"))));
            Assert.Equal("rec:toolu_1", row.Member("op_id")!.Value.AsString());
        }
        Assert.Empty(rig.Errors.Records);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("unrelated-pid")]
    [InlineData("reused-pid")]
    [InlineData("no-start")]
    [InlineData("invalid-pid")]
    [InlineData("no-cwd")]
    [InlineData("other-session")]
    public void ARegistrationWithoutTheRequiredSessionProofContributesNoAskDirectory(string missing)
    {
        var (rig, services, mailbox) = CrossRepoAskRig();
        if (missing is "unrelated-pid" or "reused-pid" or "no-start" or "invalid-pid")
        {
            SeedAskTeam(services, mailbox, rig.Sandbox.Root + "/repo-b", "held", bound: false, registeredCwd: "");
        }
        var dir = SeedAskTeam(services, mailbox, rig.Sandbox.Root + "/repo-b", "b",
            bound: missing != "unbound",
            pid: missing == "unrelated-pid" ? 4243 : AskProcessTable.Owner,
            start: missing == "no-start" ? null : missing == "reused-pid" ? "Sun Oct 11 09:00:00 2026" : AskProcessTable.Start,
            session: missing == "other-session" ? "someone-else" : "sess-1",
            registeredCwd: missing == "no-cwd" ? "" : null);
        if (missing == "invalid-pid")
        {
            var registration = mailbox.RegistrationFile("b");
            var row = JsonText.Parse(File.ReadAllText(registration)).AsObject();
            row["pid"] = "4242";
            File.WriteAllText(registration, JsonText.Stringify(row, 0));
        }

        rig.Run("posttooluse-ask-record", AskEventIn(rig), services: () => services);

        Assert.False(File.Exists(Path.Combine(dir, "asks.jsonl")));
        Assert.False(File.Exists(Path.Combine(services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "asks.jsonl")));
        Assert.Empty(rig.Errors.Records);
    }

    [Fact]
    public void MainCheckoutAndLinkedWorktreeSharingABoxDirectoryReceiveOnlyOneRow()
    {
        var (rig, services, mailbox) = CrossRepoAskRig();
        var main = rig.Sandbox.Root + "/proj";
        var dir = SeedAskTeam(services, mailbox, main, "a");
        var worktree = rig.Sandbox.Root + "/worktree";
        var gitdir = main + "/.git/worktrees/wt";
        Directory.CreateDirectory(gitdir);
        Directory.CreateDirectory(worktree + "/.claude");
        File.WriteAllText(worktree + "/.git", "gitdir: " + gitdir + "\n");
        File.WriteAllText(gitdir + "/gitdir", worktree + "/.git\n");
        File.WriteAllText(gitdir + "/commondir", "../..\n");
        Directory.CreateSymbolicLink(worktree + "/.claude/ah2", dir);
        SeedAskTeam(services, mailbox, worktree, "b");
        Assert.Equal(dir, services.Config.MainHierarchyDir(worktree));

        rig.Run("posttooluse-ask-record", AskEventIn(rig), services: () => services);

        Assert.Single(File.ReadAllLines(Path.Combine(dir, "asks.jsonl")));
        Assert.Empty(rig.Errors.Records);
    }

    [Fact]
    public void ASessionWithoutAnyRecordedTeamWritesNothing()
    {
        var (rig, services, _) = CrossRepoAskRig();

        rig.Run("posttooluse-ask-record", AskEventIn(rig), services: () => services);

        Assert.False(File.Exists(Path.Combine(services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "asks.jsonl")));
        Assert.Empty(rig.Errors.Records);
    }
}
