using Ah.Abstractions;
using Ah.Core.Json;
using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>Seeds roster rows (per-dir-files-to-db S7: <c>roster_records</c> is their only home) through the domain's <c>peers.put</c>.</summary>
public static class TestPeers
{
    /// <summary>Appends each JSON line of <paramref name="lines"/> (one row per non-blank line, as a <c>peers.jsonl</c> held them) to <paramref name="dir"/>'s roster in <paramref name="ahHome"/>'s database.</summary>
    public static void Put(IReadOnlyList<IStateDomain> domains, string ahHome, string dir, string lines)
    {
        var put = domains.Single(d => d.Name == "peers").Ops.Single(o => o.Name == "put").DbBody!;
        foreach (var line in lines.Split('\n').Where(static l => l.Trim().Length > 0))
        {
            var call = new OpCall("put", JsonText.ToUtf8(new JsonObject { ["dir"] = dir, ["body"] = line.Trim() }, 0), new RequestContext(dir, dir, string.Empty, string.Empty), Guid.NewGuid().ToString());
            var outcome = StateDbModule.CreateOpRunner(ahHome).Run(new DbOpRequest("peers", call.OperationId!, "direct", tx => put(call, tx), DbOnly: true));
            if (outcome.Status != DbOpStatus.Done)
            {
                throw new InvalidOperationException($"peers.put: {outcome.Error}");
            }
        }
    }

    /// <summary>Removes every roster row of <paramref name="dir"/> in <paramref name="ahHome"/>'s database (a test's reset between steps); nothing when there is no database.</summary>
    public static void Clear(string ahHome, string dir)
    {
        if (!File.Exists(Path.Join(ahHome, "state", "ah.db")))
        {
            return;
        }

        _ = StateDbModule.CreateOpRunner(ahHome).Run(new DbOpRequest("peers", Guid.NewGuid().ToString(), "direct", tx =>
        {
            tx.Execute("DELETE FROM roster_records WHERE pool_dir = ?1", Ah.Core.Text.PathGuard.RealPath(dir, strict: false) ?? dir);
            return new OpResult(true, JsonText.ToUtf8(new JsonObject(), 0));
        }, DbOnly: true));
    }

    /// <summary>The roster rows of <paramref name="dir"/>, one JSON line each, in append order.</summary>
    public static string[] Lines(string ahHome, string dir) => [.. StateDbModule.CreateRosterRecords(ahHome).Bodies(dir)];

    /// <summary>The bodies of <paramref name="dir"/>'s roster rows in <paramref name="ahHome"/>'s database, one per line as a <c>peers.jsonl</c> held them ("" when none).</summary>
    public static string Text(string ahHome, string dir) =>
        string.Concat(StateDbModule.CreateRosterRecords(ahHome).Bodies(dir).Select(static b => b + "\n"));
}
