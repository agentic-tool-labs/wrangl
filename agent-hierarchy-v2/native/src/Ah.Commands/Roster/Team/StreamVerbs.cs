using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// <c>stream-open</c> and <c>stream-done</c>: a stream is a named, parallel line of work, a worktree and branch off the base and,
/// under herdr, a tab made when its first peer joins through <c>--stream</c>. Opening spawns nothing and makes no tab.
/// </summary>
internal static class StreamVerbs
{
    private static readonly string[] OpenFlags = ["base", "branch", "no-worktree", "dry-run", "team", "cwd", "orchestrator-pid"];
    private static readonly string[] DoneFlags = ["dry-run", "team", "cwd", .. StreamDoneGate.Flags];

    private static string Quote(string text) => JsonText.Quote(text);

    // ---------------------------------------------------------------- git

    private sealed record GitResult(bool Ok, string Stdout, string Detail);

    private static GitResult Git(RosterContext c, IReadOnlyList<string> argv)
    {
        var result = c.Services.Launch!.Processes.Run(new ProcessRequest(["git", .. argv], Timeout: TimeSpan.FromSeconds(60)));
        var stdout = Encoding.UTF8.GetString(result.Stdout.Span);
        var stderr = Encoding.UTF8.GetString(result.Stderr.Span).Trim();
        var ok = result.ExitCode == 0 && !result.TimedOut;
        return new GitResult(ok, stdout, stderr.Length > 0 ? stderr : result.TimedOut ? "git timed out" : $"git exited with {result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "a signal"}");
    }

    private sealed record Worktree(string Path, string? Branch);

    private static List<Worktree> Worktrees(RosterContext c, string root)
    {
        var result = Git(c, ["-C", root, "worktree", "list", "--porcelain"]);
        if (!result.Ok)
        {
            throw c.Refuse(
                new JsonObject
                {
                    ["refused"] = "git-failed",
                    ["detail"] = result.Detail,
                    ["message"] = $"stream-open: git worktree list failed in {root} — nothing was written",
                },
                $"stream-open: git worktree list failed in {root} — nothing was written");
        }

        var entries = new List<Worktree>();
        string? path = null;
        string? branch = null;
        void Flush()
        {
            if (path is not null)
            {
                entries.Add(new Worktree(path, branch));
            }
        }

        foreach (var line in result.Stdout.Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = line["worktree ".Length..];
                branch = null;
            }
            else if (path is not null && line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = line["branch refs/heads/".Length..];
            }
        }

        Flush();
        return entries;
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

    private sealed record WorktreePlan(string Path, bool Reuse, List<string>? Argv, bool BranchCreated, string? InUseAt, string? BaseRef = null, string? BaseSha = null);

