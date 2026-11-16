using Ah.Abstractions;
using Ah.Core.Json;
using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>Seeds gate rows (per-dir-files-to-db S3: the <c>gates</c> table is their only home) through the domain's <c>gates.put</c>.</summary>
public static class TestGates
{
    /// <summary>Appends each JSON line of <paramref name="lines"/> (one row per non-blank line, as a <c>gates.jsonl</c> held them) to <paramref name="dir"/>'s log in <paramref name="ahHome"/>'s database.</summary>
    public static void Put(IReadOnlyList<IStateDomain> domains, string ahHome, string dir, string lines)
    {
        var put = domains.Single(d => d.Name == "gates").Ops.Single(o => o.Name == "put").DbBody!;
        foreach (var line in lines.Split('\n').Where(static l => l.Trim().Length > 0))
        {
            var call = new OpCall("put", JsonText.ToUtf8(new JsonObject { ["dir"] = dir, ["body"] = line.Trim() }, 0), new RequestContext(dir, dir, string.Empty, string.Empty), Guid.NewGuid().ToString());
            var outcome = StateDbModule.CreateOpRunner(ahHome).Run(new DbOpRequest("gates", call.OperationId!, "direct", tx => put(call, tx), DbOnly: true));
            if (outcome.Status != DbOpStatus.Done)
            {
                throw new InvalidOperationException($"gates.put: {outcome.Error}");
            }
        }
    }

    /// <summary>The bodies of <paramref name="dir"/>'s gate rows in <paramref name="ahHome"/>'s database, joined one per line as a <c>gates.jsonl</c> held them ("" when none).</summary>
    public static string Text(string ahHome, string dir) =>
        string.Concat(StateDbModule.CreateGates(ahHome).Bodies(dir).Select(static b => b + "\n"));
}
