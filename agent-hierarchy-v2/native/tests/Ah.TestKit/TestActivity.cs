using Ah.Abstractions;
using Ah.Core.Json;
using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>Seeds activity records (per-dir-files-to-db S2: the <c>activity</c> table is their only home) through the domain's <c>activity.put</c>.</summary>
public static class TestActivity
{
    /// <summary>Stores <paramref name="body"/> as the record of <paramref name="subject"/> under <paramref name="dir"/> in <paramref name="ahHome"/>'s database, written at <paramref name="updatedAtMs"/> (now when null).</summary>
    public static void Put(IReadOnlyList<IStateDomain> domains, string ahHome, string dir, string subject, string body, long? updatedAtMs = null)
    {
        var args = new JsonObject { ["dir"] = dir, ["subject"] = subject, ["body"] = body };
        if (updatedAtMs is { } at)
        {
            args["updated_at_ms"] = at;
        }

        var put = domains.Single(d => d.Name == "activity").Ops.Single(o => o.Name == "put").DbBody!;
        var call = new OpCall("put", JsonText.ToUtf8(args, 0), new RequestContext(dir, dir, string.Empty, string.Empty), Guid.NewGuid().ToString());
        var outcome = StateDbModule.CreateOpRunner(ahHome).Run(new DbOpRequest("activity", call.OperationId!, "direct", tx => put(call, tx), DbOnly: true));
        if (outcome.Status != DbOpStatus.Done)
        {
            throw new InvalidOperationException($"activity.put: {outcome.Error}");
        }
    }
}
