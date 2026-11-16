using Ah.Hierarchy;

namespace Ah.Cli.Composition;

/// <summary>
/// The checklist folds of a service graph (b459 steps 11, 13): a team's entries are folded again only after the plan counter moves (the progress ops'
/// <c>backlog</c> domain), or after <see cref="Forget"/>, the on-demand rebuild. No timer refolds them. A null counter (no database) folds every time.
/// </summary>
internal sealed class ChecklistMemo(Func<long?> seq, Func<string, string, IReadOnlyList<ChecklistEntry>> fold)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Dir, string Team), (long Seq, IReadOnlyList<ChecklistEntry> Entries)> _held = [];

    internal IReadOnlyList<ChecklistEntry> Of(string dir, string team)
    {
        // read before the fold: a write between the two leaves the older counter held, so the next poll folds again
        if (seq() is not { } now)
        {
            return fold(dir, team);
        }

        lock (_gate)
        {
            if (_held.TryGetValue((dir, team), out var held) && held.Seq == now)
            {
                return held.Entries;
            }
        }

        var entries = fold(dir, team);
        lock (_gate)
        {
            _held[(dir, team)] = (now, entries);
        }

        return entries;
    }

    private readonly Dictionary<(string Dir, string Team, string Stream), (long Seq, string? Ship)> _ships = [];

    /// <summary>
    /// The newest ship <paramref name="read"/> finds in one stream's plan log, held on the same plan counter as the folds (b459 step 18): the board
    /// audit's skip check reads no plan-log text while the counter is unchanged.
    /// </summary>
    internal string? LastShip(string dir, string team, string stream, Func<string?> read)
    {
        if (seq() is not { } now)
        {
            return read();
        }

        lock (_gate)
        {
            if (_ships.TryGetValue((dir, team, stream), out var held) && held.Seq == now)
            {
                return held.Ship;
            }
        }

        var ship = read();
        lock (_gate)
        {
            _ships[(dir, team, stream)] = (now, ship);
        }

        return ship;
    }

    internal void Forget()
    {
        lock (_gate)
        {
            _held.Clear();
            _ships.Clear();
        }
    }
}
