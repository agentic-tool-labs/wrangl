using System.Diagnostics;
using Ah.Abstractions;
using Ah.StateDb;

namespace Ah.Cli.Composition;

/// <summary>
/// A new binary finds a database of an older schema while the old daemon still runs, and every direct write refuses it. Only a daemon
/// migrates, after its backup, so a CLI verb has the daemon handshake replace the older daemon and waits for the database to catch up.
/// A hook never comes here: it has no time to wait, and its own daemon check only asks the old daemon to drain.
/// </summary>
internal static class SchemaUpgradeWait
{
    // ponytail: a daemon that cannot start or migrate costs each call this whole wait before the refusal; read the start-failure marker if that hurts
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan EnsureBudget = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>Returns once the database is current or <paramref name="budget"/> ran out; a write that still cannot be made refuses as it always did.</summary>
    internal static void Run(IAhEnvironment env, Func<IDaemonClient> daemon, TextWriter stderr, TimeSpan? budget = null)
    {
        if (StateDbModule.SchemaBehind(env.AhHome) is not { } behind || env.GetVariable("AH_DAEMON") == "off")
        {
            return;
        }

        stderr.WriteLine($"ah: the state database is schema {behind}, older than this binary's {StateDbModule.SchemaVersion}; replacing the daemon, which backs it up and migrates it");
        var client = daemon();
        var wait = budget ?? Budget;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < wait)
        {
            try
            {
                _ = client.EnsureDaemonAsync(EnsureBudget).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the next round tries again, and the verb's own write reports a database that never caught up
            }

            var round = Stopwatch.StartNew();
            while (round.Elapsed < EnsureBudget && clock.Elapsed < wait)
            {
                if (StateDbModule.SchemaBehind(env.AhHome) is null)
                {
                    return;
                }

                Thread.Sleep(PollEvery);
            }
        }
    }
}
