using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;

namespace Ah.Cli.Verbs.Db;

/// <summary>The rows read from some pools and the pool locks held while they are used; disposing releases the locks.</summary>
internal sealed class MsgsCollection(IReadOnlyList<MsgPoolBatch> batches, IReadOnlySet<string> writablePools, List<IDisposable> held, Func<IReadOnlyList<MsgPoolBatch>> reader) : IDisposable
{
    /// <summary>The pools read again under the locks this collection still holds, so a file that appeared since the first read is seen.</summary>
    internal IReadOnlyList<Ah.StateDb.MsgPool> Reread() => [.. reader().Select(b => new Ah.StateDb.MsgPool(b, WritablePools.Contains(b.PoolDir)))];

    /// <summary>One batch per pool, in sorted pool order.</summary>
    internal IReadOnlyList<MsgPoolBatch> Batches { get; } = batches;

    /// <summary>The batches with their write flags, as the database side takes them.</summary>
    internal IReadOnlyList<Ah.StateDb.MsgPool> Pools { get; } = [.. batches.Select(b => new Ah.StateDb.MsgPool(b, writablePools.Contains(b.PoolDir)))];

    /// <summary>The pools that sit under the home and so may be locked and written; every other pool is read-only.</summary>
    internal IReadOnlySet<string> WritablePools { get; } = writablePools;

    public void Dispose()
    {
        foreach (var h in held)
        {
            h.Dispose();
        }
    }
}

/// <summary>Turns pool directories into <see cref="MsgPoolBatch"/> rows: the files of <c>msgs</c> and <c>msgs/archive</c>, read and classified as draft or sealed.</summary>
internal static class MsgsCollector
{
    /// <summary>
    /// Reads the pools in sorted order. The domain lock of each pool under <paramref name="ahHome"/> is taken before its files are read and
    /// stays held until the returned collection is disposed; a pool outside the home is read without a lock file or any other write.
    /// Returns null, with <paramref name="error"/> set, when a lock is not obtained.
    /// </summary>
    internal static MsgsCollection? Collect(
        string ahHome, IReadOnlyList<string> pools, IHierarchyCore core, ILockProvider locks, DateTimeOffset now, TimeSpan lockWait, bool lockRegistered, out string? error, Action<string>? unreadable = null)
    {
        error = null;
        // strict only to refuse a link loop; the key itself is the one every msgs write and read uses
        var resolved = pools.Select(p => PathGuard.RealPath(p, strict: true) is null ? null : MsgRows.PoolKey(p)).ToList();
        if (resolved.Any(p => p is null))
        {
            error = "a pool cannot be resolved (a link loop or an unreadable link)";
            return null;
        }

        var real = resolved.OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var writable = real.Where(p => PathGuard.Within(ahHome, p)).ToHashSet(StringComparer.Ordinal);
        var held = new List<IDisposable>();
        foreach (var pool in real.Where(p => lockRegistered || writable.Contains(p)))
        {
            // the lock the engine takes for a msgs write to this pool (files-off-disk §13.3)
            var lockPath = Ah.StateDb.StateDbModule.StateLockPath(ahHome, "msgs", Path.Join(pool, ".state-msgs"), ".domain.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            var lease = locks.Acquire(lockPath, lockWait);
            if (lease is null)
            {
                foreach (var h in held)
                {
                    h.Dispose();
                }

                error = $"could not take the msgs lock of {pool}; another writer holds it";
                return null;
            }

            held.Add(lease);
        }

        IReadOnlyList<MsgPoolBatch> Read() => [.. real.Select(p => ReadPool(p, core, now, unreadable))];
        return new MsgsCollection(Read(), writable, held, Read);
    }

    private static MsgPoolBatch ReadPool(string pool, IHierarchyCore core, DateTimeOffset now, Action<string>? unreadable)
    {
        var rows = new List<MsgFileRow>();
        var skipped = new List<string>();
        foreach (var archived in new[] { false, true })
        {
            var dir = Path.Join(pool, "msgs", archived ? "archive" : "");
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (core.ParseMsgFilename(name) is not { } parts)
                {
                    skipped.Add((archived ? "archive/" : "") + name);
                    continue;
                }

                try
                {
                    rows.Add(ReadFile(pool, file, name, parts, archived, now));
                }
                catch (Exception e) when (unreadable is not null && e is IOException or UnauthorizedAccessException)
                {
                    unreadable($"{file}: {e.Message}");
                }
            }
        }

        return new MsgPoolBatch(pool, rows, skipped);
    }

    // reads the file itself: the core serves only the store, and this home is not cut over yet
    private static MsgFileRow ReadFile(string pool, string file, string name, MsgFilenameParts parts, bool archived, DateTimeOffset now)
    {
        var bytes = File.ReadAllBytes(file);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var fields = MessageFront.ParseFrontmatter(text)?.Fields;
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file));
        var quiet = now - written >= TimeSpan.FromMilliseconds(HierarchyModule.LandedQuietMs);
        var noReport = fields?.Get("id") is { IsText: true } id && id.AsString() == parts.Id && !MessageFront.HasAuthoredContent(text);
        var sealedFile = quiet && bytes.Length <= MsgRows.BodyCap && !noReport;
        // a sealed file is sealed as of its last write, so an imported report does not read as fresh
        return MsgFileRows.FromFrontMatter(
            pool, file, name, parts.Id, parts.Type, parts.To, parts.Slug, fields is null ? default(JsonValue?) : JsonValue.FromObject(fields), archived,
            sealedFile ? MsgRows.Sealed : MsgRows.Draft, sealedFile ? bytes : null) with { SealedAtMs = sealedFile ? written.ToUnixTimeMilliseconds() : null };
    }
}
