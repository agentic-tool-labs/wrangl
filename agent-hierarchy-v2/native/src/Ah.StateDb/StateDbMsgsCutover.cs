using System.Globalization;
using Ah.Core.Json;
using static Ah.StateDb.StateDbCutoverKit;

namespace Ah.StateDb;

internal sealed class StateDbMsgsCutover : IMsgsCutover
{
    private const string ManifestKey = "import.msgs";

    private sealed class Stop(string message, bool refused) : Exception(message)
    {
        internal bool Refused { get; } = refused;
    }

    private sealed class DryRunDone : Exception;

    public MsgsResult Import(string ahHome, IReadOnlyList<MsgPool> pools, bool dryRun, DateTimeOffset now)
    {
        if (Guard(ahHome, pools) is { } refusal)
        {
            return Refuse(refusal);
        }

        var direct = new StateDbDirect(ahHome, DirectWriteOptions.Message);
        var counts = new List<MsgPoolCount>();
        try
        {
            if (dryRun)
            {
                try
                {
                    var rolledBack = direct.Write<int>(tx =>
                    {
                        RequireSchema(tx);
                        counts.AddRange(pools.Select(p => ImportPool(tx, p.Batch, now)));
                        WriteManifest(tx, pools, now);
                        throw new DryRunDone();
                    });
                    if (!rolledBack.Ok)
                    {
                        return Fail(rolledBack.Message);
                    }
                }
                catch (DryRunDone)
                {
                }

                return Done(counts, $"would import {Total(counts, c => c.Files)} files ({Total(counts, c => c.Drafts)} drafts); nothing was changed");
            }

            var schema = direct.Write(tx =>
            {
                RequireSchema(tx);
                return 0;
            });
            if (!schema.Ok)
            {
                return Fail(schema.Message);
            }

            foreach (var pool in pools)
            {
                var one = direct.Write(tx => ImportPool(tx, pool.Batch, now));
                if (!one.Ok)
                {
                    return Fail(one.Message);
                }

                counts.Add(one.Value!);
            }

            var manifest = direct.Write(tx =>
            {
                WriteManifest(tx, pools, now);
                return 0;
            });
            return manifest.Ok ? Done(counts, $"imported {Total(counts, c => c.Inserted)} of {Total(counts, c => c.Files)} files") : Fail(manifest.Message);
        }
        catch (Stop stop)
        {
            return stop.Refused ? Refuse(stop.Message) : Fail(stop.Message);
        }
    }

    private static string? Guard(string ahHome, IReadOnlyList<MsgPool> pools)
    {
        if (!Within(ahHome, Path.Join(ahHome, "state")))
        {
            return $"the state directory of {ahHome} resolves outside it";
        }

        return pools.FirstOrDefault(p => p.Writable && !Within(ahHome, p.Batch.PoolDir)) is { } outside
            ? $"the pool {outside.Batch.PoolDir} is outside {ahHome} and cannot be written"
            : null;
    }

    private static void RequireSchema(IStateDbTransaction tx)
    {
        if (Schema(tx) < 5)
        {
            throw new Stop($"the database schema is {Schema(tx)}; msgs needs 5", refused: false);
        }
    }

    private static MsgPoolCount ImportPool(IStateDbTransaction tx, MsgPoolBatch batch, DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        // the import writes outside the op runner, so it counts itself in the store's version: a held listing keys on it
        tx.Execute("INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('msgs', 1, ?1) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1, changed_ms = ?1", nowMs);
        var inserted = 0;
        var pks = new Dictionary<MsgFileRow, long>(ReferenceEqualityComparer.Instance);
        foreach (var row in batch.Rows)
        {
            tx.Execute(MsgRows.InsertMessageSql, MsgRows.MessageArgs(row));
            inserted += (long)tx.Query("SELECT changes()")[0][0]! > 0 ? 1 : 0;
            var pk = (long)tx.Query(MsgRows.SelectPkSql, MsgRows.KeyArgs(row))[0][0]!;
            pks[row] = pk;
            if (row.Bytes is not null)
            {
                // a file that was a draft when it was first imported and is sealed now becomes sealed in the same transaction as its body
                tx.Execute(MsgRows.UpgradeToSealedSql, MsgRows.UpgradeArgs(pk, row));
                tx.Execute(MsgRows.InsertBodySql, MsgRows.BodyArgs(pk, row, row.SealedAtMs ?? nowMs));
            }
        }

        foreach (var pair in MsgRows.Pair(batch.Rows))
        {
            var answeredMs = pair.Response is { Bytes: not null } sealedResponse ? sealedResponse.SealedAtMs ?? nowMs : (long?)null;
            tx.Execute(MsgRows.InsertExchangeSql, MsgRows.ExchangeArgs(pair, pks[pair.Request], pair.Response is null ? null : pks[pair.Response], answeredMs));
        }

        return new MsgPoolCount(batch.PoolDir, batch.Files, batch.TotalBytes, batch.Drafts, batch.Skipped.Count, inserted);
    }

    private static void WriteManifest(IStateDbTransaction tx, IReadOnlyList<MsgPool> pools, DateTimeOffset now)
    {
        static string Js(string text) => JsonText.Quote(text);
        var entries = pools.Select(p => $"{{\"pool\":{Js(p.Batch.PoolDir)},\"files\":{p.Batch.Files},\"bytes\":{p.Batch.TotalBytes},\"drafts\":{p.Batch.Drafts},\"skipped\":[{string.Join(",", p.Batch.Skipped.Select(Js))}]}}");
        var manifest = $"{{\"at\":{Js(now.ToString("O", CultureInfo.InvariantCulture))},\"pools\":[{string.Join(",", entries)}]}}";
        tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ManifestKey, manifest);
    }

    private static int Total(List<MsgPoolCount> counts, Func<MsgPoolCount, int> field) => counts.Sum(field);

    private static MsgsResult Done(List<MsgPoolCount> counts, string message) => new(true, message, false, counts);

    private static MsgsResult Refuse(string reason) => new(false, reason, true, []);

    private static MsgsResult Fail(string reason) => new(false, reason, false, []);
}
