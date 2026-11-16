using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>b331: an idle-capacity alarm is re-checked when it is handed over, naming only the members still idle.</summary>
public sealed class IdleCapacityRevalidationTests
{
    private static JsonValue Alarm(params string[] members)
    {
        var list = new JsonArray();
        foreach (var m in members)
        {
            list.Add(m);
        }

        return JsonValue.FromObject(new JsonObject
        {
            ["id"] = "e1",
            ["event"] = "idle-capacity",
            ["text"] = $"ahd alarm: idle-capacity — {members.Length} members idle 10+ min with no open request: {string.Join(", ", members)}. Ready backlog items nobody holds: b1 X. Run it.",
            ["data"] = new JsonObject { ["request_id"] = JsonValue.Null, ["members"] = list, ["items"] = new JsonArray { "b1" } },
        });
    }

    private static bool Applies(JsonValue entry, Func<string, bool> idle) =>
        EventRevalidation.Applies(entry, static (_, _) => false, static _ => false, static _ => false, static _ => false, static _ => false, static _ => true, static _ => true, idle);

    [Fact]
    public void TheMembersNoLongerIdleAreLeftOutAndNoneLeftDropsTheAlarm()
    {
        var alarm = Alarm("tm-a", "tm-b", "tm-c");
        Func<string, bool> onlyC = static m => m == "tm-c";

        var shown = EventRevalidation.Shown(alarm, onlyC);

        Assert.True(Applies(alarm, onlyC));
        Assert.Contains("1 member idle 10+ min with no open request: tm-c. Ready backlog items", shown.Member("text")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Equal(["tm-c"], shown.Member("data")!.Value.Member("members")!.Value.AsArray().Select(static m => m.AsString()));
        Assert.False(Applies(alarm, static _ => false));
    }

    [Fact]
    public void NegativeAllMembersStillIdleLeaveTheAlarmAsItWas()
    {
        var alarm = Alarm("tm-a", "tm-b");

        Assert.True(Applies(alarm, static _ => true));
        Assert.Same(alarm.AsObject(), EventRevalidation.Shown(alarm, static _ => true).AsObject());
    }
}
