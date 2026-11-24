using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>stream-done</c>'s unshipped-commits gate and archive (spec branch-hygiene §3.2). <see cref="Check"/> runs every check and
/// decides, with read-only git, before stream-done writes anything; <see cref="Archive"/> is step 6, after the record is written.
/// </summary>
internal static class StreamDoneGate
{
    internal const int MaxReason = 200;

    internal const int Listed = 15;

    internal static readonly string[] Flags = ["covered", "merged-into", "abandon"];

    /// <summary>The verdict of a stream done with unshipped commits in a run whose rules mark it local-only (ah2-596). Never archived.</summary>
    internal const string LocalOnly = "local-only";

    /// <summary>What step 6 would do: the work tree to remove (null: none), the ref to create and the head to delete.</summary>
    internal sealed record ArchivePlan(string Root, string Branch, string Sha, string Ref, string? Worktree);

    /// <summary>
    /// The gate's decision. <c>Verdict</c> null means stream-done behaves exactly as before this gate (not in a git checkout, or a
    /// done stream with nothing left to archive). <c>Archivable</c>: the verdict lets step 6 run once the members are gone.
    /// </summary>
    internal sealed record Plan(string? Verdict, string? Reason, string? HeadSha, int? Unshipped, bool Archivable, ArchivePlan? Archive, string? ArchiveSkipped, List<string> Warnings);

    private static readonly Plan Off = new(null, null, null, null, false, null, null, []);

    /// <summary>The unshipped commits of a branch against a target: the count and the first ≤15 as <c>sha7 subject</c>.</summary>
    internal sealed record Unshipped(int Count, IReadOnlyList<string> Lines, string? Failure)
    {
        internal JsonArray LinesJson()
        {
            var array = new JsonArray();
            array.AddRange(Lines.Select(l => (JsonValue)l));
            return array;
        }
    }

    /// <summary>
    /// The <c>+</c> lines of <c>git cherry -v</c>: the non-merge commits in <c>target..branch</c> whose patch-id has no match in
    /// <paramref name="target"/>. Patch-id, never sha (D1): a cherry-picked ship reads 0, also after a catch-up merge.
    /// </summary>
    internal static Unshipped Count(RosterContext c, string root, string target, string branch)
    {
        if (target.StartsWith('-') || branch.StartsWith('-'))
        {
            return new Unshipped(0, [], $"refusing a ref that starts with '-': {(target.StartsWith('-') ? target : branch)}");
        }

        var cherry = ReleaseGate.Git(c, root, "cherry", "-v", target, branch);
        if (!cherry.Ok)
        {
            return new Unshipped(0, [], cherry.Detail);
        }

        var plus = cherry.Stdout.Split('\n').Where(l => l.StartsWith("+ ", StringComparison.Ordinal)).ToList();

        // A catch-up merge of the target into the branch (`merge -X theirs <tip>`) makes the target's copies of the branch's commits
        // reachable from the branch, so `git cherry <target> <branch>` no longer compares against them. Re-check each '+' commit
        // against the target from that commit itself, where the target's copies are still on the other side.
        var merges = ReleaseGate.Git(c, root, "rev-list", "--merges", "-n", "1", target + ".." + branch);
        if (!merges.Ok)
        {
            return new Unshipped(0, [], merges.Detail);
        }

        if (merges.Stdout.Trim().Length > 0)
        {
            plus = plus.Where(l =>
            {
                var sha = l[2..].Split(' ', 2)[0];
                var alone = ReleaseGate.Git(c, root, "cherry", target, sha);
                return !alone.Ok || alone.Stdout.Split('\n').Any(a => a.Trim() == "+ " + sha);
            }).ToList();
        }
        var lines = plus.Take(Listed).Select(l =>
        {
            var rest = l[2..];
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            var sha = space < 0 ? rest : rest[..space];
            return (sha.Length > 7 ? sha[..7] : sha) + (space < 0 ? string.Empty : " " + rest[(space + 1)..]);
        }).ToList();
        return new Unshipped(plus.Count, lines, null);
    }

    private static RosterFailure Refuse(RosterContext c, string code, string detail, JsonObject? extra = null, string? shown = null)
    {
        var message = $"stream-done: {shown ?? code}: {detail} — nothing was written";
        var body = extra ?? new JsonObject();
        body["refused"] = code;
        body["message"] = message;
        return c.Refuse(body, message);
    }

    private static bool IsArchivable(string? verdict) =>
        verdict is "shipped" or "covered" or "abandoned" || (verdict?.StartsWith("merged-into:", StringComparison.Ordinal) ?? false);

