using Ah.Abstractions;
using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>A team's links record (per-dir-files-to-db S11b: the <c>links</c> table is its only home), read and put through the store the links domain writes with.</summary>
public static class TestLinks
{
    /// <summary>The record of <paramref name="team"/> under <paramref name="dir"/> in <paramref name="ahHome"/>'s database (JSON text), or null.</summary>
    public static string? Text(string ahHome, string dir, string team) => StateDbModule.CreateLinks(ahHome).Body(dir, team);

    /// <summary>Stores <paramref name="body"/> (JSON text) as the record of <paramref name="team"/> under <paramref name="dir"/>.</summary>
    public static void Put(string ahHome, string dir, string team, string body)
    {
        var store = StateDbModule.CreateLinks(ahHome);
        var outcome = StateDbModule.CreateOpRunner(ahHome).Run(new DbOpRequest("links", Guid.NewGuid().ToString(), "direct", tx =>
        {
            store.Put(tx, dir, team, body);
            return new OpResult(true, "{}"u8.ToArray());
        }, DbOnly: true));
        if (outcome.Status != DbOpStatus.Done)
        {
            throw new InvalidOperationException($"links put: {outcome.Error}");
        }
    }
}
