using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>roster stream-list</c> (spec branch-hygiene §4.1, D14): one line per live stream with a branch, over every team of this
/// checkout, as <c>&lt;branch&gt;\t&lt;worktree or -&gt;</c>. Read-only: writes nothing, no network. The live set is
/// <see cref="BranchHygiene.AllStreams"/>, the one the stream-done gate uses.
/// </summary>
internal static class StreamListVerb
{
    private static readonly string[] Flags = ["cwd"];

    [Verb("roster:stream-list")]
    internal static int List(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        StreamSupport.RejectUnknownFlags(c, Flags, "stream-list");
        foreach (var row in BranchHygiene.AllStreams(c).Where(r => r.Live && r.Branch is { Length: > 0 }))
        {
            var tree = MemberPlanning.Text(row.Record.Member("worktree")) is { Length: > 0 } t ? t : "-";
            c.Stdout.Write($"{row.Branch}\t{tree}\n");
        }

        return 0;
    }
}

/// <summary>
/// What stream-open and stream-done share (spec branch-hygiene §2, §3): the per-project golden branch, resolved offline, and the
/// git and team-record reads and ref moves the stream-done gate needs. All git runs through <see cref="ReleaseGate.Git"/>.
/// </summary>
internal static class BranchHygiene
{
    internal const string ReleaseKey = "ah.releaseRef";

    internal enum RefState
    {
        Unresolved,
        Valid,
        Invalid,
    }

    /// <summary>Golden: <c>Ref</c> is the ref it resolved to (or the invalid configured value), null when unresolved.</summary>
    internal sealed record ReleaseRef(RefState State, string? Ref)
    {
        internal bool Active => State == RefState.Valid;
    }

    /// <summary>
    /// Golden, by the first rule that yields a commit (§2): <c>git config ah.releaseRef</c> (set but unresolvable is invalid, no
    /// fallthrough), then <c>origin/HEAD</c>'s target, then <c>origin/main</c>, <c>origin/master</c>, then local <c>main</c>,
    /// <c>master</c>. Never fetches and never asks the remote.
    /// </summary>
    internal static ReleaseRef Release(RosterContext c, string root)
    {
        if (Configured(c, root) is { } configured)
        {
            return configured;
        }

        var candidates = new List<string>();
        if (ReleaseGate.Git(c, root, "symbolic-ref", "-q", "refs/remotes/origin/HEAD") is { Ok: true } head && head.Stdout.Trim() is { Length: > 0 } target)
        {
            candidates.Add(target);
        }

        candidates.AddRange(["refs/remotes/origin/main", "refs/remotes/origin/master", "refs/heads/main", "refs/heads/master"]);
        foreach (var candidate in candidates)
        {
            if (Resolves(c, root, candidate))
            {
                return new ReleaseRef(RefState.Valid, Short(candidate));
            }
        }

        return new ReleaseRef(RefState.Unresolved, null);
    }

    /// <summary>Rule 1 alone: the <c>ah.releaseRef</c> override, valid or invalid, or null when it is not set.</summary>
    internal static ReleaseRef? Configured(RosterContext c, string root)
    {
        var configured = ReleaseGate.Git(c, root, "config", "--get", ReleaseKey);
        return configured.Ok && configured.Stdout.Trim() is { Length: > 0 } value
            ? new ReleaseRef(Resolves(c, root, value) ? RefState.Valid : RefState.Invalid, value)
            : null;
    }

