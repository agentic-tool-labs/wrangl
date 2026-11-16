using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// A deputy on duty: it owns the team while its row is live and holds <c>orchestrator@T</c>, the chief (still the recorded owner)
/// holds <c>chief@T</c>, and a dead deputy leaves the chief the only owner again with nothing written.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class DeputyDutyTests
{
    private const int GonePid = 2147483000;
    private const string Deputy = "tm-deputy-chief";
    private const string DeputySession = "dep-s";
    private const string ChiefSession = "chief-s";

    private readonly MeshFixture _fx;
    private readonly string _project;
    private readonly string _teamFile;

    public DeputyDutyTests()
    {
        var sandbox = UnitSandbox.Create();
        _project = sandbox.Root + "/proj";
        Directory.CreateDirectory(_project);
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah", ["AH_TEST"] = "1" };
        _fx = MeshFixture.Create(vars, DateTimeOffset.Parse("2026-03-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture), _project);
        _fx.Hierarchy.Core.Liveness = MeshModule.CreateMemberLiveness(_fx.Mailbox, _fx.Table, PlatformModule.CreateIo(null).Processes);
        _teamFile = _fx.Roster.TeamPath(_fx.Callers.RequestContext(_project).HierarchyDirectory, "tm");
        Directory.CreateDirectory(Path.GetDirectoryName(_teamFile)!);
    }

    private static int Self => Environment.ProcessId;

    /// <summary>Plants team tm: its recorded chief at <paramref name="chiefPid"/>, a deputy-chief row at <paramref name="deputyPid"/>, and <c>duty</c> when on.</summary>
    private void Team(int chiefPid, int deputyPid, bool onDuty = true, string role = "deputy-chief")
    {
        var start = _fx.OwnStart!;
        var team = new JsonObject
        {
            ["orchestrator"] = new JsonObject { ["session_id"] = ChiefSession, ["pid"] = chiefPid, ["pid_start"] = chiefPid == Self ? start : "Mon Jan  1 00:00:00 2001" },
            ["members"] = new JsonArray
            {
                new JsonObject { ["name"] = Deputy, ["role"] = role, ["session_id"] = DeputySession, ["pid"] = deputyPid, ["pid_start"] = deputyPid == Self ? start : "Mon Jan  1 00:00:00 2001" },
            },
        };
        if (onDuty)
        {
            team["duty"] = new JsonObject { ["deputy"] = Deputy, ["since"] = "2026-03-01T12:00:00.000Z", ["cause"] = "duty on" };
        }

        File.WriteAllText(_teamFile, JsonText.Stringify(team, 0));
    }

    /// <summary>The deputy's registration under its member box, at <paramref name="pid"/>: live for this process, dead for a gone pid.</summary>
    private void RegisterDeputy(int pid) =>
        Assert.True(_fx.Mailbox.WriteRegistration(new RegistrationWrite(
            DeputySession, [new BoxRef($"{Deputy}@tm", _fx.Mailbox.BoxDirFor(_teamFile, Deputy))], _teamFile, "claude", pid, PidStart: pid == Self ? _fx.OwnStart : "Mon Jan  1 00:00:00 2001", Bound: true)));

    private List<string> Boxes(string session) =>
        [.. _fx.Callers.ResolveCaller(new CallerRequest(_project, session, Self, null)).Select(b => b.Address)];

    private bool Owns() => _fx.Roster.IsTeamOwnedBy(JsonText.Parse(File.ReadAllText(_teamFile)), new TeamInvoker(Self, null));

    [Fact]
    public void TheDeputyOnDutyOwnsTheTeamAndHoldsTheOrchestratorBox()
    {
        Team(GonePid, Self);

        Assert.True(Owns());
        Assert.Contains("orchestrator@tm", Boxes(DeputySession));
    }

    [Fact]
    public void NegativeTheSameRowOffDutyOwnsNothing()
    {
        Team(GonePid, Self, onDuty: false);

        Assert.False(Owns());
        Assert.DoesNotContain("orchestrator@tm", Boxes(DeputySession));
    }

    [Fact]
    public void NegativeADutyNamingALiveRowOfAnotherRoleClassOwnsNothing()
    {
        Team(GonePid, Self, role: "implementor");

        Assert.False(Owns());
    }

    [Fact]
    public void NegativeADutyNamingADeadRowOwnsNothing()
    {
        Team(GonePid, GonePid);

        Assert.False(Owns());
    }

    [Fact]
    public void TheChiefHoldsTheChiefBoxWhileTheDeputyIsLive()
    {
        Team(Self, GonePid);
        RegisterDeputy(Self);

        var boxes = Boxes(ChiefSession);

        Assert.Contains("chief@tm", boxes);
        Assert.DoesNotContain("orchestrator@tm", boxes);
    }

    [Fact]
    public void NegativeADeadDeputyLeavesTheChiefTheOrchestratorBoxWithNothingWritten()
    {
        Team(Self, GonePid);
        RegisterDeputy(GonePid);
        var before = File.ReadAllText(_teamFile);

        var boxes = Boxes(ChiefSession);

        Assert.Contains("orchestrator@tm", boxes);
        Assert.DoesNotContain("chief@tm", boxes);
        Assert.True(Owns());
        Assert.Equal(before, File.ReadAllText(_teamFile));
    }

    [Fact]
    public void AfterDutyOffUnackedMailInTheChiefBoxKeepsItTheChiefs()
    {
        Team(Self, GonePid, onDuty: false);
        var dir = _fx.Mailbox.BoxDirFor(_teamFile, "chief");
        Assert.True(_fx.Mailbox.AppendEntry(dir, new JsonObject { ["v"] = 1, ["id"] = "m-left", ["kind"] = "note", ["from"] = "deputy-chief", ["to"] = "chief@tm", ["created"] = "2026-03-01T12:00:00+00:00", ["text"] = "QUESTION: x", ["route"] = "fallback" }).Ok);

        Assert.Equal(["orchestrator@tm", "chief@tm"], Boxes(ChiefSession));

        _ = _fx.Mailbox.AckIds(dir, ["m-left"], "chief");
        Assert.Equal(["orchestrator@tm"], Boxes(ChiefSession));
    }

    [Fact]
    public void NegativeATeamWithNoDutyGivesTheChiefTheOrchestratorBoxAsBefore()
    {
        Team(Self, GonePid, onDuty: false);
        RegisterDeputy(Self);

        Assert.Equal(["orchestrator@tm"], Boxes(ChiefSession));
    }

    /// <summary>A process table of one chain: 10, its parent 20 (started at <see cref="ParentStart"/>), then pid 1.</summary>
    private sealed class ChainTable : IProcessTable
    {
        internal const string ParentStart = "Sun Nov  8 12:00:00 2026";

        public IReadOnlyList<int>? Ancestors(int pid) => pid == 10 ? [20, 1] : pid == 20 ? [1] : null;

        public bool? HasAncestor(int pid, int candidate) => Ancestors(pid)?.Contains(candidate);

        public string? StartTime(long pid) => pid == 20 ? ParentStart : null;
    }

    private static NearestSession Nearest(params string[] addresses) => NearestWithDuty(Deputy, addresses);

    // the team's recorded chief (pid 5) is no ancestor of the walk; only its duty deputy matters here
    private static NearestSession NearestWithDuty(string? deputy, params string[] addresses) =>
        MeshModule.NearestRegisteredSession(new ChainTable(), 10, [new TeamOrchestrator("tm", 5, null, deputy)], [.. addresses.Select(a => new RegisteredSession(20, ChainTable.ParentStart, a))]);

    [Fact]
    public void ADeputyHoldingItsTeamsOrchestratorBoxFirstIsFoundAsItsMemberRow()
    {
        var found = Nearest("orchestrator@tm", $"{Deputy}@tm");

        Assert.Equal((NearestKind.Member, "tm", Deputy), (found.Kind, found.Team, found.Member));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tm-other")]
    public void NegativeAMemberBoxIsNotPreferredWhenTheRecordNamesNoSuchDeputyOnDuty(string? deputy)
    {
        var found = NearestWithDuty(deputy, "orchestrator@tm", $"{Deputy}@tm");

        Assert.Equal((NearestKind.Orchestrator, "tm"), (found.Kind, found.Team));
    }

    [Fact]
    public void NegativeALaneLeadOwningItsChildTeamIsStillFoundAsThatTeamsOrchestrator()
    {
        var found = Nearest("orchestrator@mo-ws1", "mo-so-ws1@mo");

        Assert.Equal((NearestKind.Orchestrator, "mo-ws1"), (found.Kind, found.Team));
    }

    [Fact]
    public void ADeadDeputyLetsTheDoorbellRingTheChiefForTheOrchestratorBoxWithNoRegistrationWrite()
    {
        Team(Self, GonePid);
        RegisterDeputy(GonePid);
        var bell = MeshModule.CreateMailDoorbell(_fx.Mailbox, _fx.Core, _fx.Files, _fx.Environment, _fx.Hooks);
        Assert.True(_fx.Mailbox.WriteRegistration(new RegistrationWrite(ChiefSession, [new BoxRef("chief@tm", _fx.Mailbox.BoxDirFor(_teamFile, "chief"))], _teamFile, "claude", Self, PidStart: _fx.OwnStart, Bound: true)));
        var registration = JsonText.Stringify(_fx.Mailbox.ReadRegistration(ChiefSession)!, 0);
        var box = _fx.Mailbox.BoxDirFor(_teamFile, "orchestrator");
        Directory.CreateDirectory(box);
        File.WriteAllText(box + "/entries.jsonl", JsonText.Stringify(new JsonObject { ["id"] = "1790000000000-a1", ["kind"] = "event", ["event"] = "member-down", ["from"] = "ahd", ["to"] = "orchestrator@tm", ["created"] = "2026-03-01T12:00:00.000Z", ["text"] = "member-down", ["route"] = "pull" }, 0) + "\n");

        var urgent = bell.HasUrgent(ChiefSession, DateTimeOffset.Parse("2026-03-01T12:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds());

        Assert.True(urgent);
        Assert.Equal(registration, JsonText.Stringify(_fx.Mailbox.ReadRegistration(ChiefSession)!, 0));
    }

    [Fact]
    public void NegativeALiveDeputyKeepsTheOrchestratorBoxOffTheChiefsDoorbell()
    {
        Team(Self, GonePid);
        RegisterDeputy(Self);
        var bell = MeshModule.CreateMailDoorbell(_fx.Mailbox, _fx.Core, _fx.Files, _fx.Environment, _fx.Hooks);
        Assert.True(_fx.Mailbox.WriteRegistration(new RegistrationWrite(ChiefSession, [new BoxRef("chief@tm", _fx.Mailbox.BoxDirFor(_teamFile, "chief"))], _teamFile, "claude", Self, PidStart: _fx.OwnStart, Bound: true)));
        var box = _fx.Mailbox.BoxDirFor(_teamFile, "orchestrator");
        Directory.CreateDirectory(box);
        File.WriteAllText(box + "/entries.jsonl", JsonText.Stringify(new JsonObject { ["id"] = "1790000000000-a1", ["kind"] = "event", ["event"] = "member-down", ["from"] = "ahd", ["to"] = "orchestrator@tm", ["created"] = "2026-03-01T12:00:00.000Z", ["text"] = "member-down", ["route"] = "pull" }, 0) + "\n");

        Assert.False(bell.HasUrgent(ChiefSession, DateTimeOffset.Parse("2026-03-01T12:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void AfterDutyOffLateChiefMailRingsTheOwnerHoldingOnlyTheOrchestratorBoxWithNoRegistrationWrite()
    {
        Team(Self, GonePid, onDuty: false);
        var bell = MeshModule.CreateMailDoorbell(_fx.Mailbox, _fx.Core, _fx.Files, _fx.Environment, _fx.Hooks);
        Assert.True(_fx.Mailbox.WriteRegistration(new RegistrationWrite(ChiefSession, [new BoxRef("orchestrator@tm", _fx.Mailbox.BoxDirFor(_teamFile, "orchestrator"))], _teamFile, "claude", Self, PidStart: _fx.OwnStart, Bound: true)));
        Assert.True(_fx.Mailbox.WriteRegistration(new RegistrationWrite("other-s", [new BoxRef("orchestrator@tm", _fx.Mailbox.BoxDirFor(_teamFile, "orchestrator"))], _teamFile, "claude", GonePid, PidStart: "Mon Jan  1 00:00:00 2001", Bound: true)));
        var registration = JsonText.Stringify(_fx.Mailbox.ReadRegistration(ChiefSession)!, 0);
        var box = _fx.Mailbox.BoxDirFor(_teamFile, "chief");
        Directory.CreateDirectory(box);
        File.WriteAllText(box + "/entries.jsonl", JsonText.Stringify(new JsonObject { ["id"] = "1790000000000-a1", ["kind"] = "event", ["event"] = "member-down", ["from"] = "ahd", ["to"] = "chief@tm", ["created"] = "2026-03-01T12:00:00.000Z", ["text"] = "late report", ["route"] = "pull" }, 0) + "\n");
        var now = DateTimeOffset.Parse("2026-03-01T12:00:01.000Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

        Assert.True(bell.HasUrgent(ChiefSession, now));
        Assert.Equal(registration, JsonText.Stringify(_fx.Mailbox.ReadRegistration(ChiefSession)!, 0));
        Assert.False(bell.HasUrgent("other-s", now));
    }

    [Fact]
    public void TheChiefsTallyCountsHandledEscalatedAndWaitingAndIsToldOnlyWhenItChanges()
    {
        Team(Self, GonePid);
        RegisterDeputy(Self);
        var chief = _fx.Mailbox.BoxDirFor(_teamFile, "chief");
        Assert.True(_fx.Mailbox.WriteRegistration(new RegistrationWrite(ChiefSession, [new BoxRef("chief@tm", chief)], _teamFile, "claude", Self, PidStart: _fx.OwnStart, Bound: true)));
        var orchestrator = _fx.Mailbox.BoxDirFor(_teamFile, "orchestrator");
        JsonObject Entry(string id, string to) => new() { ["v"] = 1, ["id"] = id, ["kind"] = "note", ["from"] = "x", ["to"] = to, ["created"] = "2026-03-01T12:00:00+00:00", ["text"] = "hi", ["route"] = "fallback" };
        Assert.True(_fx.Mailbox.AppendEntry(orchestrator, Entry("o-1", "orchestrator@tm")).Ok);
        _ = _fx.Mailbox.AckIds(orchestrator, ["o-1"], "deputy");
        Assert.True(_fx.Mailbox.AppendEntry(chief, Entry("c-1", "chief@tm")).Ok);

        Assert.Equal("Deputy on duty (tm): 1 handled, 1 escalated, 1 waiting on you.", _fx.Mailbox.DeputyTally(ChiefSession));
        Assert.Null(_fx.Mailbox.DeputyTally(ChiefSession));
    }

    [Fact]
    public void TheDoorbellServesAChiefBoxAndNotAPlainMemberBox()
    {
        var bell = MeshModule.CreateMailDoorbell(_fx.Mailbox, _fx.Core, _fx.Files, _fx.Environment, _fx.Hooks);
        Assert.True(_fx.Mailbox.WriteRegistration(new RegistrationWrite(ChiefSession, [new BoxRef("chief@tm", _fx.Mailbox.BoxDirFor(_teamFile, "chief"))], _teamFile, "claude", Self, PidStart: _fx.OwnStart, Bound: true)));
        RegisterDeputy(Self);

        Assert.True(bell.Serves(ChiefSession));
        Assert.False(bell.Serves(DeputySession));
    }
}