    /// <summary>Steps 2-4 and the archive decision of step 6. Throws the refusal; writes nothing.</summary>
    internal static Plan Check(RosterContext c, string dir, JsonValue team, string name, JsonValue prior, bool alreadyDone)
    {
        var covered = StreamSupport.ValueFlag(c, "covered", "stream-done");
        var merged = StreamSupport.ValueFlag(c, "merged-into", "stream-done");
        var abandon = StreamSupport.ValueFlag(c, "abandon", "stream-done");
        if (new[] { covered, merged, abandon }.Count(f => f is not null) > 1)
        {
            throw new RosterFailure("stream-done: --covered, --merged-into and --abandon are mutually exclusive; give one");
        }

        var reason = (covered ?? abandon)?.Trim();
        if ((covered ?? abandon) is not null && (reason!.Length == 0 || reason.Length > MaxReason))
        {
            throw Refuse(c, "reason-required", $"--{(covered is not null ? "covered" : "abandon")} takes a reason of 1-{MaxReason} characters");
        }

        var root = c.Services.Launch is null ? null : c.Services.Config.CheckoutRoot(c.Cwd);
        if (string.IsNullOrEmpty(root))
        {
            return Off;
        }

        bool IsThis(BranchHygiene.StreamRow r) => r.Name == name && r.Team == c.TeamFile;
        var branch = MemberPlanning.Text(prior.Member("branch"));
        if (alreadyDone)
        {
            // re-run: only step 6 is re-attempted, on the recorded verdict
            var recordedVerdict = MemberPlanning.Text(prior.Member("verdict"));
            var recordedHead = MemberPlanning.Text(prior.Member("head_sha"));
            if (!IsArchivable(recordedVerdict) || recordedHead is null || branch is null || prior.Member("archived_ref") is { IsText: true })
            {
                return Off;
            }

            var (again, againSkipped, againWarnings) = PlanArchive(c, dir, team, name, root, branch, recordedHead, prior, BranchHygiene.AllStreams(c), IsThis);
            return new Plan(recordedVerdict, MemberPlanning.Text(prior.Member("reason")), recordedHead, null, true, again, againSkipped, againWarnings);
        }

        // step 2: branch presence
        if (branch is not { Length: > 0 })
        {
            return Off with { Verdict = "no-branch", Reason = reason ?? merged };
        }

        if (BranchHygiene.HeadOf(c, root, branch) is not { } head)
        {
            CheckClean(c, prior);
            return Off with { Verdict = "branch-missing", Reason = reason ?? merged, Warnings = [$"branch-missing: refs/heads/{branch} does not exist; the unshipped-commits gate was skipped and nothing is archived"] };
        }

        // step 3: the gate
        var release = BranchHygiene.Release(c, root);
        if (release.State == BranchHygiene.RefState.Invalid)
        {
            throw BranchHygiene.RefuseInvalid(c, "stream-done", release);
        }

        var streams = BranchHygiene.AllStreams(c);
        var warnings = new List<string>();
        int? count = null;
        string verdict;
        if (merged is not null)
        {
            verdict = MergedInto(c, root, release, branch, merged, streams, IsThis);
        }
        else if (!release.Active)
        {
            warnings.Add("golden-unresolved: no ah.releaseRef, origin/HEAD, origin/main|master or main|master resolves; the unshipped-commits gate was skipped");
            if (covered is null && abandon is null)
            {
                // done as before the gate: no work-tree refusal, nothing archived
                return Off with { Verdict = "unchecked", HeadSha = head, Warnings = warnings };
            }

            verdict = covered is not null ? "covered" : "abandoned";
        }
        else
        {
            var unshipped = Count(c, root, release.Ref!, "refs/heads/" + branch);
            if (unshipped.Failure is { } failed)
            {
                throw Refuse(c, "git-failed", $"git cherry {release.Ref} {branch} failed: {failed}");
            }

            count = unshipped.Count;
            verdict = unshipped.Count == 0 ? "shipped"
                : covered is not null ? "covered"
                : abandon is not null ? "abandoned"
                : c.Services.LocalOnly(c.Cwd, MemberPlanning.Text(team.Member("team")) ?? c.TeamFile) ? LocalOnly
                : throw Refuse(
                    c,
                    "unshipped-commits",
                    $"{unshipped.Count} commit(s) of {branch} are not in {release.Ref}; ship them, or give --covered <reason>, --merged-into <branch> or --abandon <reason>",
                    new JsonObject
                    {
                        ["unshipped"] = unshipped.Count,
                        ["commits"] = unshipped.LinesJson(),
                        ["flags"] = new JsonArray { "--covered <reason>", "--merged-into <branch>", "--abandon <reason>" },
                    });
        }

        // step 4: the work tree must be clean
        CheckClean(c, prior);
        if (verdict == LocalOnly)
        {
            // the unshipped commits are the run's only copy: nothing is archived, so the branch and work tree stay
            warnings.Add($"local-only: the run's rules say it never pushes; {count} commit(s) of {branch} are not in {release.Ref} and stay on {branch}");
            return new Plan(verdict, reason, head, count, false, null, null, warnings);
        }

        var (archive, skipped, archiveWarnings) = PlanArchive(c, dir, team, name, root, branch, head, prior, streams, IsThis);
        warnings.AddRange(archiveWarnings);
        return new Plan(verdict, reason, head, count, true, archive, skipped, warnings);
    }

