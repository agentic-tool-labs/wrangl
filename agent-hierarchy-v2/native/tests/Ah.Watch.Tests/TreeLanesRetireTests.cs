using System.Globalization;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>Which teams the daemon retires (ah-ui-focus §6), from the composed read model.</summary>
public sealed partial class TreeLanesTests
{
    private const string OtherPath = Canon + "/teams/other.json";

    private static readonly long Days = DateTimeOffset.Parse("2026-06-05T00:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    /// <summary>The ah2-tm3 case: dev is gone, and "so" is alive and leads another team whose member runs.</summary>
    private static TreeReader OldTeamWhoseOrchestratorMovedOn()
    {
        var reader = Truth();
        reader.Teams[OtherPath] = Doc("so", "impl");
        reader.Registrations["s-impl"] = (Reg("s-impl", "impl@other", 55, "2026-06-01T10:05:00Z"), [OtherPath]);
        reader.Registrations["s-ref"] = (Reg("s-ref", "stranger", 77, "2026-06-01T10:08:00Z"), []);
        reader.LivePids.AddRange([1, 55, 77]);
        return reader;
    }

    private static RetirePass Pass(TreeReader reader, long? nowMs = null, bool recordedDead = false)
    {
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;
        return model.TeamsToRetire(nowMs ?? Days, (_, _) => recordedDead);
    }

    private static string[] Retired(RetirePass pass) => [.. pass.Retire.Select(static c => c.Team)];

    [Fact]
    public void AnOldTeamWhoseOrchestratorLeadsALiveTeamRetiresAndThatTeamDoesNot()
    {
        Assert.Equal(["default"], Retired(Pass(OldTeamWhoseOrchestratorMovedOn())));
    }

    [Fact]
    public void ADeadOrchestratorRetiresItsTeam()
    {
        var reader = Truth();
        reader.Registrations["s-ref"] = (Reg("s-ref", "stranger", 77, "2026-06-01T10:08:00Z"), []);
        reader.LivePids.Add(77);

        Assert.Equal(["default"], Retired(Pass(reader)));
    }

    [Fact]
    public void ALiveOrchestratorWithThisTeamAsItsOnlyTeamIsNotRetired()
    {
        var reader = OldTeamWhoseOrchestratorMovedOn();
        _ = reader.Teams.Remove(OtherPath);

        Assert.Empty(Pass(reader).Retire);
    }

    [Fact]
    public void AnOrchestratorLeadingAnotherTeamWithNobodyLiveDoesNotCount()
    {
        var reader = OldTeamWhoseOrchestratorMovedOn();
        _ = reader.LivePids.RemoveAll(static p => p == 55);

        Assert.Empty(Pass(reader).Retire);
    }

    [Fact]
    public void ALiveMemberKeepsTheTeam()
    {
        var reader = OldTeamWhoseOrchestratorMovedOn();
        reader.LivePids.Add(42);

        Assert.Empty(Pass(reader).Retire);
    }

    [Fact]
    public void AnOpenExchangeToALiveSessionKeepsTheTeamAndOneToADeadMemberDoesNot()
    {
        var toOrchestrator = OldTeamWhoseOrchestratorMovedOn();
        toOrchestrator.Exchanges["a"] = Msg("orchestrator", "developer", null, "dev", "default");
        Assert.Empty(Pass(toOrchestrator).Retire);

        var toDead = OldTeamWhoseOrchestratorMovedOn();
        toDead.Exchanges["a"] = Msg("developer", "orchestrator", "dev", null, "default");
        Assert.Equal(["default"], Retired(Pass(toDead)));
    }

    [Fact]
    public void AWaitingItemDoesNotKeepAnOldTeamButItsDayMustStillPass()
    {
        var reader = OldTeamWhoseOrchestratorMovedOn();
        var plan = new LazyLane(new ProgressLane(new PlanReader(), TestFolds.New, static () => [Canon]), static () => true);
        var (model, clock) = SeededWithActivity(reader, plan: plan);
        using var owner = model;
        model.EmitKeySet(ProgressLane.LaneName);
        Drain(model, clock);

        Assert.True(Num(Summary(model), "waiting_on_you") > 0);
        var last = Assert.Single(model.TeamsToRetire(Days, static (_, _) => false).Retire).LastWorkMs;
        Assert.Empty(model.TeamsToRetire(last + SummaryData.StaleAfterMs - 1, static (_, _) => false).Retire);
    }

    [Fact]
    public void ADayMustPassSinceItsOwnLastWork()
    {
        var last = Assert.Single(Pass(OldTeamWhoseOrchestratorMovedOn()).Retire).LastWorkMs;

        Assert.Empty(Pass(OldTeamWhoseOrchestratorMovedOn(), last + SummaryData.StaleAfterMs - 1).Retire);
        Assert.Single(Pass(OldTeamWhoseOrchestratorMovedOn(), last + SummaryData.StaleAfterMs).Retire);
    }

    [Fact]
    public void AMembersWorkCountsAndTheOrchestratorsAndTheDirsDoNot()
    {
        var quiet = Assert.Single(Pass(OldTeamWhoseOrchestratorMovedOn()).Retire).LastWorkMs;

        var worked = OldTeamWhoseOrchestratorMovedOn();
        worked.Activities["s-dev"] = Act("idle", toolAt: "2026-06-02T00:00:00Z", ctxAt: "2026-06-02T00:00:00Z");
        Assert.True(Assert.Single(Pass(worked).Retire).LastWorkMs > quiet);

        var elsewhere = OldTeamWhoseOrchestratorMovedOn();
        elsewhere.Activities["so"] = Act("working", toolAt: "2026-06-04T23:00:00Z", ctxAt: "2026-06-04T23:00:00Z");
        elsewhere.Decisions[(Canon, "run1")] = ["2026-06-04T23:00:00Z"];
        Assert.Equal(quiet, Assert.Single(Pass(elsewhere).Retire).LastWorkMs);
    }

    /// <summary>A plan with nothing waiting: the fixture plan's waiting item would hold the team.</summary>
    private sealed class FinishedPlanReader : IViewStateReader
    {
        private const string Rows = """[{"ts":"2026-06-02T00:00:00Z","stream":"s","item":"a","event":"add","title":"A","by":"orchestrator@default"}]""";

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) => op switch
        {
            "keys" => JsonText.Parse("""{"teams":["_default"]}"""),
            "streams" => JsonText.Parse("""{"streams":[]}"""),
            _ => JsonText.Parse("{\"streams\":[{\"stream\":\"s\",\"rows\":" + Rows + ",\"offset\":1,\"length\":1}]}"),
        };
    }

