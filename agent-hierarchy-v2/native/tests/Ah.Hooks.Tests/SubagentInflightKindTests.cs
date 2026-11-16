using System.Text;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>The in-flight recorder says whether a record is a subagent or a background task, and a stop keeps what its start said.</summary>
public sealed class SubagentInflightKindTests
{
    private const string Session = "sess-1";

    private static void Append(HooksHostTests.Rig rig, JsonObject row)
    {
        var path = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");
    }

    private static HooksHostTests.Rig PeerOwing()
    {
        var rig = new HooksHostTests.Rig();
        Append(rig, new JsonObject
        {
            ["session_id"] = Session,
            ["from"] = "orchestrator@tm",
            ["from_name"] = "orchestrator",
            ["reply_to"] = JsonValue.Null,
            ["task"] = "20260101-000000-aa11",
            ["status"] = "pending",
            ["nudges"] = 0,
            ["msg"] = "/h/msgs/x.md",
            ["ts"] = "2026-01-01T00:00:00.000Z",
        });
        return rig;
    }

    private static void Started(HooksHostTests.Rig rig, string id, string? kind)
    {
        var row = new JsonObject { ["type"] = "subagent", ["session_id"] = Session, ["agent_id"] = id, ["status"] = "started", ["ts"] = "2026-01-01T00:00:01.000Z" };
        if (kind is not null)
        {
            row["kind"] = kind;
        }

        Append(rig, row);
    }

    private static void Fire(HooksHostTests.Rig rig, JsonObject e)
    {
        e["session_id"] = Session;
        e["cwd"] = rig.Sandbox.Root + "/proj";
        _ = rig.Run("subagent-inflight", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)));
    }

    private static List<JsonObject> Written(HooksHostTests.Rig rig) =>
        [.. rig.Writer.Calls.Select((c, i) => (c, i)).Where(static x => x.c == "peer-records.append").Select(x => JsonText.Parse(Encoding.UTF8.GetBytes(rig.Writer.Arguments[x.i])).AsObject()["rec"].AsObject())];

    private static JsonObject Bash(string taskId, bool background) => new()
    {
        ["hook_event_name"] = "PostToolUse",
        ["tool_name"] = "Bash",
        ["tool_input"] = new JsonObject { ["run_in_background"] = background },
        ["tool_response"] = new JsonObject { ["backgroundTaskId"] = taskId },
    };

    [Fact]
    public void ASubagentStartIsRecordedWithKindSubagent()
    {
        var rig = PeerOwing();

        Fire(rig, new JsonObject { ["hook_event_name"] = "SubagentStart", ["agent_id"] = "a1" });

        var row = Assert.Single(Written(rig));
        Assert.Equal(("a1", "started", "subagent"), (row["agent_id"].AsString(), row["status"].AsString(), row["kind"].AsString()));
    }

    [Fact]
    public void ABackgroundBashAndAMonitorAreRecordedWithKindBackground()
    {
        var rig = PeerOwing();

        Fire(rig, Bash("b1", true));
        Fire(rig, new JsonObject { ["hook_event_name"] = "PostToolUse", ["tool_name"] = "Monitor", ["tool_input"] = new JsonObject(), ["tool_response"] = new JsonObject { ["taskId"] = "m1" } });

        var rows = Written(rig);
        Assert.Equal(["b1", "m1"], rows.Select(static r => r["agent_id"].AsString()));
        Assert.All(rows, static r => Assert.Equal("background", r["kind"].AsString()));
    }

    [Fact]
    public void NegativeABashThatIsNotInTheBackgroundLeavesNoRecord()
    {
        var rig = PeerOwing();

        Fire(rig, Bash("b2", false));

        Assert.Empty(Written(rig));
    }

    [Fact]
    public void AStopKeepsTheKindItsStartRecordedForASubagentAndForABackgroundTask()
    {
        var rig = PeerOwing();
        Started(rig, "a1", "subagent");
        Started(rig, "b1", "background");

        Fire(rig, new JsonObject { ["hook_event_name"] = "SubagentStop", ["agent_id"] = "a1" });
        Fire(rig, new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["prompt"] = "<task-notification><task-id>b1</task-id><status>completed</status></task-notification>" });

        var rows = Written(rig);
        Assert.Equal([("a1", "stopped", "subagent"), ("b1", "stopped", "background")], rows.Select(static r => (r["agent_id"].AsString(), r["status"].AsString(), r["kind"].AsString())));
    }

    [Fact]
    public void NegativeAStopOfARecordWrittenWithoutAKindHasNoKindEither()
    {
        var rig = PeerOwing();
        Started(rig, "old", null);

        Fire(rig, new JsonObject { ["hook_event_name"] = "SubagentStop", ["agent_id"] = "old" });

        var row = Assert.Single(Written(rig));
        Assert.Equal("stopped", row["status"].AsString());
        Assert.False(row.ContainsKey("kind"));
    }

    [Fact]
    public void ASubagentStartRecordsTheAgentTypeItsPayloadNames()
    {
        var rig = PeerOwing();

        Fire(rig, new JsonObject { ["hook_event_name"] = "SubagentStart", ["agent_id"] = "a1", ["agent_type"] = "task-gopher:task-gopher" });

        var row = Assert.Single(Written(rig));
        Assert.Equal("task-gopher:task-gopher", row["agent_type"].AsString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NegativeAStartWhosePayloadNamesNoAgentTypeRecordsNone(string? type)
    {
        var rig = PeerOwing();
        var start = new JsonObject { ["hook_event_name"] = "SubagentStart", ["agent_id"] = "a1" };
        if (type is not null)
        {
            start["agent_type"] = type;
        }

        Fire(rig, start);

        var row = Assert.Single(Written(rig));
        Assert.False(row.ContainsKey("agent_type"));
    }

    [Fact]
    public void NegativeABackgroundTaskAndAStopRecordNoAgentType()
    {
        var rig = PeerOwing();
        Started(rig, "a1", "subagent");

        Fire(rig, Bash("b1", true));
        Fire(rig, new JsonObject { ["hook_event_name"] = "SubagentStop", ["agent_id"] = "a1", ["agent_type"] = "task-gopher:task-gopher" });

        Assert.All(Written(rig), static r => Assert.False(r.ContainsKey("agent_type")));
    }
}
