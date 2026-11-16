using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>One pool of message files and whether the run may write into it.</summary>
/// <param name="Batch">The files of the pool as the collector read them.</param>
/// <param name="Writable">
/// Whether the run may write into the pool (its lock file, its message files). A pool that is only registered, outside the ah home, is read-only:
/// no operation of this interface changes a byte or a modification time under it, and a pool outside the ah home cannot be marked writable.
/// </param>
public sealed record MsgPool(MsgPoolBatch Batch, bool Writable);

/// <summary>What an import recorded for one pool.</summary>
/// <param name="PoolDir">The pool's hierarchy directory.</param>
/// <param name="Files">The parseable files.</param>
/// <param name="Bytes">The bytes of the sealed bodies.</param>
/// <param name="Drafts">The files kept as drafts.</param>
/// <param name="Skipped">The names that did not parse.</param>
/// <param name="Inserted">The message rows this run added; a repeat run adds none.</param>
public sealed record MsgPoolCount(string PoolDir, int Files, long Bytes, int Drafts, int Skipped, int Inserted);

/// <summary>The outcome of an import.</summary>
/// <param name="Ok">Whether it did what was asked with nothing wrong.</param>
/// <param name="Message">A sentence to print.</param>
/// <param name="Refused">True when the request was refused before anything was changed, which a caller reports as a usage error.</param>
/// <param name="Pools">The counts per pool.</param>
public sealed record MsgsResult(bool Ok, string Message, bool Refused, IReadOnlyList<MsgPoolCount> Pools);

/// <summary>Moves message files into the message tables. It never flips the mode.</summary>
public interface IMsgsCutover
{
    /// <summary>
    /// Stores every file of the pools, one transaction per pool, and records a manifest in <c>meta</c>. A file already stored is left alone, so a repeat run
    /// adds nothing. With <paramref name="dryRun"/> the whole run is one transaction that is rolled back; the counts are still reported.
    /// </summary>
    MsgsResult Import(string ahHome, IReadOnlyList<MsgPool> pools, bool dryRun, DateTimeOffset now);
}
