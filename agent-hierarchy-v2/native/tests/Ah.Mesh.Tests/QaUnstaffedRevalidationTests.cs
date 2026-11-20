using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>ah2-334: the QA form of queue-stall-unstaffed is delivered only while its job is still queued; the checklist form is unchanged.</summary>
public sealed class QaUnstaffedRevalidationTests
{
    private static JsonValue Entry(JsonObject data) =>
        JsonValue.FromObject(new JsonObject { ["id"] = "e1", ["event"] = "queue-stall-unstaffed", ["text"] = "ahd alarm: queue-stall-unstaffed", ["data"] = data });

    private static JsonValue QaEntry() => Entry(new JsonObject { ["request_id"] = JsonValue.Null, ["qa_entry"] = "q1", ["repo"] = "/repo", ["sha"] = "abc", ["set"] = "full" });

    private static bool Applies(JsonValue entry, Func<string, bool>? qaQueued) =>
        EventRevalidation.Applies(entry, static (_, _) => false, static _ => false, static _ => false, static _ => false, static _ => false, static _ => true, static _ => true, static _ => true, qaQueued);

    [Fact]
    public void AQueuedJobIsDeliveredAndNegativeAJobNoLongerQueuedIsNot()
    {
        Assert.True(Applies(QaEntry(), id => id == "q1"));
        Assert.False(Applies(QaEntry(), static _ => false));
    }

    [Fact]
    public void NegativeTheChecklistFormIsNotCheckedAgainstTheQaQueue()
    {
        var checklist = Entry(new JsonObject { ["request_id"] = JsonValue.Null, ["item"] = "_run/a", ["role"] = "implementor" });
        Assert.True(Applies(checklist, static _ => false));
    }

    [Fact]
    public void WithNoQaReadTheQaFormStillApplies()
    {
        Assert.True(Applies(QaEntry(), null));
    }
}
