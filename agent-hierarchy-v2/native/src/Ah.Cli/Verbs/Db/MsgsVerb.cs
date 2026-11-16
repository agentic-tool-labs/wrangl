using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Hierarchy;
using Ah.Roster;

namespace Ah.Cli.Verbs.Db;

/// <summary>The test-only <c>ah db test-seed msgs</c>.</summary>
internal static class MsgsVerb
{
    internal const string Domain = "msgs";

    private const string TestSeed = "test-seed";

    private const string Usage = "usage: ah db test-seed msgs [--pool D]...";

    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    internal static bool Handles(string[] args) => args is [TestSeed, Domain, ..];

    /// <summary>Stores the message files planted under each pool, as a daemon start would: refused outside test mode.</summary>
    internal static int Run(string ahHome, ServiceGraph? graph, DateTimeOffset now, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (graph is null)
        {
            stderr.WriteLine($"ah db {args[0]} {Domain}: no services are available here");
            return 1;
        }

        return Run(ahHome, graph.Hierarchy.Core, graph.Hierarchy.Messages, graph.Roster, graph.Io.Locks, Ah.StateDb.StateDbModule.CreateMsgsCutover(), now, args, stdout, stderr, graph.Environment.TestMode);
    }

    internal static int Run(
        string ahHome, IHierarchyCore core, IHierarchyMessages messages, IRosterCore roster, ILockProvider locks, Ah.StateDb.IMsgsCutover store,
        DateTimeOffset now, string[] args, TextWriter stdout, TextWriter stderr, bool testMode)
    {
        var pools = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] != "--pool" || ++i >= args.Length)
            {
                stderr.WriteLine(Usage);
                return 2;
            }

            pools.Add(args[i]);
        }

        if (!testMode)
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        if (pools.Count == 0)
        {
            pools.AddRange(RegisteredPools(ahHome, messages, roster));
        }

        if (pools.Count == 0)
        {
            stderr.WriteLine($"ah db {TestSeed} {Domain}: no pool given and none registered in the known-dirs index");
            return 2;
        }

        // a planted fixture may be the sandbox's first message, and no message write has created the database for it
        using (Ah.StateDb.StateDbModule.Open(ahHome, mayCreate: true))
        {
        }

        // a fixture is planted just before it is seeded, so it counts as long landed: it is sealed unless it is a draft by content or size
        using var collection = MsgsCollector.Collect(ahHome, pools, core, locks, now.AddDays(1), LockWait, lockRegistered: false, out var error);
        if (collection is null)
        {
            stderr.WriteLine($"ah db {TestSeed} {Domain}: {error}");
            return 1;
        }

        var result = store.Import(ahHome, collection.Pools, dryRun: false, now);
        var o = result.Ok ? stdout : stderr;
        o.WriteLine($"ah db {TestSeed} {Domain}: {result.Message}");
        foreach (var p in result.Pools)
        {
            o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {p.PoolDir}: files {p.Files}, drafts {p.Drafts}, skipped {p.Skipped}, bytes {p.Bytes}, inserted {p.Inserted}"));
        }

        if (result.Ok)
        {
            // what a daemon start does next (§10), so a planted stub reads and fills with no daemon
            Ah.StateDb.StateDbModule.ImportMsgDrafts(ahHome, stderr.WriteLine);
        }

        return result.Ok ? 0 : result.Refused ? 2 : 1;
    }

    /// <summary>
    /// The pools of every hierarchy directory that the known-dirs index inside <paramref name="ahHome"/> lists, with the pools their
    /// rosters name. An index that is missing or unreadable lists nothing, so only pools under the home are then accepted.
    /// </summary>
    internal static List<string> RegisteredPools(string ahHome, IHierarchyMessages messages, IRosterCore roster)
    {
        var dirs = Ah.StateDb.StateDbModule.KnownDirs(ahHome).Select(static e => e.Key).ToList();
        dirs.AddRange(Ah.StateDb.StateDbModule.RegisteredHierarchyDirs(ahHome));
        return [.. dirs.Distinct(StringComparer.Ordinal).SelectMany(d => PoolsOfDir(d, messages, roster))];
    }

    // a directory gone or unreadable names no pools; a home keeps stale entries, and one must not stop the rest
    private static IReadOnlyList<string> PoolsOfDir(string dir, IHierarchyMessages messages, IRosterCore roster)
    {
        try
        {
            return messages.PoolDirs(dir, roster.ListTeamNames(dir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return [];
        }
    }
}
