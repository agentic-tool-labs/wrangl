using System.Text;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>What counts as live work for an Orchestrator: the question gate may only hold a turn when none of it is in flight.</summary>
public sealed class LiveWorkTests
{
    private const string Session = "sess-1";
    private const string RequestId = "20261011-115900-lw01";

    private sealed record Scenario(HooksHostTests.Rig Rig, string Dir, string Pool)
    {
        public DeciderBox[] Boxes => [new DeciderBox("orchestrator@tm", "tm", Session) { Dir = Dir }];

        public GateContext Context()
        {
            var input = new JsonObject { ["session_id"] = Session, ["cwd"] = Rig.Sandbox.Root + "/proj", ["tool_name"] = "AskUserQuestion" };
            return new GateContext(Rig.Services, JsonText.Parse(Encoding.UTF8.GetBytes(JsonText.Stringify(input, 0))), "x");
        }

        public string? Find() => LiveWork.Find(Context(), Boxes);
    }

    private static Scenario Quiet(Func<string, JsonValue, bool>? memberUp = null, PeerRecordsRoute? peerRoute = null)
    {
        var rig = new HooksHostTests.Rig(memberUp: memberUp, peerRoute: peerRoute);
        _ = Directory.CreateDirectory(rig.Sandbox.Root + "/proj/.git");
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");
        _ = Directory.CreateDirectory(dir + "/msgs");
        _ = Directory.CreateDirectory(dir + "/teams");
        File.WriteAllText(dir + "/teams/tm.json", """{"members":[{"name":"implementor","role":"implementor"}]}""");
        return new Scenario(rig, dir, dir);
    }

    /// <summary>An open request to <c>implementor@tm</c>, dispatched a minute ago, so far inside its eta.</summary>
    private static void Dispatch(Scenario s)
    {
        var path = $"{s.Pool}/msgs/{RequestId}--implementor--t1--request.md";
        File.WriteAllText(
            path,
            $"---\nid: {RequestId}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: t1\nparent: null\nreason: null\neta: large\nto_name: null\nfrom_name: null\nteam: tm\ncreated: 2026-10-11T11:59:00.000Z\n---\n\n## [0] tldr\n- none\n");
        var pending = s.Rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        var row = new JsonObject
        {
            ["type"] = "dispatch",
            ["session_id"] = Session,
            ["request_id"] = RequestId,
            ["to"] = "implementor",
            ["to_addr"] = "implementor@tm",
            ["path"] = path,
            ["created"] = "2026-10-11T11:59:00.000Z",
        };
        File.AppendAllText(pending, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
    }

    [Fact]
    public void AnUnreadablePeerLogCountsAsLiveWork()
    {
        var s = Quiet((_, _) => true, new PeerRecordsRoute(new StopPeerNudgeUnreadableStoreTests.UnreadablePeerLog(), new StopPeerNudgeUnreadableStoreTests.DbMode()));

        Assert.Contains("could not be read", s.Find(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATeamWithNothingInFlightHasNoLiveWork()
    {
        var s = Quiet((_, _) => true);

        Assert.Null(s.Find());
    }

    [Fact]
    public void AnOpenDispatchNotYetPastItsEtaToALiveMemberIsLiveWork()
    {
        var s = Quiet((_, _) => true);
        Dispatch(s);

        Assert.NotNull(s.Find());
    }

    [Fact]
    public void AnOpenDispatchToAMemberWhoseProcessIsGoneIsNotLiveWork()
    {
        var s = Quiet((_, _) => false);
        Dispatch(s);

        Assert.Null(s.Find());
    }

    [Fact]
    public void ADispatchWhoseReportHasLandedIsNotLiveWork()
    {
        var s = Quiet((_, _) => true);
        Dispatch(s);
        File.WriteAllText(
            $"{s.Pool}/msgs/{RequestId}--orchestrator--t1--response.md",
            $"---\nid: {RequestId}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: t1\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: tm\ncreated: 2026-10-11T11:59:30.000Z\n---\n\n## [0] tldr\n- done\n");

        Assert.Null(s.Find());
    }

    [Fact]
    public void AnOpenRunAnchorAloneIsLiveWork()
    {
        var s = Quiet((_, _) => true);
        File.WriteAllText(
            $"{s.Pool}/msgs/{RequestId}--orchestrator--pipeline-run-anchor--request.md",
            $"---\nid: {RequestId}\ntype: request\nto: orchestrator\nfrom: orchestrator\nslug: pipeline-run-anchor\ncreated: 2026-10-11T10:00:00.000Z\n---\n\n## constraints\nbranch: ah/x\n");

        Assert.NotNull(s.Find());
    }
}
