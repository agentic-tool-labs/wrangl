using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// The home documents (b397 group 2) as <c>meta</c> rows of the state database of <paramref name="ahHome"/>: rows only, no file is read or
/// taken in. A read that cannot answer reads as no document, as the old unreadable file did.
/// </summary>
internal sealed class StateDbHomeDocs(string ahHome) : IHomeDocs
{
    private readonly IDbOpRunner _runner = StateDbModule.CreateOpRunner(ahHome);

    public string? Read(string key) =>
        StateDbStoreReads.ReadOrEmpty(ahHome, c => StateDbRows.Run(c, "SELECT value FROM meta WHERE key = ?1", [key]) is [[string body]] ? [body] : new List<string>()).FirstOrDefault();

    public bool TryRead(string key, out string? body)
    {
        body = null;
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return true;
        }

        try
        {
            body = StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(c, "SELECT value FROM meta WHERE key = ?1", [key]) is [[string text]] ? text : null);
            return true;
        }
        catch (Exception e) when (e is StoreUnavailableException or SqliteException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Write(string key, string? body)
    {
        var outcome = _runner.Run(new DbOpRequest("home-docs", null, ahHome, tx =>
        {
            if (body is null)
            {
                tx.Execute("DELETE FROM meta WHERE key = ?1", key);
            }
            else
            {
                tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, body);
            }

            return new OpResult(true, "null"u8.ToArray());
        }, LeavesSeq: true, DbOnly: true));
        if (outcome.Status != DbOpStatus.Done)
        {
            throw new InvalidOperationException($"{key} was not written: {outcome.Error ?? outcome.Status.ToString()}");
        }
    }
}
