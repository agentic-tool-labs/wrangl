using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The git lane (lazy): one key per assigned worktree, <c>(canonical dir, "team/member")</c>, one slice <c>files:&lt;dirId&gt;/&lt;team&gt;/&lt;member&gt;</c>.
/// The path git runs in is the member's recorded <c>work_root</c> (one <c>worktree-grants.status</c> per read); a worktree without one has no slice.
/// A refresh is one <c>git status --porcelain=v2 --branch -z</c> through the watch side's git request (no optional index lock, killed after
/// <see cref="GitTimeout"/>), at most once per
/// <see cref="MinRefreshMs"/> per worktree: a notice that arrives sooner is held and comes due through <see cref="LapsedKeys"/>. The body carries
/// no stderr text; a failed run is an error code (<c>timeout</c>, <c>truncated</c>, <c>not_a_repo</c>, else <c>failed</c>). Nothing here watches <c>.git</c>, which is too noisy.
/// </summary>
internal sealed class GitLane(
    IViewStateReader reader,
    Func<IReadOnlyList<string>> canonicalDirs,
    IProcessRunner process,
    Func<long> clock,
    Action<string> emit,
    string? home = null,
    Func<string, bool>? subscribed = null) : IModelLane, ILeaseLane, IQuietKeys
{
    public const string LaneName = "git";

    public const long MinRefreshMs = 5_000;

    public const int MaxFiles = 500;

    public static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(15);

    internal static readonly string[] StatusArgv = ["git", "-c", "core.fsmonitor=false", "status", "--porcelain=v2", "--branch", "-z"];

    private readonly object _gate = new();
    private readonly Dictionary<string, (long AtMs, LaneResult Result)> _last = new(StringComparer.Ordinal);
    private readonly HashSet<string> _due = new(StringComparer.Ordinal);
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    public string Name => LaneName;

    /// <summary>How many times git was run.</summary>
    public long Runs { get; private set; }

    public static string KeyOf(string dir, string team, string member) => LaneParts.Join(dir, team + "/" + member);

    public static string SliceIdOf(string dir, string team, string member) => "files:" + ActivityLane.DirId(dir) + "/" + team + "/" + member;

    public IReadOnlyList<string> SliceIds(string key)
    {
        var (dir, team, member) = Parts(key);
        return [SliceIdOf(dir, team, member)];
    }

    /// <summary>Every member with a recorded work root, enumerated from the grants documents.</summary>
    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        foreach (var dir in canonicalDirs().Distinct(StringComparer.Ordinal))
        {
            JsonValue teams;
            try
            {
                teams = reader.Read("worktree-grants", "keys", dir, new JsonObject { ["dir"] = dir });
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (teams.Member("teams") is not { IsArray: true } list)
            {
                continue;
            }

            foreach (var team in list.AsArray().Where(static t => t.IsText).Select(static t => t.AsString()))
            {
                foreach (var member in MembersWithRoot(dir, team))
                {
                    keys.Add(KeyOf(dir, team, member));
                }
            }
        }

        lock (_gate)
        {
            _known.UnionWith(keys);
        }

        return keys;
    }

    /// <summary>The members no client holds the slice of: a reconcile spawns no git for them, and a lease starting reads the key then.</summary>
    public IReadOnlySet<string> QuietKeys()
    {
        if (subscribed is null)
        {
            return new HashSet<string>();
        }

        lock (_gate)
        {
            return _known.Where(k =>
            {
                var (dir, team, member) = Parts(k);
                return !subscribed(SliceIdOf(dir, team, member));
            }).ToHashSet(StringComparer.Ordinal);
        }
    }

    // Members that share one work root share one git status within the refresh floor.
    private readonly Dictionary<string, (long AtMs, GitOutcome Outcome)> _byRoot = new(StringComparer.Ordinal);

    internal int CachedRoots
    {
        get
        {
            lock (_gate)
            {
                return _byRoot.Count;
            }
        }
    }

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, team, member) = Parts(key);
        var id = SliceIdOf(dir, team, member);
        var now = clock();
        lock (_gate)
        {
            if (_last.TryGetValue(key, out var held) && now - held.AtMs < MinRefreshMs)
            {
                _ = _due.Add(key);
                return held.Result;
            }
        }

        var root = WorkRoot(dir, team, member);
        if (root is null)
        {
            lock (_gate)
            {
                _ = _last.Remove(key);
                _ = _due.Remove(key);
                _ = _known.Remove(key);
            }

            return new LaneResult([new SliceChange(id, null)]);
        }

        GitOutcome? outcome;
        lock (_gate)
        {
            outcome = _byRoot.TryGetValue(root, out var shared) && now - shared.AtMs < MinRefreshMs ? shared.Outcome : null;
        }

        var reused = outcome is not null;
        if (outcome is null)
        {
            Runs++;
            outcome = Run(root);
            lock (_gate)
            {
                foreach (var stale in _byRoot.Where(e => now - e.Value.AtMs >= MinRefreshMs).Select(static e => e.Key).ToList())
                {
                    _ = _byRoot.Remove(stale);
                }

                _byRoot[root] = (now, outcome);
            }
        }

        var body = Body(outcome);
        var result = new LaneResult([new SliceChange(id, JsonText.ToUtf8(JsonValue.FromObject(body), 0), 0)]);
        lock (_gate)
        {
            _last[key] = (now, result);
            _ = reused ? _due.Add(key) : _due.Remove(key);
            _ = _known.Add(key);
        }

        return result;
    }

    /// <summary>
    /// An activity transition of <paramref name="subject"/> in <paramref name="dir"/>: queue a refresh of the worktree of the member that session is
    /// registered as (the registration's address is <c>member@team</c>, or a bare member name), and of no other. A subject with no registration, or
    /// one that matches no known worktree, notifies nothing.
    /// </summary>
    public void Notify(string dir, string subject)
    {
        if (home is null)
        {
            return;
        }

        List<string> addresses = [];
        try
        {
            var registration = reader.Read("mailbox", "registration", home, new JsonObject { ["session_id"] = subject }).Member("registration") ?? JsonValue.Null;
            if (LaneParts.Text(registration, "address") is { } primary)
            {
                addresses.Add(primary);
            }

            if (registration.Member("addresses") is { IsArray: true } more)
            {
                addresses.AddRange(more.AsArray().Where(static a => a.IsText).Select(static a => a.AsString()));
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var targets = addresses.Select(static a => a.IndexOf('@', StringComparison.Ordinal) is var at and >= 0 ? (Member: a[..at], Team: (string?)a[(at + 1)..]) : (Member: a, Team: (string?)null)).ToList();
        string[] keys;
        lock (_gate)
        {
            keys = [.. _known.Where(k =>
            {
                var (d, t, m) = Parts(k);
                return d == dir && targets.Any(x => x.Member == m && (x.Team is null || x.Team == t));
            })];
        }

        foreach (var key in keys)
        {
            emit(key);
        }
    }

    public string? KeyOfToken(string token)
    {
        const string Head = "files:";
        if (!token.StartsWith(Head, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = token[Head.Length..].Split('/');
        if (parts.Length != 3)
        {
            return null;
        }

        var dir = canonicalDirs().FirstOrDefault(d => ActivityLane.DirId(d) == parts[0]);
        return dir is null ? null : KeyOf(dir, parts[1], parts[2]);
    }

    /// <summary>The worktrees whose refresh was held back by the minimum interval and is due now.</summary>
    public IReadOnlyList<string> LapsedKeys()
    {
        var now = clock();
        lock (_gate)
        {
            return [.. _due.Where(k => !_last.TryGetValue(k, out var held) || now - held.AtMs >= MinRefreshMs)];
        }
    }

    private GitOutcome Run(string root)
    {
        try
        {
            var result = process.Run(GitRunner.Request(root, StatusArgv, GitTimeout));
            if (result.TimedOut)
            {
                return new GitOutcome(null, "timeout");
            }

            if (result.Truncated)
            {
                return new GitOutcome(null, "truncated");
            }

            if (result.ExitCode != 0)
            {
                return new GitOutcome(null, Encoding.UTF8.GetString(result.Stderr.Span).Contains("not a git repository", StringComparison.Ordinal) ? "not_a_repo" : "failed");
            }

            return new GitOutcome(Encoding.UTF8.GetString(result.Stdout.Span), null);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return new GitOutcome(null, "failed");
        }
    }

    internal sealed record GitOutcome(string? Output, string? Error);

    private static JsonObject Body(GitOutcome outcome) => Body(outcome.Output, outcome.Error ?? "failed");

    internal static JsonObject Body(string? output, string error = "failed")
    {
        if (output is null)
        {
            return new JsonObject
            {
                ["branch"] = JsonValue.Null,
                ["head"] = JsonValue.Null,
                ["upstream"] = JsonValue.Null,
                ["ahead"] = JsonValue.Null,
                ["behind"] = JsonValue.Null,
                ["files"] = JsonValue.FromArray([]),
                ["files_total"] = 0,
                ["truncated"] = false,
                ["error"] = error,
            };
        }

        string? branch = null;
        string? head = null;
        string? upstream = null;
        JsonValue ahead = JsonValue.Null;
        JsonValue behind = JsonValue.Null;
        var files = new List<(string Path, string Xy)>();
        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var r = records[i];
            if (r.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                head = r[13..] == "(initial)" ? null : r[13..];
            }
            else if (r.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = r[14..] == "(detached)" ? null : r[14..];
            }
            else if (r.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = r[18..];
            }
            else if (r.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var ab = r[12..].Split(' ');
                if (ab.Length == 2 && int.TryParse(ab[0].TrimStart('+'), out var a) && int.TryParse(ab[1].TrimStart('-'), out var b))
                {
                    ahead = a;
                    behind = b;
                }
            }
            else if (r.StartsWith("1 ", StringComparison.Ordinal) && Field(r, 8) is { } p1)
            {
                files.Add((p1, r.Substring(2, 2)));
            }
            else if (r.StartsWith("2 ", StringComparison.Ordinal) && Field(r, 9) is { } p2)
            {
                files.Add((p2, r.Substring(2, 2)));
                i++;
            }
            else if (r.StartsWith("u ", StringComparison.Ordinal) && Field(r, 10) is { } p3)
            {
                files.Add((p3, r.Substring(2, 2)));
            }
            else if (r.StartsWith("? ", StringComparison.Ordinal) && r.Length > 2)
            {
                files.Add((r[2..], "??"));
            }
        }

        files.Sort(static (x, y) => string.CompareOrdinal(x.Path, y.Path));
        var list = new JsonArray();
        foreach (var (path, xy) in files.Take(MaxFiles))
        {
            list.Add(JsonValue.FromObject(new JsonObject { ["path"] = path, ["xy"] = xy }));
        }

        return new JsonObject
        {
            ["branch"] = branch is null ? JsonValue.Null : branch,
            ["head"] = head is null ? JsonValue.Null : head,
            ["upstream"] = upstream is null ? JsonValue.Null : upstream,
            ["ahead"] = ahead,
            ["behind"] = behind,
            ["files"] = JsonValue.FromArray(list),
            ["files_total"] = files.Count,
            ["truncated"] = files.Count > MaxFiles,
        };
    }

    private static string? Field(string record, int spaces)
    {
        var at = -1;
        for (var n = 0; n < spaces; n++)
        {
            at = record.IndexOf(' ', at + 1);
            if (at < 0)
            {
                return null;
            }
        }

        return at + 1 < record.Length ? record[(at + 1)..] : null;
    }

    private List<string> MembersWithRoot(string dir, string team)
    {
        JsonValue grants;
        try
        {
            grants = reader.Read("worktree-grants", "status", dir, new JsonObject { ["dir"] = dir, ["team"] = team });
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        return grants.Member("members") is { IsMap: true } members
            ? members.AsObject().Keys.Where(m => LaneParts.Text(members.AsObject()[m], "work_root") is not null).Order(StringComparer.Ordinal).ToList()
            : [];
    }

    private string? WorkRoot(string dir, string team, string member)
    {
        if (!canonicalDirs().Contains(dir, StringComparer.Ordinal))
        {
            return null;
        }

        var grants = reader.Read("worktree-grants", "status", dir, new JsonObject { ["dir"] = dir, ["team"] = team });
        return grants.Member("present") is { IsBool: true } present && present.AsBool() && grants.Member("members") is { IsMap: true } members
            && members.AsObject().ContainsKey(member) ? LaneParts.Text(members.AsObject()[member], "work_root") : null;
    }

    private static (string Dir, string Team, string Member) Parts(string key)
    {
        var (dir, rest) = LaneParts.Split(key);
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? throw new ArgumentException("not a git key", nameof(key)) : (dir, rest[..slash], rest[(slash + 1)..]);
    }
}