    [Fact]
    public void RetiringAndTheSummaryReadOneLastWorkDateAndThePlanMovesIt()
    {
        var quiet = Assert.Single(Pass(OldTeamWhoseOrchestratorMovedOn()).Retire).LastWorkMs;

        var plan = new LazyLane(new ProgressLane(new FinishedPlanReader(), TestFolds.New, static () => [Canon]), static () => true);
        var (model, clock) = SeededWithActivity(OldTeamWhoseOrchestratorMovedOn(), plan: plan);
        using var owner = model;
        model.EmitKeySet(ProgressLane.LaneName);
        Drain(model, clock);

        var lastWork = Assert.Single(model.TeamsToRetire(Days, static (_, _) => false).Retire).LastWorkMs;
        Assert.True(lastWork > quiet);
        Assert.Equal(lastWork, LaneParts.Ms(Text(Summary(model), "updated_at")));
    }

    [Fact]
    public void ATeamWithAMarkerIsLeftAlone()
    {
        var reader = OldTeamWhoseOrchestratorMovedOn();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa").TrimEnd('}') + ",\"closed\":{\"at\":\"2026-06-03T00:00:00.000Z\",\"by\":\"ui\"}}";

        Assert.Empty(Pass(reader).Retire);
    }

    [Fact]
    public void AReopenHoldsUntilNewWorkAndThenADayIdle()
    {
        static string Reopened(string at) => Doc("so", "dev", "qa").TrimEnd('}') + ",\"reopened\":{\"at\":\"" + at + "\",\"by\":\"ui\"}}";

        var untouched = OldTeamWhoseOrchestratorMovedOn();
        untouched.Teams[TeamPath] = Reopened("2026-06-03T00:00:00.000Z");
        Assert.Empty(Pass(untouched, Days + (30 * 86_400_000L)).Retire);

        var workedSince = OldTeamWhoseOrchestratorMovedOn();
        workedSince.Teams[TeamPath] = Reopened("2026-06-01T23:00:00.000Z");
        workedSince.Activities["s-dev"] = Act("idle", toolAt: "2026-06-02T00:00:00Z", ctxAt: "2026-06-02T00:00:00Z");
        Assert.Equal(["default"], Retired(Pass(workedSince)));
    }

