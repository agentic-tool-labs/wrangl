using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The <c>subagents:&lt;dirId&gt;/&lt;team&gt;</c> slice: a started run under its owner, the Orchestrator's own run, and the runs it leaves out (ah2-226).</summary>
public sealed class SubagentsLaneTests
{
    private const string Dir = "/repo/.claude/ah2";
    private const string TeamFile = Dir + "/teams/tm.json";

    private sealed class Records(Dictionary<(string Lane, string Key), LaneRecord> rows) : IRecordView
    {
        public LaneRecord? Get(string lane, string key) => rows.GetValueOrDefault((lane, key));

        public IReadOnlyList<string> Keys(string lane) => [.. rows.Keys.Where(k => k.Lane == lane).Select(static k => k.Key).Order(StringComparer.Ordinal)];
    }

    private static Records TeamWithImplAndOrchestrator() => new(new()
    {
        [(TeamLane.LaneName, TeamLane.KeyOf(Dir, TeamFile))] = new(JsonValue.FromObject(new JsonObject
        {
            ["members"] = new JsonArray { new JsonObject { ["name"] = "impl" } },
            ["orchestrator"] = new JsonObject { ["session_id"] = "s-orch" },
        }), 1),
        [(RegistrationLane.LaneName, "s-impl")] = new(JsonValue.FromObject(new JsonObject
        {
            ["session_id"] = "s-impl",
            ["address"] = "impl@tm",
            ["team_files"] = new JsonArray { TeamFile },
        }), 1),
    });

    private static List<JsonObject> Rows(IReadOnlyList<SubagentRun> runs)
    {
        var lane = new SubagentsLane(() => [Dir], static _ => true, _ => runs, TeamWithImplAndOrchestrator);
        var change = Assert.Single(lane.Reread(ProgressLane.KeyOf(Dir, "tm"), 0).Changes);
        Assert.Equal("subagents:" + ActivityLane.DirId(Dir) + "/tm", change.Id);
        return [.. JsonText.Parse(change.Body!).Member("rows")!.Value.AsArray().Select(static r => r.AsObject())];
    }

    [Fact]
    public void AStartedRunOfAMemberIsServedUnderThatMemberWithTheInflightBound()
    {
        var row = Assert.Single(Rows([new SubagentRun("s-impl", "a1", "started", "2026-11-18T10:00:00.000Z", null, "Explore")]));

        Assert.Equal("a1", row.Str("agent_id"));
        Assert.Equal("Explore", row.Str("agent_type"));
        Assert.Equal("impl", row.Str("owner"));
        Assert.Equal("member", row.Str("owner_kind"));
        Assert.Equal("2026-11-18T10:00:00.000Z", row.Str("started_at"));
        Assert.Equal(Ah.Hierarchy.HierarchyModule.SubagentInflightTtlMs / 1000, row.Get("ttl_s")!.Value.AsNumber());
    }

    [Fact]
    public void TheOrchestratorsOwnRunReadsOwnerKindOrchestrator()
    {
        var row = Assert.Single(Rows([new SubagentRun("s-orch", "a2", "started", "2026-11-18T10:00:00.000Z", null)]));

        Assert.Equal("orchestrator", row.Str("owner"));
        Assert.Equal("orchestrator", row.Str("owner_kind"));
        Assert.True(row.Get("agent_type")!.Value.IsNull);
    }

    [Fact]
    public void AStoppedRunAndARunOfASessionNotOnTheTeamAreLeftOut()
    {
        Assert.Empty(Rows(
        [
            new SubagentRun("s-impl", "a3", "stopped", "2026-11-18T10:00:00.000Z", "2026-11-18T10:05:00.000Z"),
            new SubagentRun("s-other", "a4", "started", "2026-11-18T10:00:00.000Z", null),
        ]));
    }

    [Fact]
    public void AStartedRunPastTheInflightBoundIsStillARowSoTheClientDropsIt()
    {
        var row = Assert.Single(Rows([new SubagentRun("s-impl", "a5", "lapsed", "2026-11-18T08:00:00.000Z", null)]));

        Assert.Equal("a5", row.Str("agent_id"));
    }

    [Fact]
    public void AnUnheldSliceIsEvictedAndItsRunsAreNotRead()
    {
        var reads = 0;
        var lane = new SubagentsLane(() => [Dir], static _ => false, _ =>
        {
            reads++;
            return [];
        }, TeamWithImplAndOrchestrator);

        var result = lane.Reread(ProgressLane.KeyOf(Dir, "tm"), 0);

        Assert.Empty(result.Changes);
        Assert.Equal(["subagents:" + ActivityLane.DirId(Dir) + "/tm"], result.Evict);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void HeldRunsAreReadAgainOnlyWhenThePeerRecordsCounterMoves()
    {
        var version = 1L;
        var reads = 0;
        var lane = new SubagentsLane(() => [Dir], static _ => true, _ =>
        {
            reads++;
            return [];
        }, TeamWithImplAndOrchestrator, () => version);
        var key = ProgressLane.KeyOf(Dir, "tm");

        _ = lane.Reread(key, 0);
        _ = lane.Reread(key, 0);
        Assert.Equal(1, reads);

        version = 2;
        _ = lane.Reread(key, 0);
        Assert.Equal(2, reads);
    }
}