    /// <summary>
    /// <c>--merged-into X</c>: X is golden's local name (when golden resolves) or the branch of a live stream of any team, and
    /// <c>git cherry X branch</c> has no <c>+</c> lines.
    /// </summary>
    private static string MergedInto(
        RosterContext c, string root, BranchHygiene.ReleaseRef release, string branch, string merged, List<BranchHygiene.StreamRow> streams, Func<BranchHygiene.StreamRow, bool> isThis)
    {
        var golden = release.Active ? BranchHygiene.BranchPart(c, root, release.Ref!) : null;
        string? target = null;
        if (merged != branch && streams.Any(r => !isThis(r) && r.Live && r.Branch == merged) && BranchHygiene.HeadOf(c, root, merged) is not null)
        {
            target = "refs/heads/" + merged;
        }
        else if (golden is not null && merged == golden)
        {
            target = release.Ref!;
        }

        if (target is null)
        {
            var allowed = golden is null ? "the branch of a live stream" : $"{golden} nor the branch of a live stream";
            throw Refuse(c, "merged-into-invalid", $"--merged-into {merged} is neither {allowed}", new JsonObject { ["branch"] = merged });
        }

        var missing = Count(c, root, target, "refs/heads/" + branch);
        if (missing.Failure is { } why)
        {
            throw Refuse(c, "git-failed", $"git cherry {merged} {branch} failed: {why}");
        }

        if (missing.Count > 0)
        {
            throw Refuse(
                c,
                "merged-into-incomplete",
                $"{missing.Count} commit(s) of {branch} are not in {merged}",
                new JsonObject { ["missing"] = missing.Count, ["commits"] = missing.LinesJson() },
                $"merged-into-incomplete {missing.Count}");
        }

        return "merged-into:" + merged;
    }

    /// <summary>Step 4: a work tree that still exists must have an empty <c>git status --porcelain</c>.</summary>
    private static void CheckClean(RosterContext c, JsonValue prior)
    {
        if (MemberPlanning.Text(prior.Member("worktree")) is not { Length: > 0 } tree || !c.Services.Files.Exists(tree))
        {
            return;
        }

        var dirty = BranchHygiene.DirtyPaths(c, tree) ?? throw Refuse(c, "git-failed", $"git status failed in {tree}");
        if (dirty.Count > 0)
        {
            var paths = new JsonArray();
            paths.AddRange(dirty.Select(p => (JsonValue)p));
            throw Refuse(c, "worktree-dirty", $"{tree} has uncommitted changes ({string.Join(", ", dirty)}); commit or remove them", new JsonObject { ["worktree"] = tree, ["paths"] = paths });
        }
    }

    /// <summary>Step 6's decision: archive only when every member of the stream is dismissed or proven gone and no other live stream shares the branch.</summary>
    private static (ArchivePlan? Plan, string? Skipped, List<string> Warnings) PlanArchive(
        RosterContext c, string dir, JsonValue team, string name, string root, string branch, string head, JsonValue prior,
        List<BranchHygiene.StreamRow> streams, Func<BranchHygiene.StreamRow, bool> isThis)
    {
        var warnings = new List<string>();
        if (streams.Any(r => !isThis(r) && r.Live && r.Branch == branch))
        {
            warnings.Add($"branch-shared: another live stream works on {branch}; it is not archived");
            return (null, "branch-shared", warnings);
        }

        // gone, per member: dismissed (no longer a member row) or proven dead, the proof ScratchSweep uses
        var launch = c.Services.Launch!;
        if (launch.Streams.StreamMembers(team, name).Any(m => MemberPlanning.Text(launch.Teardown.MemberLiveness(dir, m, c.TeamFile).Get("liveness")) != "proven_dead"))
        {
            return (null, "member-alive", warnings);
        }

        var tree = MemberPlanning.Text(prior.Member("worktree")) is { Length: > 0 } t && c.Services.Files.Exists(t) ? t : null;
        // a registration whose directory is gone holds nothing (git worktree prune drops it), unless it is locked: git keeps a locked
        // entry for a work tree on removable media, so it still holds the branch
        var holder = BranchHygiene.Worktrees(c, root).FirstOrDefault(w => w.Branch == branch && (w.Locked || c.Services.Files.Exists(w.Path)) && (tree is null || !SamePath(c, w.Path, tree)));
        if (holder.Path is not null)
        {
            warnings.Add($"branch-checked-out: {branch} is checked out at {holder.Path}; it is not archived");
            return (null, "branch-checked-out", warnings);
        }

        var archiveRef = BranchHygiene.ArchiveRef(c, branch);
        if (BranchHygiene.RefSha(c, root, archiveRef) is { } existing && existing != head)
        {
            throw Refuse(c, "archive-ref-exists", $"{archiveRef} already names {existing}, not {head}", new JsonObject { ["ref"] = archiveRef });
        }

        if (tree is not null && WorktreeGuard(c, tree, head) is { } guard)
        {
            warnings.Add(guard.Warning);
            return (null, guard.Skipped, warnings);
        }

        return (new ArchivePlan(root, branch, head, archiveRef, tree), null, warnings);
    }

