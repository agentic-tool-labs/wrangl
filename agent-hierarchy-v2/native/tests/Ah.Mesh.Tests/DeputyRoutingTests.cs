using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Mesh.Tests;

/// <summary>S2: what the on-duty deputy cannot handle reaches the chief at <c>chief@T</c>, and only urgent items there ring.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class DeputyRoutingTests
{
    private const double T0 = MailDoorbellTests.T0;

    private sealed class Liveness(bool live) : IMemberLiveness
    {
        public MemberRegistrationLiveness? ForMember(string memberName, string team) => memberName == "tm-dep" ? new("dep-s", live, "test") : null;

        public bool PidIdentityLive(int pid, string? recordedStart) => true;
    }

    private const string OnDuty = "{\"members\":[{\"name\":\"tm-dep\",\"role\":\"deputy-chief\"}],\"orchestrator\":{\"pid\":1,\"session_id\":\"chief-s\"},\"duty\":{\"deputy\":\"tm-dep\",\"since\":\"2020-01-01T00:00:00.000Z\"}}\n";

    private static (MailDoorbellTests.Rig Rig, string Hier) Make(bool live)
    {
        var rig = new MailDoorbellTests.Rig();
        Directory.CreateDirectory(rig.Root + "/h/teams");
        File.WriteAllText(rig.Root + "/h/teams/tm.json", OnDuty);
        rig.Fx.Hierarchy.Core.Liveness = new Liveness(live);
        return (rig, rig.Root + "/h");
    }

    private static List<JsonValue> Box(MailDoorbellTests.Rig rig, string hier, string name) =>
        [.. rig.Fx.Mailbox.ReadEntries(rig.Fx.Mailbox.BoxDirFor(hier + "/teams/tm.json", name)).Entries];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDeputysUnackedMailGoesToTheChiefAfterTheReRingsOnlyWhileItIsLive(bool live)
    {
        var (rig, hier) = Make(live);
        rig.Add("n1", T0 - 60_000);
        rig.Add("n2", T0 - 59_000);
        _ = rig.Decide(T0, hier: hier);
        for (var i = 1; i <= 3; i++)
        {
            _ = rig.Decide(T0 + (i * 30_000), hier: hier);
        }

        var escalated = rig.Decide(T0 + 120_000, hier: hier);

        Assert.Equal(live, escalated.Escalation is null);
        Assert.Equal(live ? 1 : 0, Box(rig, hier, "chief").Count(e => e.Member("event")?.AsString() == "mail-unacked"));
    }

    [Theory]
    [InlineData("step-needs-user", "chief")]
    [InlineData("member-down", "orchestrator")]
    public void AStepNeedingTheUserGoesStraightToTheChiefAndOtherEventsStayWithTheDeputy(string ev, string box)
    {
        var (rig, hier) = Make(live: true);

        Assert.True(rig.Fx.Mailbox.Send(MeshModule.EventRequest("orchestrator@tm", "ahd", hier, hier, ev, "x")).Ok);

        Assert.Single(Box(rig, hier, box));
    }

    [Fact]
    public void OnDutyAStandardItemInTheChiefBoxDoesNotRingButAnUrgentOneDoes()
    {
        var (rig, hier) = Make(live: true);
        var teamFile = hier + "/teams/tm.json";
        var chief = rig.Fx.Mailbox.BoxDirFor(teamFile, "chief");
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite(MailDoorbellTests.Session, [new BoxRef("chief@tm", chief)], teamFile, "claude", Environment.ProcessId, PidStart: rig.Fx.OwnStart, Bound: true)));
        Directory.CreateDirectory(chief);
        void Put(string id, string kind, string text) => File.AppendAllText(chief + "/entries.jsonl", JsonText.Stringify(new JsonObject
        {
            ["id"] = id, ["kind"] = kind, ["from"] = "tm-dep", ["to"] = "chief@tm", ["created"] = MailDoorbellTests.Rig.Iso(T0 - 60_000), ["text"] = text, ["route"] = "pull",
        }, 0) + "\n");

        Put("1789999940000-a", "report", "30-minute note");
        Assert.Null(rig.Decide(T0, hier: hier).Ring);

        Put("1789999940001-b", "note", "QUESTION: ship the hotfix now? default: wait");
        Assert.NotNull(rig.Decide(T0 + 1, hier: hier).Ring);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnDutyTheSecondWindowCopyRingsTheChiefAndAPlainMemberWaitingDoesNot(bool copy)
    {
        var (rig, hier) = Make(live: true);
        var teamFile = hier + "/teams/tm.json";
        var chief = rig.Fx.Mailbox.BoxDirFor(teamFile, "chief");
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite(MailDoorbellTests.Session, [new BoxRef("chief@tm", chief)], teamFile, "claude", Environment.ProcessId, PidStart: rig.Fx.OwnStart, Bound: true)));
        Directory.CreateDirectory(chief);
        var data = new JsonObject { ["item"] = "_run/a", ["member"] = "impl-1" };
        if (copy)
        {
            data["copy"] = true;
        }

        File.WriteAllText(chief + "/entries.jsonl", JsonText.Stringify(new JsonObject
        {
            ["id"] = "1789999940000-a", ["kind"] = "event", ["event"] = "member-waiting", ["from"] = "ahd", ["to"] = "chief@tm", ["created"] = MailDoorbellTests.Rig.Iso(T0 - 60_000), ["text"] = "waited 20m", ["route"] = "pull", ["data"] = data,
        }, 0) + "\n");

        Assert.Equal(copy, rig.Decide(T0, hier: hier).Ring is not null);
    }
}
