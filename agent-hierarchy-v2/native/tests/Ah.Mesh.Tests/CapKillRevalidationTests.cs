using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>ah2-318 K7: a cap kill reaches the Orchestrator though its owner is gone; the owner's own copy is dropped then.</summary>
public sealed class CapKillRevalidationTests
{
    private static JsonValue Entry(string step, bool ownerCopy)
    {
        var data = new JsonObject { ["request_id"] = "j1", ["job"] = "j1", ["step"] = step, ["member"] = "impl" };
        if (ownerCopy)
        {
            data["owner_copy"] = true;
        }

        return JsonValue.FromObject(new JsonObject { ["id"] = "e1", ["event"] = "job-overdue", ["text"] = "ahd alarm: job-overdue", ["data"] = data });
    }

    private static bool AppliesWithOwnerGone(JsonValue entry) =>
        EventRevalidation.Applies(entry, static (_, _) => false, static _ => false, static _ => false, static _ => false, static _ => true, static _ => true, static _ => true, static _ => true);

    [Fact]
    public void TheOrchestratorsCappedEventAppliesWithTheOwnerGoneAndNegativeTheOwnersCopyAndAnOverdueStepDoNot()
    {
        Assert.True(AppliesWithOwnerGone(Entry("capped", ownerCopy: false)));
        Assert.False(AppliesWithOwnerGone(Entry("capped", ownerCopy: true)));
        Assert.False(AppliesWithOwnerGone(Entry("overdue", ownerCopy: false)));
    }
}