    /// <summary>The git operations that leave a marker in a work tree's own git dir, and the name each is reported under.</summary>
    private static readonly (string Marker, string Op)[] Operations =
    [
        ("rebase-merge", "rebase"), ("rebase-apply", "rebase"), ("MERGE_HEAD", "merge"),
        ("CHERRY_PICK_HEAD", "cherry-pick"), ("REVERT_HEAD", "revert"), ("BISECT_LOG", "bisect"),
    ];

    /// <summary>
    /// Step 6's work-tree HEAD guard (B1, D13): null when the work tree may be removed; else the skip code and its warning. An operation
    /// in progress fails it; so does a HEAD other than <paramref name="head"/> with commits no branch, tag, remote or archive ref reaches.
    /// A git failure fails it too (fail closed).
    /// </summary>
    internal static (string Skipped, string Warning)? WorktreeGuard(RosterContext c, string tree, string head)
    {
        foreach (var (marker, op) in Operations)
        {
            var path = ReleaseGate.Git(c, tree, "rev-parse", "--path-format=absolute", "--git-path", marker);
            if (!path.Ok)
            {
                return ("worktree-guard-failed", $"worktree-guard-failed: git rev-parse --git-path {marker} failed in {tree}: {path.Detail}; the worktree is kept");
            }

            var p = path.Stdout.Trim();
            if (File.Exists(p) || Directory.Exists(p))
            {
                return ("worktree-op-in-progress", $"worktree-op-in-progress {op}: a {op} is in progress in {tree}; the worktree and branch are kept");
            }
        }

        var current = ReleaseGate.Git(c, tree, "rev-parse", "HEAD");
        if (current.Ok && current.Stdout.Trim() == head)
        {
            return null;
        }

        var stray = ReleaseGate.Git(c, tree, "log", "--format=%h %s", "HEAD", "--not", "--branches", "--tags", "--remotes", "--glob=refs/archive/*");
        if (!current.Ok || !stray.Ok)
        {
            return ("worktree-guard-failed", $"worktree-guard-failed: could not read the HEAD of {tree}: {(current.Ok ? stray.Detail : current.Detail)}; the worktree is kept");
        }

        var lines = stray.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0
            ? null
            : ("worktree-unreachable-commits", $"worktree-unreachable-commits {lines.Length}: {tree} holds commits on no branch, tag, remote or archive ref ({string.Join("; ", lines.Take(Listed))}); put them on the branch or another branch, then re-run");
    }

    private static bool SamePath(RosterContext c, string a, string b)
    {
        string Real(string p)
        {
            try
            {
                return c.Services.Files.RealPath(p);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return Path.GetFullPath(p);
            }
        }

        return Real(a) == Real(b);
    }

    /// <summary>Step 6: <c>git worktree remove</c> (never forced), the archive ref, the head deleted. The archived ref, or null with a warning.</summary>
    internal static string? Archive(RosterContext c, ArchivePlan plan, List<string> warnings)
    {
        if (plan.Worktree is { } tree)
        {
            var removed = ReleaseGate.Git(c, plan.Root, "worktree", "remove", tree);
            if (!removed.Ok)
            {
                warnings.Add($"archive skipped: git worktree remove {tree} failed: {removed.Detail}");
                return null;
            }
        }

        if (BranchHygiene.MoveToArchive(c, plan.Root, plan.Branch, plan.Sha, plan.Ref) is { } failed)
        {
            warnings.Add($"archive incomplete: {failed}");
            return BranchHygiene.RefSha(c, plan.Root, plan.Ref) == plan.Sha ? plan.Ref : null;
        }

        return plan.Ref;
    }

    /// <summary>The verdict fields written onto the stream record (step 5).</summary>
    internal static void Stamp(JsonObject record, Plan plan)
    {
        record["verdict"] = plan.Verdict!;
        record["reason"] = plan.Reason is { } r ? r : JsonValue.Null;
        record["head_sha"] = plan.HeadSha is { } h ? h : JsonValue.Null;
        record["archived_ref"] = JsonValue.Null;
    }
}