    [Fact]
    public void ATeamFileThatDoesNotReadIsReportedAndOthersStillRetire()
    {
        var reader = OldTeamWhoseOrchestratorMovedOn();
        var bad = Canon + "/teams/bad.json";
        reader.Teams[bad] = "[]";

        var pass = Pass(reader);

        Assert.Equal(bad, Assert.Single(pass.Unreadable));
        Assert.Equal(["default"], Retired(pass));
    }

    [Fact]
    public void AnUndecidedOrchestratorCountsAsDeadOnlyWhenItsRecordedProcessIsGone()
    {
        var reader = Truth();
        // nothing decides "so": no reference process was seen, so its death is not proven
        Assert.Empty(Pass(reader).Retire);
        Assert.Equal(["default"], Retired(Pass(reader, recordedDead: true)));
    }

    [Fact]
    public void AnOldProcessesReadingSaysNothing()
    {
        var (fresh, _) = SeededWithActivity(OldTeamWhoseOrchestratorMovedOn());
        using var freshOwner = fresh;
        Assert.NotEmpty(fresh.TeamsToRetire(Days, static (_, _) => false).Retire);

        var (old, _) = SeededWithActivity(OldTeamWhoseOrchestratorMovedOn(), wallClockMs: Days);
        using var oldOwner = old;
        Assert.Empty(old.TeamsToRetire(Days, static (_, _) => false).Retire);
    }

    [Fact]
    public void TheLazyLanesMustBeCurrentBeforeAPassIsBelieved()
    {
        var (model, clock) = SeededWithActivity(OldTeamWhoseOrchestratorMovedOn());
        var connected = false;
        var lane = new LazyLane(new ProgressLane(new PlanReader(), TestFolds.New, static () => [Canon]), () => connected);
        using var lazy = new LazyAwareModel(model, [lane], []);
        Assert.Empty(WatchModule.TeamsToRetire(lazy, Days, static (_, _) => false).Retire);

        _ = lazy.Manifest("summary");
        using var poll = model.Scheduler.BeginPoll();
        clock.Now += LazyAwareModel.SettleMs - 1;
        Assert.Empty(WatchModule.TeamsToRetire(lazy, Days, static (_, _) => false).Retire);

        clock.Now += 1;
        Assert.Single(WatchModule.TeamsToRetire(lazy, Days, static (_, _) => false).Retire);

        // a key left dirty while nothing read it: back to saying nothing, until it is read
        var key = ProgressLane.KeyOf(Canon, "default");
        _ = lane.Reread(key, 0);
        Assert.NotEmpty(lane.DirtyKeys);
        Assert.Empty(WatchModule.TeamsToRetire(lazy, Days, static (_, _) => false).Retire);

        connected = true;
        _ = lane.Reread(key, 0);
        Assert.Single(WatchModule.TeamsToRetire(lazy, Days, static (_, _) => false).Retire);
    }
}