    /// <summary>
    /// What a stream's worktree needs, decided with read-only git. <c>Argv</c> is the <c>git worktree add</c> to run (null when reusing).
    /// Refuses an occupied path; <c>InUseAt</c> names the worktree already holding the branch.
    /// </summary>
    private static WorktreePlan PlanWorktree(RosterContext c, string root, string name, string branch, string baseRef, bool baseChosen)
    {
        var path = System.IO.Path.Combine(root, ".claude", "worktrees", name);
        var entries = Worktrees(c, root);
        var atPath = entries.FirstOrDefault(e => SamePath(c, e.Path, path));
        if (atPath is not null && atPath.Branch == branch)
        {
            var at = Git(c, ["-C", root, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"]).Stdout.Trim();
            return new WorktreePlan(path, true, null, false, null, baseRef.Length > 0 ? baseRef : at, at);
        }

        if (atPath is not null || c.Services.Files.Exists(path))
        {
            var message = $"stream-open: {path} already exists and is not a worktree on branch {branch} — nothing was written";
            throw c.Refuse(new JsonObject { ["refused"] = "worktree-path-occupied", ["path"] = path, ["message"] = message }, message);
        }

        var badRef = branch.StartsWith('-') || !Git(c, ["-C", root, "check-ref-format", "--branch", branch]).Ok ? "branch"
            : baseRef.StartsWith('-') ? "base"
            : null;
        if (badRef is not null)
        {
            var value = badRef == "branch" ? branch : baseRef;
            var message = $"stream-open: --{badRef} {value} is not a usable git ref name — nothing was written";
            throw c.Refuse(new JsonObject { ["refused"] = "bad-ref", ["flag"] = badRef, ["message"] = message }, message);
        }

        var holder = entries.FirstOrDefault(e => e.Branch == branch);
        var tip = Git(c, ["-C", root, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"]);
        var exists = tip.Ok;
        List<string> argv = ["-C", root, "worktree", "add"];
        string? resolvedBase;
        string? baseSha;
        if (exists)
        {
            // An adopted branch sits where it sits: its base_sha is that commit, and its base stays the branch the caller or the default named.
            baseSha = tip.Stdout.Trim();
            resolvedBase = baseRef.Length > 0 ? baseRef : baseSha;
        }
        else
        {
            (resolvedBase, baseSha) = baseChosen ? ResolveBase(c, root, baseRef) : (baseRef, Git(c, ["-C", root, "rev-parse", "--verify", "--quiet", baseRef + "^{commit}"]) is { Ok: true } known ? known.Stdout.Trim() : null);
        }

        argv.AddRange(exists ? [path, branch] : ["-b", branch, path, resolvedBase!]);
        return new WorktreePlan(path, false, argv, !exists, holder?.Path, resolvedBase, baseSha);
    }

    /// <summary>
    /// The ref a new branch is cut from and the commit it names: <paramref name="baseRef"/> as it stands, else <c>origin/</c> of it, else a
    /// refusal. A base that exists only as a remote-tracking branch is used as such, never silently as something older.
    /// </summary>
    private static (string Ref, string? Sha) ResolveBase(RosterContext c, string root, string baseRef)
    {
        List<string> candidates = baseRef.StartsWith("origin/", StringComparison.Ordinal) ? [baseRef] : [baseRef, "origin/" + baseRef];
        foreach (var candidate in candidates)
        {
            var found = Git(c, ["-C", root, "rev-parse", "--verify", "--quiet", candidate + "^{commit}"]);
            if (found.Ok)
            {
                return (candidate, found.Stdout.Trim());
            }
        }

        var message = $"stream-open: --base {baseRef} names no local ref and no origin/{baseRef} — nothing was written";
        throw c.Refuse(new JsonObject { ["refused"] = "base-not-found", ["base"] = baseRef, ["message"] = message }, message);
    }

    /// <summary>
    /// The base a new stream starts from when none is given, as a branch or commit that exists: the branch checked out in the caller's own
    /// work tree (a lane's worktree, not the main checkout), else, on a detached HEAD, the commit checked out there. Needs no branch
    /// called <c>main</c> and ignores <c>origin/HEAD</c>.
    /// </summary>
    private static (string Ref, string Source) DefaultBase(RosterContext c)
    {
        string? Printed(IReadOnlyList<string> args) => Git(c, ["-C", c.Cwd, .. args]) is { Ok: true } r && r.Stdout.Trim() is { Length: > 0 } text ? text : null;

        return Printed(["symbolic-ref", "--quiet", "--short", "HEAD"]) is { } current
            ? (current, "checked-out branch")
            : (Printed(["rev-parse", "--verify", "--quiet", "HEAD"]) ?? "HEAD", "checked-out commit");
    }

    private static string Sha7(string? sha) => sha is { Length: > 7 } ? sha[..7] : sha ?? "unknown";

    /// <summary>
    /// The one warning for adopting a branch that existed before this stream-open: its tip, how far it trails <paramref name="baseRef"/>
    /// (the ref a new branch would have been cut from), and that <c>--base</c> was ignored. The count is left out when git cannot give it.
    /// </summary>
    private static string AdoptionWarning(RosterContext c, string root, string branch, string? tipSha, string baseRef, string? baseFlag)
    {
        var text = $"branch {branch} already existed at {Sha7(tipSha)}";
        if (tipSha is { Length: > 0 } && baseRef.Length > 0
            && Git(c, ["-C", root, "rev-list", "--count", $"{tipSha}..{baseRef}"]) is { Ok: true } behind
            && int.TryParse(behind.Stdout.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            text += n == 0 ? $": up to date with {baseRef}" : $": {n} {(n == 1 ? "commit" : "commits")} behind {baseRef}";
        }

        return baseFlag is { Length: > 0 } ? $"{text}; --base {baseFlag} ignored" : text;
    }

    /// <summary>
    /// The rules a sub-orchestrator's <c>stream-open</c> on its child team obeys before any git write: the stream and branch are named
    /// <c>&lt;lane&gt;-…</c>, the base is the lane's recorded base or the lane's own branch, and the lane has room for another open
    /// stream. Returns the base: the lane base when <c>--base</c> names it, else the lane's branch, else the lane base.
    /// </summary>
    private static string CheckLane(RosterContext c, JsonValue child, string name, string? branchFlag, string? baseFlag, bool needsRoom)
    {
        var link = child.Member("parent");
        var managerName = MemberPlanning.Text(link?.Member("team"));
        var ws = MemberPlanning.Text(link?.Member("stream"));
        if (managerName is null || ws is null)
        {
            throw LaneRefusal(c, "lane-unlinked", "this team is not a lane's child team");
        }

        var prefix = ws + "-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || (branchFlag is not null && !branchFlag.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw LaneRefusal(c, "lane-name", $"a lane stream and its branch must be named {prefix}…; got {(name.StartsWith(prefix, StringComparison.Ordinal) ? branchFlag : name)}");
        }

        var manager = SubOrchestratorCarve.ReadTeam(c, managerName);
        var branchName = branchFlag ?? name;
        if (manager.Member("streams") is { IsMap: true } siblings)
        {
            bool Under(string text, string root) => root.Length > 0 && (text == root || text.StartsWith(root + "-", StringComparison.Ordinal));
            foreach (var key in siblings.AsObject().Keys.Where(k => !Under(ws, k)))
            {
                var recorded = MemberPlanning.Text(siblings.AsObject()[key].Member("branch")) ?? string.Empty;
                if (Under(name, key) || Under(branchName, key) || Under(name, recorded) || Under(branchName, recorded))
                {
                    throw LaneRefusal(c, "lane-name", $"{name} (branch {branchName}) falls under the stream {key} of {managerName}; choose a name that does not");
                }
            }
        }

        if (Ah.Roster.RosterModule.LaneBase(manager, ws) is not { } laneBase)
        {
            throw LaneRefusal(c, "lane-base-missing", $"stream {ws} of {managerName} has no recorded base");
        }

        var laneBranch = manager.Member("streams") is { IsMap: true } laneStreams && laneStreams.AsObject().Get(ws) is { IsMap: true } laneRecord
            ? MemberPlanning.Text(laneRecord.Member("branch"))
            : null;
        if (baseFlag is { Length: > 0 } && baseFlag != laneBase && baseFlag != laneBranch)
        {
            throw LaneRefusal(c, "lane-base-fixed", $"a lane stream branches from the lane base {laneBase} or the lane branch {laneBranch ?? "(none)"}; --base {baseFlag} is refused");
        }

        // b358: a new stream starts at the lane branch's tip, so the lead never fast-forwards it by hand; --base <lane base> still picks the base
        var chosen = baseFlag == laneBase ? laneBase : laneBranch ?? laneBase;

        if (needsRoom)
        {
            var max = Ah.Roster.RosterModule.LaneCaps(manager, ws).Streams;
            var open = child.Member("streams") is { IsMap: true } map
                ? map.AsObject().Keys.Count(k => MemberPlanning.Text(map.AsObject()[k].Member("state")) != "done")
                : 0;
            if (open >= max)
            {
                throw LaneRefusal(c, "lane-streams-full", $"{open}/{max} lane streams are open; close one or ask the top Orchestrator to raise the cap");
            }
        }

        return chosen;
    }

    private static RosterFailure LaneRefusal(RosterContext c, string code, string detail)
    {
        var message = $"stream-open: {code}: {detail} — nothing was written";
        return c.Refuse(new JsonObject { ["refused"] = code, ["message"] = message }, message);
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        array.AddRange(values.Select(v => (JsonValue)v));
        return array;
    }

    private static int RefuseUnavailable(RosterContext c, string verb)
    {
        c.Stderr.WriteLine($"ah: roster {verb} is not implemented natively: the launch layer is not composed");
        return 70;
    }

    // ---------------------------------------------------------------- stream-open

    internal static int Open(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            return RefuseUnavailable(c, "stream-open");
        }

        StreamSupport.RejectUnknownFlags(c, OpenFlags, "stream-open");
        var services = c.Services;
        var launch = services.Launch;
        var name = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        if (name is null || !StreamSupport.NameRe().IsMatch(name))
        {
            var shown = name is null ? "null" : Quote(name);
            var message = $"stream-open: stream name {shown} must match /^[a-z][a-z0-9_-]{{0,39}}$/ — nothing was written";
            throw c.Refuse(
                new JsonObject
                {
                    ["refused"] = "stream-name-invalid",
                    ["name"] = RosterJson.Text(name),
                    ["message"] = message,
                },
                message);
        }

        var branchFlag = StreamSupport.ValueFlag(c, "branch", "stream-open");
        var baseFlag = StreamSupport.ValueFlag(c, "base", "stream-open");
        var dryRun = c.Opts.IsTrue("dry-run");
        var dir = c.Dir;
        TeamScopes.ResolveWritableTeamScope(c, dir);
        var team = services.Roster.ReadTeamRecord(dir, c.TeamFile);
        if (team.IsNull)
        {
            TeamScopes.CheckNewTeamName(c, dir);
        }

        double? ownerPid = team.IsNull ? TeamRecords.NewTeamOwnerPid(c, "stream-open") : null;
        var transport = MemberPlanning.Text(team.Member("transport")) is { Length: > 0 } t ? t : launch.Transports.Detect();
        var rootCwd = team.Member("expected_root") is { IsTruthy: true, IsText: true } er ? er.AsString() : services.Hierarchy.Messages.RealCwd(c.Cwd);
        var streams = launch.Streams.StreamsOf(team);
        var priorValue = streams.Get(name);
        var existed = priorValue is { IsTruthy: true };
        var prior = priorValue ?? JsonValue.Null;
        var reopened = existed && MemberPlanning.Text(prior.Member("state")) == "done";
        var wantWorktree = existed ? prior.Member("worktree") is { IsNull: false } : !c.Opts.IsTrue("no-worktree");
        var branch = existed ? MemberPlanning.Text(prior.Member("branch")) ?? string.Empty : branchFlag is { Length: > 0 } bf ? bf : name;
        string? baseSource = baseFlag is { Length: > 0 } ? "--base" : null;
        if (!team.IsNull && c.Proved().Carve is not null)
        {
            baseFlag = CheckLane(c, team, name, branchFlag, baseFlag, !existed || reopened);
            baseSource = "lane";
        }

        var needsDefaultBase = !existed && baseFlag is not { Length: > 0 };
        var baseRef = existed ? MemberPlanning.Text(prior.Member("base")) ?? string.Empty : baseFlag is { Length: > 0 } bs ? bs : string.Empty;
        var warnings = new List<string>();
        if (existed)
        {
            void Kept(string flag, string what, JsonValue? value) =>
                warnings.Add($"{flag} ignored: stream {name} keeps its recorded {what} {JsonText.Stringify(value ?? JsonValue.Null, 0)}");
            if (branchFlag is not null && branchFlag != MemberPlanning.Text(prior.Member("branch")))
            {
                Kept($"--branch {branchFlag}", "branch", prior.Member("branch"));
            }

            if (baseFlag is not null && baseFlag != MemberPlanning.Text(prior.Member("base")))
            {
                Kept($"--base {baseFlag}", "base", prior.Member("base"));
            }

            if (c.Opts.IsTrue("no-worktree") && prior.Member("worktree") is { IsNull: false })
            {
                Kept("--no-worktree", "worktree", prior.Member("worktree"));
            }
        }

        WorktreePlan? wt = null;
        if (wantWorktree)
        {
            var root = services.Config.CheckoutRoot(c.Cwd);
            if (string.IsNullOrEmpty(root))
            {
                var message =
                    $"stream-open: {c.Cwd} is not inside a git checkout — pass --no-worktree for a stream with no branch; nothing was written";
                throw c.Refuse(
                    new JsonObject
                    {
                        ["refused"] = "git-failed",
                        ["detail"] = $"{c.Cwd} is not inside a git checkout",
                        ["message"] = message,
                    },
                    message);
            }

            var releaseBase = false;
            if (!existed)
            {
                // an explicit --base needs only the override checked (§3.1); golden is looked up only to be the default
                var release = needsDefaultBase ? BranchHygiene.Release(c, root) : BranchHygiene.Configured(c, root) ?? new BranchHygiene.ReleaseRef(BranchHygiene.RefState.Unresolved, null);
                if (release.State == BranchHygiene.RefState.Invalid)
                {
                    throw BranchHygiene.RefuseInvalid(c, "stream-open", release);
                }

                if (needsDefaultBase && release.Active)
                {
                    (baseRef, baseSource, releaseBase) = (release.Ref!, "golden", true);
                }
            }

            if (needsDefaultBase && !releaseBase)
            {
                (baseRef, baseSource) = DefaultBase(c);
                if (baseSource is "checked-out branch")
                {
                    warnings.Add($"note: no --base given; the base is the checked-out branch {baseRef} — pass --base <integration branch> to choose another");
                }
            }

            wt = PlanWorktree(c, root, name, branch, baseRef, baseChosen: !needsDefaultBase || releaseBase);
            if (!wt.BranchCreated)
            {
                baseSource = "existing branch";
                if (!existed)
                {
                    warnings.Add(AdoptionWarning(c, root, branch, wt.BaseSha, baseRef, baseFlag));
                }
            }

            if (dryRun && wt.InUseAt is not null)
            {
                var message = $"stream-open: branch {branch} is checked out at {wt.InUseAt} — nothing was written";
                throw c.Refuse(
                    new JsonObject
                    {
                        ["refused"] = "branch-in-use",
                        ["branch"] = branch,
                        ["detail"] = $"branch {branch} is checked out at {wt.InUseAt}",
                        ["message"] = message,
                    },
                    message);
            }
        }

        string? baseLine = null;
        if (existed)
        {
            if (MemberPlanning.Text(prior.Member("base")) is { Length: > 0 } keptBase)
            {
                baseLine = $"base: {keptBase} @{Sha7(MemberPlanning.Text(prior.Member("base_sha")))} (kept)";
            }
        }
        else if (wt?.BaseRef is { Length: > 0 } openedBase)
        {
            baseLine = $"base: {openedBase} @{Sha7(wt.BaseSha)} ({baseSource})";
            if (baseSource is "checked-out commit")
            {
                warnings.Add("warning: no --base given and HEAD is detached; branched from the checked-out commit — pass --base <integration branch>");
            }
        }

        JsonObject record;
        if (existed)
        {
            record = prior.AsObject().Clone();
            record["state"] = "open";
        }
        else
        {
            record = new JsonObject
            {
                ["state"] = "open",
                ["branch"] = wantWorktree ? branch : JsonValue.Null,
                ["base"] = wantWorktree ? wt?.BaseRef ?? baseRef : JsonValue.Null,
                ["base_sha"] = wt?.BaseSha is { } sha ? sha : JsonValue.Null,
                ["worktree"] = wt is not null ? wt.Path : JsonValue.Null,
                ["tab_id"] = JsonValue.Null,
                ["opened"] = services.Hierarchy.Messages.LocalIso(),
                ["opened_at"] = Ah.Core.Time.Timestamps.ToIsoString(services.Clock.UtcNow),
            };
        }

        record.Remove("closed");
        var rules = launch.Streams;
        var openLabel = $"{rules.StreamGlyph("open", new JsonArray(), [])} {name}";
        var teamFlag = StreamSupport.TeamFlagText(c);
        if (dryRun)
        {
            var herdrArgv = new JsonArray();
            if (transport == "herdr" && record.Get("tab_id") is { IsTruthy: true })
            {
                herdrArgv.Add(Strings(["tab", "list"]));
            }

            var gitArgv = new JsonArray();
            if (wt?.Argv is { } plannedArgv)
            {
                gitArgv.Add(Strings(plannedArgv));
            }

            var dry = new JsonObject
            {
                ["dry_run"] = true,
                ["stream"] = StreamSupport.StreamView(name, record),
                ["would_run"] = new JsonObject { ["git"] = gitArgv, ["herdr"] = herdrArgv },
                ["transport"] = transport,
            };
            if (transport != "herdr")
            {
                dry["degraded"] = "no-tabs";
            }

            if (baseLine is not null)
            {
                dry["base"] = baseLine;
            }

            if (warnings.Count > 0)
            {
                dry["warnings"] = Strings(warnings);
            }

            c.Out(dry);
            return 0;
        }

        if (wt?.Argv is { } argv)
        {
            var added = Git(c, argv);
            if (!added.Ok)
            {
                if (wt.InUseAt is not null)
                {
                    var inUse = $"stream-open: branch {branch} is checked out at {wt.InUseAt} — nothing was written";
                    throw c.Refuse(
                        new JsonObject { ["refused"] = "branch-in-use", ["branch"] = branch, ["detail"] = added.Detail, ["message"] = inUse },
                        inUse);
                }

                var failed = $"stream-open: git worktree add failed — nothing was written: {added.Detail}";
                throw c.Refuse(new JsonObject { ["refused"] = "git-failed", ["detail"] = added.Detail, ["message"] = failed }, failed);
            }
        }

        var snap = new StreamSupport.Snapshot(null, null);
        var tabCreated = false;
        if (transport == "herdr" && record.Get("tab_id") is { IsTruthy: true })
        {
            snap = StreamSupport.HerdrSnapshot(c, warnings);
            var made = StreamSupport.EnsureStreamTab(c, record, openLabel, rootCwd, snap.Tabs);
            if (made.TabId is not null)
            {
                record["tab_id"] = made.TabId;
                if (made.Created && made.RootPane is not null)
                {
                    record["root_pane_id"] = made.RootPane;
                }

                tabCreated = made.Created;
            }
            else
            {
                record["tab_id"] = JsonValue.Null;
                warnings.Add($"stream {name}: herdr tab create failed ({made.Error}); the worktree is kept, and the next spawn --stream {name} creates the tab");
            }
        }
        else
        {
            record["tab_id"] = JsonValue.Null;
        }

        JsonValue written;
        IReadOnlyList<string> registered = [];
        if (!team.IsNull)
        {
            written = StreamSupport.WriteStreamRecord(c, dir, name, record);
        }
        else
        {
            var found = services.Config.ResolveRoster(c.Cwd, services.Roster.TeamRosterKey(dir, c.TeamFile), c.RepoBasename, c.Registry);
            var layout = CreatePlanning.CreateLayout(c);
            var fresh = TeamRecords.NewTeamRecord(
                c, TeamScopes.AdHocRoster(found), transport, ownerPid ?? double.NaN, MemberPlanning.Text(layout, "mode") ?? "auto");
            fresh["streams"] = new JsonObject { [name] = record };
            written = fresh;
            services.Hierarchy.StateCalls.UpdateTeam(
                dir,
                c.TeamFile,
                doc =>
                {
                    if (!doc.IsNull && services.Roster.TeamIsLive(doc, c.Invoker(), services.Clock.UtcNow))
                    {
                        throw TeamScopes.RefuseLiveTeam(c, dir, doc);
                    }

                    return TeamFileOutcome.Write(fresh);
                });
            registered = TeamRegistration.RegisterTeam(c, dir, fresh);
        }

        try
        {
            var treePath = MemberPlanning.Text(record, "worktree");
            var info = new Ah.Hierarchy.ProgressStreamInfo(
                "—",
                MemberPlanning.Text(record, "branch") is { Length: > 0 } recordedBranch ? recordedBranch : "—",
                treePath is { Length: > 0 } ? Path.GetFileName(treePath.TrimEnd('/')) : "—",
                null);
            services.Hierarchy.Progress.EnsureStream(c.ProgressPool, c.ProgressTeam, name, info);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            warnings.Add($"progress: the stream's checklist was not created: {error.Message}");
        }

        RosterLedger.Train(c, "stream-open", name, $"stream {name} opened", MemberPlanning.Text(record, "branch"), MemberPlanning.Text(record, "base"));

        JsonValue label = JsonValue.Null;
        if (transport == "herdr" && record.Get("tab_id") is { IsTruthy: true })
        {
            label = tabCreated ? openLabel : RosterJson.Text(StreamSupport.RenderStream(c, dir, written, name, snap, warnings).Label);
        }

        var next = new JsonArray
        {
            $"ah roster spawn-one <role> --stream {name}{teamFlag}",
            $"ah roster spawn-ad-hoc <role> --stream {name}{teamFlag}",
        };
        var result = new JsonObject
        {
            ["opened"] = true,
            ["existed"] = existed,
            ["reopened"] = reopened,
            ["stream"] = StreamSupport.StreamView(name, record),
            ["label"] = label,
            ["worktree_created"] = wt?.Argv is not null,
            ["branch_created"] = wt?.Argv is not null && wt.BranchCreated,
            ["transport"] = transport,
        };
        if (transport != "herdr")
        {
            result["degraded"] = "no-tabs";
        }

        if (baseLine is not null)
        {
            result["base"] = baseLine;
        }

        if (warnings.Count > 0)
        {
            result["warnings"] = Strings(warnings);
        }

        TeamRegistration.Report(result, registered, c);
        result["next"] = next;
        c.Out(result);
        return 0;
    }

    // ---------------------------------------------------------------- stream-done

    /// <summary>
    /// Marks the stream done and shows the check mark. Closes no pane: members leave through the two-phase <c>dismiss</c>, when the
    /// user asks. Against golden it first refuses unshipped work (<see cref="StreamDoneGate"/>), and archives the branch and
    /// removes a clean worktree only once every member of the stream is gone.
    /// </summary>
    internal static int Done(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            return RefuseUnavailable(c, "stream-done");
        }

        StreamSupport.RejectUnknownFlags(c, DoneFlags, "stream-done");
        var services = c.Services;
        var launch = services.Launch;
        var rules = launch.Streams;
        var name = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        var dir = c.Dir;
        var team = services.Roster.ReadTeamRecord(dir, c.TeamFile);
        var priorValue = name is null ? null : rules.StreamsOf(team).Get(name);
        if (priorValue is not { IsTruthy: true } prior || name is null)
        {
            var shown = name is null ? "null" : Quote(name);
            var message = $"stream-done: no stream named {shown} in {services.Roster.TeamPath(dir, c.TeamFile)}";
            throw c.Refuse(
                new JsonObject { ["refused"] = "unknown-stream", ["stream"] = RosterJson.Text(name), ["message"] = message },
                message);
        }

        var dryRun = c.Opts.IsTrue("dry-run");
        var transport = MemberPlanning.Text(team.Member("transport")) is { Length: > 0 } t ? t : launch.Transports.Detect();
        var alreadyDone = MemberPlanning.Text(prior.Member("state")) == "done";
        var gate = StreamDoneGate.Check(c, dir, team, name, prior, alreadyDone);
        JsonObject record;
        if (alreadyDone)
        {
            record = prior.AsObject();
        }
        else
        {
            record = prior.AsObject().Clone();
            record["state"] = "done";
            record["closed"] = services.Hierarchy.Messages.LocalIso();
            record["done_at"] = Ah.Core.Time.Timestamps.ToIsoString(services.Clock.UtcNow);
            if (gate.Verdict is not null)
            {
                StreamDoneGate.Stamp(record, gate);
            }
        }

        JsonValue written;
        if (alreadyDone || dryRun)
        {
            var copy = team.AsObject().Clone();
            var all = rules.StreamsOf(team).Clone();
            all[name] = record;
            copy["streams"] = all;
            written = copy;
        }
        else
        {
            written = StreamSupport.WriteStreamRecord(c, dir, name, record);
        }

        var warnings = new List<string>(gate.Warnings);
        if (!alreadyDone && !dryRun)
        {
            var doneText = gate.Verdict is null ? $"stream {name} done" : $"stream {name} done: {gate.Verdict}{(gate.Reason is { } doneReason ? $" ({doneReason})" : string.Empty)}";
            RosterLedger.Train(c, "stream-done", name, doneText, MemberPlanning.Text(record, "branch"), MemberPlanning.Text(record, "base"), gate.Verdict, gate.HeadSha);
            try
            {
                var closed = services.Hierarchy.Progress.MarkDone(c.ProgressPool, c.ProgressTeam, name);
                if (closed.InProgress > 0)
                {
                    warnings.Add($"stream {name}: its checklist still lists {closed.InProgress} item(s) In progress ({Ah.Core.Text.NodePath.Join(c.ProgressPool, "progress", c.ProgressTeam, name + ".md")})");
                }

                if (!closed.Changed && closed.Reason is { } why && why.StartsWith("the checklist ", StringComparison.Ordinal) && why != "the checklist has no State: line")
                {
                    warnings.Add($"stream {name}: its checklist State: was not set to done: {why}");
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                warnings.Add($"stream {name}: its checklist State: was not set to done: {error.Message}");
            }
        }

        string? archivedRef = null;
        if (gate.Archive is { } archivePlan && !dryRun)
        {
            archivedRef = StreamDoneGate.Archive(c, archivePlan, warnings);
            if (archivedRef is not null)
            {
                record = record.Clone();
                record["archived_ref"] = archivedRef;
                written = StreamSupport.WriteStreamRecord(c, dir, name, record);
            }
        }

        if (transport == "herdr" && record.Get("tab_id") is { IsTruthy: true } && !dryRun)
        {
            StreamSupport.RenderStream(c, dir, written, name, StreamSupport.HerdrSnapshot(c, warnings, wantAgents: false), warnings);
        }

        var memberNames = rules.StreamMembers(team, name).Select(m => MemberPlanning.JsConvertText(m.Member("name"))).ToList();
        var memberSet = new HashSet<string>(memberNames, StringComparer.Ordinal);
        var open = new JsonArray();
        foreach (var e in StreamSupport.TeamOpenExchanges(c, dir))
        {
            if (memberSet.Contains(MemberPlanning.JsConvertText(e.Member("to_name"))) || memberSet.Contains(MemberPlanning.JsConvertText(e.Member("from_name"))))
            {
                open.Add(e);
            }
        }

        var tabOutcome = dryRun ? null : StreamTabs.CloseEmpty(c, team, [name], evenIfOpen: true, warnings).FirstOrDefault();
        var laneWarnings = new List<string>();
        var reverted = alreadyDone || dryRun
            ? []
            : LaneDissolve.Revert(c, dir, c.TeamFile ?? services.Config.TeamPrefix(c.Cwd, null), c.TeamFile, name, MemberPlanning.Text(prior.Member("sub_orchestrator")), laneWarnings);
        var result = new JsonObject { ["done"] = true, ["already_done"] = alreadyDone };
        if (dryRun)
        {
            result["dry_run"] = true;
        }

        result["stream"] = StreamSupport.StreamView(name, record);
        result["label"] = transport == "herdr" && record.Get("tab_id") is { IsTruthy: true } ? $"✓ {name}" : JsonValue.Null;
        if (tabOutcome is { } tab)
        {
            StreamTabs.Report(result, tab);
        }

        result["members"] = Strings(memberNames);
        if (reverted.Count > 0)
        {
            result["reverted"] = Strings(reverted);
        }

        if (laneWarnings.Count > 0)
        {
            result["lane_warnings"] = Strings(laneWarnings);
        }

        result["open_exchanges"] = open;
        var nextSteps = memberNames.Select(m => $"ah roster dismiss {m}{StreamSupport.TeamFlagText(c)}").ToList();
        if (gate.Verdict is not null)
        {
            result["verdict"] = gate.Verdict;
            if (gate.Unshipped is { } count)
            {
                result["unshipped"] = count;
            }

            result["archived_ref"] = archivedRef is not null ? archivedRef : RosterJson.OrNull(record.Get("archived_ref"));
            if (dryRun && gate.Archive is { } would)
            {
                result["would_archive"] = new JsonObject
                {
                    ["ref"] = would.Ref,
                    ["sha"] = would.Sha,
                    ["worktree_removed"] = would.Worktree is { } w ? w : JsonValue.Null,
                    ["head_deleted"] = "refs/heads/" + would.Branch,
                };
            }
            else if (gate.ArchiveSkipped is { } skipped)
            {
                result["archive_skipped"] = skipped;
            }

            if (gate.Archivable && archivedRef is null && record.Get("archived_ref") is not { IsText: true })
            {
                // the branch and worktree stay until every member is gone and the work-tree HEAD guard passes; a re-run then archives them
                var when = gate.ArchiveSkipped switch
                {
                    "worktree-unreachable-commits" => "after putting the listed commits on the branch or another branch",
                    "worktree-op-in-progress" => "after finishing or aborting the git operation in the worktree",
                    _ => "after the dismissals",
                };
                nextSteps.Add($"ah roster stream-done {name}{StreamSupport.TeamFlagText(c)}   ({when}: removes the worktree and archives {MemberPlanning.Text(record, "branch")}; or run branch-sweep.sh)");
            }
        }

        result["next"] = Strings(nextSteps);
        if (warnings.Count > 0)
        {
            result["warnings"] = Strings(warnings);
        }

        c.Out(result);
        return 0;
    }
}
