using System.Globalization;
using System.Text;
using Ah.Abstractions;

namespace Ah.Watch;

/// <summary>What <c>git status --porcelain=v2 --branch</c> says about one tree.</summary>
/// <param name="Branch">The branch name, or the short sha of a detached HEAD.</param>
/// <param name="Detached">Whether HEAD is detached.</param>
/// <param name="Upstream">The upstream ref, or null when the branch has none.</param>
/// <param name="Ahead">Commits ahead of the local upstream ref; null when there is no upstream.</param>
/// <param name="Behind">Commits behind the local upstream ref; null when there is no upstream.</param>
/// <param name="Dirty">Whether a tracked file differs from the index or HEAD.</param>
internal sealed record GitTreeState(string? Branch, bool Detached, string? Upstream, int? Ahead, int? Behind, bool Dirty);

/// <summary>A tag that matches <see cref="GitRunner.TagPattern"/> and when it was made (ISO-8601 UTC).</summary>
internal sealed record GitTag(string Name, string Date);

/// <summary>The matching tags that contain one commit, and the signature of the tag list they were read under.</summary>
internal sealed record TagsContaining(IReadOnlyList<string> Tags, string Signature);

/// <summary>A cached git answer: the value, or the reason there is none, and when the runner read it.</summary>
internal sealed record GitReading<T>(T? Value, string? Reason, string? AsOf)
    where T : class;

/// <summary>
/// The one place the view runs git. Read only, lazy (a read of a tree starts the work, the watch tick never does), cached per
/// tree, and bounded in trees, time and concurrency. A stale entry is served while one refresh per key runs.
/// </summary>
internal sealed class GitRunner(IProcessRunner process, Func<double> nowMs, TimeSpan? ttl = null, TimeSpan? timeout = null) : IDisposable
{
    internal const int MaxTrees = 16;
    internal const int MaxConcurrent = 2;
    internal const string ReadingReason = "reading git…";
    internal const string UnavailableReason = "git unavailable";
    internal const string NoTagsReason = "no tags";

    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    // The only argument lists git is ever started with: both read only.
    // core.fsmonitor in a repository's own config names a command the status walk would run; turning it off keeps the daemon from
    // executing anything a work tree configures. The other read-only config that executes (hooks) is not consulted by these commands.
    internal static readonly string[] StatusArgv = ["git", "-c", "core.fsmonitor=false", "status", "--porcelain=v2", "--branch", "--untracked-files=no"];
    internal static readonly string[] DescribeArgv = ["git", "-c", "core.fsmonitor=false", "describe", "--tags", "--abbrev=0"];

    private sealed record Entry(object? Value, string? Reason, double AtMs);

    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _counted;
    private readonly Dictionary<string, Task> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>The state of <paramref name="tree"/>; starts a refresh when there is none or it is older than the TTL.</summary>
    internal GitReading<GitTreeState> Status(string tree) =>
        Get<GitTreeState>("status", tree, StatusArgv, static r => r is { ExitCode: 0 } ? (ParseStatus(Encoding.UTF8.GetString(r.Stdout.Span)), null) : (null, UnavailableReason));

    /// <summary>The newest tag reachable from <paramref name="tree"/>'s HEAD.</summary>
    internal GitReading<string> Describe(string tree) =>
        Get("describe", tree, DescribeArgv, static r => r switch
        {
            { TimedOut: true } => (null, UnavailableReason),
            { ExitCode: 0 } => (Encoding.UTF8.GetString(r.Stdout.Span).Trim() is { Length: > 0 } tag ? tag : null, NoTagsReason),
            _ => (null, NoTagsReason),
        });

    /// <summary>The first <see cref="MaxTrees"/> of <paramref name="trees"/> and how many were left out.</summary>
    internal (IReadOnlyList<(string Tree, GitReading<GitTreeState> Reading)> Rows, int More) Trees(IReadOnlyList<string> trees)
    {
        var rows = new List<(string, GitReading<GitTreeState>)>();
        foreach (var tree in trees.Take(MaxTrees))
        {
            rows.Add((tree, Status(tree)));
        }

        return (rows, Math.Max(0, trees.Count - MaxTrees));
    }

    public void Dispose() => _slots.Dispose();

    /// <summary>Completes when no refresh is running.</summary>
    internal Task WhenIdle()
    {
        Task[] running;
        lock (_gate)
        {
            running = [.. _inflight.Values];
        }

        return Task.WhenAll(running);
    }

