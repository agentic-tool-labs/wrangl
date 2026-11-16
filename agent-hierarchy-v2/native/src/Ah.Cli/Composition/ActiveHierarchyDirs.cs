using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Mesh;

namespace Ah.Cli.Composition;

/// <summary>
/// The hierarchy directories worth a file watcher: one a live registration works in, or one a hook noted within the last day.
/// The rest are still read, at the reconcile period.
/// </summary>
internal sealed class ActiveHierarchyDirs(IMailbox mailbox, IConfigCore config, IFileReader files, Func<IReadOnlyList<KeyValuePair<string, string>>> knownDirs, IClock clock)
{
    internal static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public IReadOnlyList<string> Of(IReadOnlyList<string> all)
    {
        var live = LiveDirs();
        var seen = LastSeen();
        var cutoff = clock.UtcNow.ToUnixTimeMilliseconds() - Window.TotalMilliseconds;
        return [.. all.Where(d => live.Contains(d) || (seen.TryGetValue(d, out var at) && at >= cutoff))];
    }

    private HashSet<string> LiveDirs()
    {
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in mailbox.LiveRegistrations())
        {
            foreach (var teamFile in mailbox.RegistrationTeamFiles(JsonValue.FromObject(registration)))
            {
                Add(dirs, NodePath.Dirname(NodePath.Dirname(teamFile)));
            }

            if (registration.Get("cwd") is { IsText: true } cwd && cwd.AsString().Length > 0)
            {
                Add(dirs, config.HierarchyDir(cwd.AsString()));
            }
        }

        return dirs;
    }

    private void Add(HashSet<string> dirs, string dir)
    {
        _ = dirs.Add(dir);
        try
        {
            _ = dirs.Add(files.RealPath(dir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a directory that is gone has no watcher to keep
        }
    }

    private Dictionary<string, double> LastSeen()
    {
        // no index yet: only the directories with a live registration are active
        var seen = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (dir, at) in knownDirs())
        {
            if (Timestamps.TryParseMs(at, out var ms))
            {
                seen[dir] = ms;
            }
        }

        return seen;
    }
}
