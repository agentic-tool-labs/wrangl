using System.Globalization;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary>
/// ah2-204: a Claude session above an owner verb that has no session-pids row gets the row SessionStart writes, under its walked harness
/// pid, and is proved again. The fake walk finds a session once that row exists, as the real one does.
/// </summary>
public sealed partial class RosterOwnerGuardTests
{
    /// <summary>The session ids the store's session-pids rows record for <paramref name="pid"/> under the test's hierarchy dir.</summary>
    private IReadOnlyList<string> RowSessions(int pid) =>
        [.. StoreGraph().Hierarchy.Status.SessionsOfPid(Hierarchy, pid, DateTimeOffset.MinValue).Select(r => r.SessionId)];

    private void UnregisteredSession(string? session = "s-late")
    {
        _nearest = new NearestSession(NearestKind.None);
        _harness = Environment.ProcessId;
        _env["CLAUDE_CODE_SESSION_ID"] = session;
    }

    [Fact]
    public void AnUnregisteredSessionWithNoTeamGetsItsRowAndItsSpawnProceedsAsTheOwner()
    {
        UnregisteredSession();

        // the fixture cannot launch, so the spawn ends after the guard; what it must not end on is the owner proof
        var (_, stdout, stderr) = Run("spawn-ad-hoc", "architect", "--model", "sonnet");

        Assert.Null(Refusal(stdout).Refused);
        Assert.DoesNotContain("could not prove", stderr, StringComparison.Ordinal);
        Assert.Equal(["s-late"], RowSessions(Environment.ProcessId));
    }

    [Fact]
    public void NegativeACommandUnderAMemberIsStillRefusedAndWritesNoRow()
    {
        UnregisteredSession();
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);

        var (code, stdout, _) = Run("spawn-ad-hoc", "architect");

        Assert.Equal(2, code);
        Assert.Equal(("member-cannot-spawn-ad-hoc", "member-ancestor"), Refusal(stdout));
        Assert.Empty(RowSessions(Environment.ProcessId));
    }

    [Fact]
    public void NegativeAnAlreadyProvedSessionWritesNoRow()
    {
        UnregisteredSession();
        _nearest = new NearestSession(NearestKind.Session, null, null, Environment.ProcessId);

        var (_, stdout, _) = Run("spawn-ad-hoc", "architect", "--model", "sonnet");

        Assert.Null(Refusal(stdout).Refused);
        Assert.Empty(RowSessions(Environment.ProcessId));
    }

    [Fact]
    public void NegativeNoSessionIdIsStillRefusedNamingCheckinAndWritesNoRow()
    {
        UnregisteredSession(session: null);

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "architect");

        Assert.Equal(2, code);
        Assert.Equal(("owner-unproven", "no-orchestrator"), Refusal(stdout));
        Assert.Contains("Run `ah roster checkin` in the Orchestrator's Claude session and retry; if that is refused too, start a new Claude session.", stderr, StringComparison.Ordinal);
        Assert.Empty(RowSessions(Environment.ProcessId));
    }

    [Fact]
    public void NegativeNoClaudeProcessAboveTheCommandIsRefusedSayingSo()
    {
        UnregisteredSession();
        _harness = null;

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "architect");

        Assert.Equal(2, code);
        Assert.Equal(("owner-unproven", "no-orchestrator"), Refusal(stdout));
        Assert.Contains("ah roster spawn-ad-hoc: No Claude session is above this command. Run it inside the Orchestrator's Claude session.", stderr, StringComparison.Ordinal);
        Assert.Empty(RowSessions(Environment.ProcessId));
    }

    [Fact]
    public void AnUnregisteredSessionAndATeamAnotherLiveSessionOwnsIsRefusedAsOtherTeam()
    {
        TeamOwnedBy(LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture));
        var before = StoredTeam(Path.Combine(Hierarchy, "teams", "tm.json"));
        UnregisteredSession();

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "architect", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(("not-team-owner", "other-team"), Refusal(stdout));
        Assert.Contains("Pass --team <name>", stderr, StringComparison.Ordinal);
        Assert.Equal(before, StoredTeam(Path.Combine(Hierarchy, "teams", "tm.json")));
    }

    [Fact]
    public void A1AShellWithATeamFileMarkerIsNeverRegistered()
    {
        UnregisteredSession();
        _env["WRANGL_TEAM_FILE"] = Path.Combine(Hierarchy, "teams", "tm.json");

        var (code, stdout, _) = Run("spawn-ad-hoc", "architect", "--model", "sonnet");

        Assert.Equal(2, code);
        Assert.Equal(("owner-unproven", "no-orchestrator"), Refusal(stdout));
        Assert.Empty(RowSessions(Environment.ProcessId));
    }

    [Fact]
    public void A2ASessionIdALiveSessionHoldsIsNotTakenAndItsTeamIsUnchanged()
    {
        var owner = LivePidOfAnotherProcess();
        var ownerText = owner.ToString(CultureInfo.InvariantCulture);
        _sandbox.PlantTeam(
            Path.Combine(Hierarchy, "teams", "tm.json"),
            "{\"version\":1,\"team_id\":\"t-tm\",\"transport\":\"terminal\",\"roster_level\":\"repo\",\"orchestrator\":{\"session_id\":\"s-owner\",\"pid\":" + ownerText + StartPart(ownerText) + "},\"members\":[],\"streams\":{}}");
        StoreGraph().Hierarchy.Status.AppendSessionPid(Hierarchy, JsonValue.FromString("s-owner"), JsonValue.FromNumber(owner));
        var before = StoredTeam(Path.Combine(Hierarchy, "teams", "tm.json"));
        UnregisteredSession(session: "s-owner");

        var (code, stdout, _) = Run("spawn-one", "architect", "--team", "tm");

        Assert.Equal(2, code);
        Assert.Equal(("owner-unproven", "no-orchestrator"), Refusal(stdout));
        Assert.Equal(before, StoredTeam(Path.Combine(Hierarchy, "teams", "tm.json")));
        Assert.Empty(RowSessions(Environment.ProcessId));
        Assert.Equal(["s-owner"], RowSessions(owner));
    }

    [Fact]
    public void A3AClaudePidFurtherUpTheLineIsTheOneRegisteredAndTheVerbProceeds()
    {
        UnregisteredSession();
        var further = int.Parse(ObserverParent, CultureInfo.InvariantCulture);
        _harnessLine.Add(further);
        _env["CLAUDE_PID"] = ObserverParent;

        var (_, stdout, stderr) = Run("spawn-ad-hoc", "architect", "--model", "sonnet");

        Assert.Null(Refusal(stdout).Refused);
        Assert.DoesNotContain("could not prove", stderr, StringComparison.Ordinal);
        Assert.Equal(["s-late"], RowSessions(further));
        Assert.Empty(RowSessions(Environment.ProcessId));
    }

    [Fact]
    public void A3NegativeAClaudePidOffTheAncestorLineRefusesSayingSoAndWritesNoRow()
    {
        UnregisteredSession();
        _env["CLAUDE_PID"] = "424242";

        var (code, stdout, stderr) = Run("spawn-ad-hoc", "architect", "--model", "sonnet");

        Assert.Equal(2, code);
        Assert.Equal(("owner-unproven", "no-orchestrator"), Refusal(stdout));
        Assert.Contains("CLAUDE_PID names pid 424242, which is not a process above this command", stderr, StringComparison.Ordinal);
        Assert.Empty(RowSessions(Environment.ProcessId));
        Assert.Empty(RowSessions(424242));
    }
}