    /// <summary><c>origin/main</c> for <c>refs/remotes/origin/main</c>, <c>main</c> for <c>refs/heads/main</c>.</summary>
    private static string Short(string fullRef) =>
        fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal) ? fullRef["refs/remotes/".Length..]
        : fullRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? fullRef["refs/heads/".Length..]
        : fullRef;

    private static bool Resolves(RosterContext c, string root, string value) =>
        !value.StartsWith('-') && ReleaseGate.Git(c, root, "rev-parse", "--verify", "--quiet", value + "^{commit}").Ok;

    internal static RosterFailure RefuseInvalid(RosterContext c, string verb, ReleaseRef release)
    {
        var message = $"{verb}: release-ref-invalid {release.Ref}: git config {ReleaseKey} names no commit — nothing was written";
        return c.Refuse(new JsonObject { ["refused"] = "release-ref-invalid", ["value"] = release.Ref ?? string.Empty, ["message"] = message }, message);
    }

    /// <summary>Golden's local name (§2): <c>main</c> for <c>origin/main</c> or <c>refs/heads/main</c>.</summary>
    internal static string BranchPart(RosterContext c, string root, string releaseRef)
    {
        if (releaseRef.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            return releaseRef["refs/heads/".Length..];
        }

        if (releaseRef.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            var rest = releaseRef["refs/remotes/".Length..];
            var cut = rest.IndexOf('/', StringComparison.Ordinal);
            return cut > 0 ? rest[(cut + 1)..] : rest;
        }

        var slash = releaseRef.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && ReleaseGate.Git(c, root, "rev-parse", "--verify", "--quiet", "refs/remotes/" + releaseRef).Ok
            ? releaseRef[(slash + 1)..]
            : releaseRef;
    }

    /// <summary>The commit <c>refs/heads/&lt;branch&gt;</c> names, or null when there is no such branch.</summary>
    internal static string? HeadOf(RosterContext c, string root, string branch) =>
        ReleaseGate.Git(c, root, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch + "^{commit}") is { Ok: true } r && r.Stdout.Trim() is { Length: > 0 } sha
            ? sha
            : null;

    /// <summary>
    /// The branch each work tree of the repo has checked out, and whether it is locked (<c>git worktree list --porcelain</c>); empty
    /// when git fails.
    /// </summary>
    internal static List<(string Path, string? Branch, bool Locked)> Worktrees(RosterContext c, string root)
    {
        var listed = ReleaseGate.Git(c, root, "worktree", "list", "--porcelain");
        var entries = new List<(string Path, string? Branch, bool Locked)>();
        if (!listed.Ok)
        {
            return entries;
        }

        foreach (var line in listed.Stdout.Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                entries.Add((line["worktree ".Length..], null, false));
            }
            else if (entries.Count > 0 && line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                entries[^1] = entries[^1] with { Branch = line["branch refs/heads/".Length..] };
            }
            else if (entries.Count > 0 && (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal)))
            {
                entries[^1] = entries[^1] with { Locked = true };
            }
        }

        return entries;
    }

    /// <summary>One stream record of some team of this checkout.</summary>
    internal sealed record StreamRow(string? Team, string Name, JsonValue Record)
    {
        internal string? Branch => MemberPlanning.Text(Record.Member("branch"));

        internal bool Live => MemberPlanning.Text(Record.Member("state")) != "done";
    }

    /// <summary>The streams of every team in this hierarchy dir, the default team included (the keys <c>ComputeStatus</c> walks).</summary>
    internal static List<StreamRow> AllStreams(RosterContext c)
    {
        var rows = new List<StreamRow>();
        var roster = c.Services.Roster;
        var teams = new List<string?> { null };
        teams.AddRange(roster.ListTeamNames(c.Dir).Distinct(StringComparer.Ordinal));
        foreach (var team in teams)
        {
            if (roster.ReadTeamRecord(c.Dir, team).Member("streams") is not { IsMap: true } map)
            {
                continue;
            }

            var streams = map.AsObject();
            foreach (var name in streams.Keys)
            {
                if (streams[name] is { IsMap: true } stream)
                {
                    rows.Add(new StreamRow(team, name, stream));
                }
            }
        }

        return rows;
    }

    /// <summary><c>refs/archive/&lt;local date&gt;/&lt;branch&gt;</c> (§4.3).</summary>
    internal static string ArchiveRef(RosterContext c, string branch) =>
        $"refs/archive/{c.Services.Clock.UtcNow.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}/{branch}";

    /// <summary>The commit an existing ref names, or null.</summary>
    internal static string? RefSha(RosterContext c, string root, string fullRef) =>
        ReleaseGate.Git(c, root, "rev-parse", "--verify", "--quiet", fullRef) is { Ok: true } r && r.Stdout.Trim() is { Length: > 0 } sha ? sha : null;

    /// <summary>
    /// Moves <paramref name="branch"/> at <paramref name="sha"/> to its archive ref and deletes the head, only while the head still names
    /// <paramref name="sha"/>. Null on success, else what failed. An archive ref already at <paramref name="sha"/> is a no-op.
    /// </summary>
    internal static string? MoveToArchive(RosterContext c, string root, string branch, string sha, string archiveRef)
    {
        if (RefSha(c, root, archiveRef) is not { } existing)
        {
            var created = ReleaseGate.Git(c, root, "update-ref", "-m", "ah branch archive", archiveRef, sha, string.Empty);
            if (!created.Ok)
            {
                return $"git update-ref {archiveRef} failed: {created.Detail}";
            }
        }
        else if (existing != sha)
        {
            return $"archive-ref-exists {archiveRef} at {existing}";
        }

        var deleted = ReleaseGate.Git(c, root, "update-ref", "-d", "refs/heads/" + branch, sha);
        return deleted.Ok ? null : $"git update-ref -d refs/heads/{branch} failed: {deleted.Detail}";
    }

    /// <summary><c>git status --porcelain</c> paths of a work tree (ignored files never count), at most <paramref name="max"/>; null when git fails.</summary>
    internal static List<string>? DirtyPaths(RosterContext c, string tree, int max = 10)
    {
        var status = ReleaseGate.Git(c, tree, "status", "--porcelain", "--untracked-files=all");
        return status.Ok ? status.Stdout.Split('\n').Where(l => l.Length > 3).Select(l => l[3..]).Take(max).ToList() : null;
    }
}