    internal static GitTreeState ParseStatus(string text)
    {
        string? head = null;
        string? oid = null;
        string? upstream = null;
        int? ahead = null;
        int? behind = null;
        var dirty = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                oid = line["# branch.oid ".Length..].Trim();
            }
            else if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                head = line["# branch.head ".Length..].Trim();
            }
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = line["# branch.upstream ".Length..].Trim();
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = line["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && int.TryParse(parts[0].TrimStart('+'), NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                    && int.TryParse(parts[1].TrimStart('-'), NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                {
                    ahead = a;
                    behind = b;
                }
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                dirty = true;
            }
        }

        var detached = head is null or "(detached)";
        var branch = detached ? (oid is { Length: >= 7 } ? oid[..7] : null) : head;
        return new GitTreeState(branch, detached, upstream, ahead, behind, dirty);
    }

    // ---------------------------------------------------------------- release linkage reads

    /// <summary>The tag pattern release linkage looks at; a constant, so no caller can put text into the argument list.</summary>
    internal const string TagPattern = "ah2-*";
    internal const string InvalidShaReason = "not a commit id";

    internal static readonly TimeSpan TagListTtl = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan BranchTtl = TimeSpan.FromSeconds(60);

    /// <summary>Whether <paramref name="sha"/> may be put into an argument list: 7 to 40 lowercase hex digits.</summary>
    internal static bool IsSha(string sha) => sha.Length is >= 7 and <= 40 && sha.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>The argument list that asks whether <paramref name="sha"/> (already checked by <see cref="IsSha"/>) names a commit.</summary>
    internal static string[] VerifyArgv(string sha) => ["git", "rev-parse", "--verify", sha + "^{commit}"];

    /// <summary>Whether <paramref name="sha"/> names a commit in <paramref name="tree"/>: the sha when it does, null when it does not.</summary>
    internal GitReading<string> Verify(string tree, string sha) =>
        !IsSha(sha)
            ? new GitReading<string>(null, InvalidShaReason, null)
            : Get<string>("verify:" + sha, tree, VerifyArgv(sha), static r => r.TimedOut ? (null, UnavailableReason) : r.ExitCode == 0 ? (Text(r).Trim(), null) : (null, "not found"));

    /// <summary>The tags that match <see cref="TagPattern"/>, oldest first. Read at most every <see cref="TagListTtl"/>.</summary>
    internal GitReading<IReadOnlyList<GitTag>> Tags(string tree) =>
        Get<IReadOnlyList<GitTag>>(
            "tags",
            tree,
            ["git", "tag", "--list", TagPattern, "--sort=creatordate", "--format=%(refname:short)%09%(creatordate:iso-strict)"],
            static r => r is { ExitCode: 0, TimedOut: false } ? (ParseTags(Text(r)), null) : (null, UnavailableReason),
            TagListTtl);

    /// <summary>
    /// The matching tags that contain <paramref name="sha"/>. A commit in a tag stays in it, so a non-empty answer is kept for good; an
    /// empty one is read again when <paramref name="tagListSignature"/> no longer equals the one it was read under.
    /// </summary>
    internal GitReading<TagsContaining> TagsIn(string tree, string sha, string tagListSignature, bool start = true) =>
        Sha<TagsContaining>(
            tree,
            sha,
            start,
            "tagsin:" + sha,
            ["git", "tag", "--contains", sha, "--list", TagPattern],
            r => r is { ExitCode: 0, TimedOut: false } ? (new TagsContaining(Lines(r), tagListSignature), null) : (null, UnavailableReason),
            held => held.Tags.Count == 0 && held.Signature != tagListSignature);

    /// <summary>The local branches that contain <paramref name="sha"/>.</summary>
    internal GitReading<IReadOnlyList<string>> BranchesIn(string tree, string sha, bool start = true) =>
        Sha<IReadOnlyList<string>>(tree, sha, start, "branches:" + sha, ["git", "branch", "--contains", sha, "--format=%(refname:short)"], static r => LinesOrUnavailable(r), null, BranchTtl);

    /// <summary>The remote-tracking branches that contain <paramref name="sha"/>.</summary>
    internal GitReading<IReadOnlyList<string>> RemoteBranchesIn(string tree, string sha, bool start = true) =>
        Sha<IReadOnlyList<string>>(tree, sha, start, "remotes:" + sha, ["git", "branch", "-r", "--contains", sha, "--format=%(refname:short)"], static r => LinesOrUnavailable(r), null, BranchTtl);

    private GitReading<T> Sha<T>(
        string tree, string sha, bool start, string kind, string[] argv, Func<ProcessResult, (T? Value, string? Reason)> parse, Func<T, bool>? stale, TimeSpan? ttl = null)
        where T : class
    {
        if (!IsSha(sha))
        {
            return new GitReading<T>(null, InvalidShaReason, null);
        }

        return Get(kind, tree, argv, parse, ttl, stale, start);
    }

    private static string Text(ProcessResult r) => Encoding.UTF8.GetString(r.Stdout.Span);

    /// <summary>The names in a listing; a line in parentheses is git's description of a detached HEAD, not a branch.</summary>
    private static IReadOnlyList<string> Lines(ProcessResult r) =>
        [.. Text(r).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(static l => !l.StartsWith('('))];

    private static (IReadOnlyList<string>? Value, string? Reason) LinesOrUnavailable(ProcessResult r) =>
        r is { ExitCode: 0, TimedOut: false } ? (Lines(r), null) : (null, UnavailableReason);

    internal static IReadOnlyList<GitTag> ParseTags(string text)
    {
        var tags = new List<GitTag>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length == 2
                && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                tags.Add(new GitTag(parts[0], at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
            }
        }

        return tags;
    }

    private GitReading<T> Get<T>(
        string kind, string tree, string[] argv, Func<ProcessResult, (T? Value, string? Reason)> parse,
        TimeSpan? ttl = null, Func<T, bool>? stale = null, bool start = true)
        where T : class
    {
        var key = kind + "\0" + tree;
        var now = nowMs();
        Entry? held;
        lock (_gate)
        {
            _ = _entries.TryGetValue(key, out held);
            var old = held is null
                || (held.Value is T value && stale is not null ? stale(value) : now - held.AtMs >= (ttl ?? _ttl).TotalMilliseconds);
            if (old && start && !_inflight.ContainsKey(key))
            {
                ViewTiming.Current?.AddGitStarted();
                _inflight[key] = Task.Run(() => Refresh(key, tree, argv, parse));
            }
        }

        return held is null
            ? new GitReading<T>(null, ReadingReason, null)
            : new GitReading<T>((T?)held.Value, held.Reason, IsoTime.Seconds(held.AtMs));
    }

    /// <summary>The one way the watch side runs git: in <paramref name="tree"/>, never taking the optional index lock, killed after <paramref name="timeout"/>.</summary>
    internal static ProcessRequest Request(string tree, IReadOnlyList<string> argv, TimeSpan timeout) =>
        new(WithoutFsmonitor(argv), tree, GitEnvironment(), Timeout: timeout);

    // The process runner gives a child exactly the environment it is asked for, so PATH is passed on to find git; without it the
    // command is not found and every lookup reads as failed.
    private static Dictionary<string, string?> GitEnvironment() => new()
    {
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
    };

    // Every git the watch side starts runs with the fsmonitor hook off, whichever argument list built it.
    private static IReadOnlyList<string> WithoutFsmonitor(IReadOnlyList<string> argv) =>
        argv.Contains("core.fsmonitor=false") ? argv : [argv[0], "-c", "core.fsmonitor=false", .. argv.Skip(1)];

    private void Refresh<T>(string key, string tree, string[] argv, Func<ProcessResult, (T? Value, string? Reason)> parse)
        where T : class
    {
        Entry entry;
        _slots.Wait();
        try
        {
            var result = process.Run(Request(tree, argv, _timeout));
            var (value, reason) = result.TimedOut ? (null, UnavailableReason) : parse(result);
            entry = new Entry(value, value is null ? reason : null, nowMs());
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            entry = new Entry(null, UnavailableReason, nowMs());
        }
        finally
        {
            _ = _slots.Release();
        }

        lock (_gate)
        {
            if (Interlocked.Exchange(ref _counted, 1) == 0)
            {
                Ah.Core.CoreModule.CensusAdd(this, "watch.git_results", static g => { lock (g._gate) { return g._entries.Count; } });
            }

            _entries[key] = entry;
            _ = _inflight.Remove(key);
        }

        ViewTiming.Current?.AddGitDone();
    }
}
