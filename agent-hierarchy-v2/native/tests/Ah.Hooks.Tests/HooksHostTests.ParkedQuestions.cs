using System.Text;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    private static string ProjOf(Rig rig) => rig.Sandbox.Root + "/proj";

    private static JsonObject Q(string question, string header, params string[] labels)
    {
        var options = new JsonArray();
        foreach (var label in labels)
        {
            options.Add(new JsonObject { ["label"] = label, ["description"] = "d" });
        }

        return new JsonObject { ["question"] = question, ["header"] = header, ["multiSelect"] = false, ["options"] = options };
    }

    private static List<JsonValue> ParkedWrites(Rig rig) =>
        rig.Writer.Arguments.Select(a => JsonText.Parse(a))
            .Where(a => a.Member("path")!.Value.AsString().EndsWith("/parked-questions.jsonl", StringComparison.Ordinal))
            .Select(a => a.Member("row")!.Value)
            .ToList();

    private static string StoreDir(Rig rig) => rig.Services.Config.HierarchyDir(ProjOf(rig));

    private static string StoreLine(JsonObject line) => Encoding.UTF8.GetString(JsonText.ToUtf8(line, 0)) + "\n";

    private static JsonObject ParkedAs(string id, string session) =>
        Ah.Hierarchy.ParkedQuestions.ParkedLine(id, "2026-10-11T11:00:00.000Z", "tm", session, "toolu_0", 0, Q("Alarm?", "Alarm", "yes", "no"), "ah:R8", "reserved");

    private static void Seed(Rig rig, params JsonObject[] lines)
    {
        _ = Directory.CreateDirectory(StoreDir(rig));
        File.WriteAllText(Ah.Hierarchy.ParkedQuestions.PathIn(StoreDir(rig)), string.Concat(lines.Select(StoreLine)));
    }

    private static string PushEvent(Rig rig) =>
        JsonText.Stringify(
            new JsonObject { ["hook_event_name"] = "PostToolUse", ["session_id"] = "sess-1", ["cwd"] = ProjOf(rig), ["tool_use_id"] = "toolu_9", ["tool_name"] = "PushNotification", ["tool_input"] = new JsonObject { ["message"] = "m" } },
            0);

    [Fact]
    public void APushMarksTheSessionsOpenUnpushedQuestionsPushedAndNoOthers()
    {
        var rig = new Rig();
        Seed(
            rig,
            ParkedAs("q1", "sess-1"),
            ParkedAs("q2", "sess-other"),
            ParkedAs("q3", "sess-1"),
            Ah.Hierarchy.ParkedQuestions.AnsweredLine("q3", "2026-10-11T11:30:00.000Z", ["yes"], "decision-answer", "ans:q3"),
            ParkedAs("q4", "sess-1"),
            Ah.Hierarchy.ParkedQuestions.PushedLine("q4", "2026-10-11T11:10:00.000Z"));

        _ = rig.Run("posttooluse-push-record", PushEvent(rig), services: AsOrchestrator(rig));

        Assert.Equal(["q1"], ParkedWrites(rig).Select(p => p.Member("id")!.Value.AsString()));
        Assert.Equal("pushed", ParkedWrites(rig)[0].Member("kind")!.Value.AsString());
        Assert.Contains("pushed:q1:toolu_9", rig.Writer.OperationIds);
    }

    [Fact]
    public void APushFromASessionThatIsNoTeamsOrchestratorWritesNothing()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        _ = rig.Run("posttooluse-push-record", PushEvent(rig), services: AsOrchestrator(rig, recorded: false));

        Assert.Empty(rig.Writer.Calls);
    }

    private static string? Nudge(Rig rig) =>
        Ah.Hooks.Lifecycle.ParkedPushNudge.Notice(
            new Ah.Hooks.Plumbing.GateContext(AsOrchestrator(rig)(), JsonText.Parse(Encoding.UTF8.GetBytes(PushEvent(rig))), "x"),
            AsOrchestrator(rig)().Advisory!,
            "sess-1");

    [Fact]
    public void AnOpenUnpushedQuestionOwesOnePushNudgeAndRecordsThatItWasSaid()
    {
        var rig = new Rig();
        Seed(rig, ParkedAs("q1", "sess-1"));

        var notice = Nudge(rig);

        Assert.StartsWith("q1 is parked but not pushed. Send: PushNotification \"Alarm: Alarm? Options: yes | no.", notice, StringComparison.Ordinal);
        var gate = JsonText.Parse(Assert.Single(rig.Writer.Arguments, a => a.Contains("parked-push-nudge", StringComparison.Ordinal)));
        Assert.Equal("q1", gate.Member("rec")!.Value.Member("key")!.Value.AsString());
    }

    [Fact]
    public void ANudgeAlreadySaidPushedAnsweredOrAnotherSessionsQuestionOwesNothing()
    {
        var said = new Rig();
        Seed(said, ParkedAs("q1", "sess-1"));
        said.PutGates(StoreDir(said), StoreLine(new JsonObject { ["type"] = "parked-push-nudge", ["session_id"] = "sess-1", ["key"] = "q1" }));
        var pushed = new Rig();
        Seed(pushed, ParkedAs("q1", "sess-1"), Ah.Hierarchy.ParkedQuestions.PushedLine("q1", "2026-10-11T11:10:00.000Z"));
        var answered = new Rig();
        Seed(answered, ParkedAs("q1", "sess-1"), Ah.Hierarchy.ParkedQuestions.AnsweredLine("q1", "2026-10-11T11:30:00.000Z", ["yes"], "decision-answer", "ans:q1"));
        var other = new Rig();
        Seed(other, ParkedAs("q1", "sess-other"));

        Assert.Null(Nudge(said));
        Assert.Null(Nudge(pushed));
        Assert.Null(Nudge(answered));
        Assert.Null(Nudge(other));
        Assert.Null(Nudge(new Rig()));
    }
}
