using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The <c>recover_records</c> table (schema 43) over the state database of <paramref name="ahHome"/>. Rows only: the old <c>recover/</c> files are not read.</summary>
internal sealed class StateDbRecoverRecords(string ahHome) : IRecoverRecordStore
{
    private readonly IDbOpRunner _runner = StateDbModule.CreateOpRunner(ahHome);

    private static string Kind(bool control) => control ? "control" : "record";

    public string? Read(string dirKey, string team, string member, bool control)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return null;
        }

        try
        {
            return StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(
                c, "SELECT body FROM recover_records WHERE dir_key = ?1 AND team = ?2 AND member = ?3 AND kind = ?4", [dirKey, team, member, Kind(control)])) is [var row]
                ? (string?)row[0]
                : null;
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException)
        {
            // an unreadable store is not "no record": a caller that took it for one would write a fresh record over the stored one
            throw new StoreUnavailableException($"the recovery record of {team}/{member} cannot be read: {e.Message}", e);
        }
    }

    public void Write(string dirKey, string team, string member, bool control, string body)
    {
        var outcome = _runner.Run(new DbOpRequest("recover", null, dirKey, tx =>
        {
            tx.Execute(
                "INSERT INTO recover_records(dir_key, team, member, kind, body, updated_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6) "
                + "ON CONFLICT(dir_key, team, member, kind) DO UPDATE SET body = ?5, updated_at = ?6",
                dirKey, team, member, Kind(control), body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return new OpResult(true, "null"u8.ToArray());
        }, LeavesSeq: true, DbOnly: true));
        if (outcome.Status != DbOpStatus.Done)
        {
            throw new InvalidOperationException($"the recovery record of {team}/{member} was not written: {outcome.Error ?? outcome.Status.ToString()}");
        }
    }
}
