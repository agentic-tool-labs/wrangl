using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy.StateCalls;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// <c>resume</c>: plan the relaunch of a snapshot's members (the default, read-only), or with <c>--yes</c> launch each one that can be,
/// from its own worktree and into its session when the snapshot has one, then make the resuming session the team's orchestrator.
/// </summary>
internal static class ResumeVerb
{
    private static readonly string[] Flags = ["cwd", "team", "yes", "orchestrator-pid", "allow-main", "checkpoint", "relaunch", "relaunch-missing", "all", "force", "hold"];
    private static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(120000);

    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster resume is not implemented natively: the launch layer is not composed");
            return 70;
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"resume: unrecognized flag --{key} (use --yes, --team, --cwd, --orchestrator-pid, or --force)");
            }
        }

        if (c.Opts.IsTrue("hold"))
        {
            return ResumeHold.Run(c);
        }

        if (c.Opts.IsTrue("all") && !c.Opts.IsTrue("checkpoint"))
        {
            return ResumeAll.Run(c);
        }

        HoldRefusal.RefuseIfHeld(c);
        if (c.Opts.IsTrue("checkpoint"))
        {
            return ResumeCheckpoint.Run(c);
        }

        if (c.Opts.Has("relaunch") || c.Opts.Has("relaunch-missing"))
        {
            throw new RosterFailure("resume: --relaunch and --relaunch-missing only go with --checkpoint");
        }

        var services = c.Services;
        var launch = services.Launch;
        var roster = services.Roster;
        var dir = c.Dir;
        var arg = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        string snapPath;
        if (arg is not null)
        {
            snapPath = arg.Contains('/', StringComparison.Ordinal) ? NodePath.Resolve(c.Cwd, arg) : NodePath.Join(roster.SnapshotsHome(dir), arg);
        }
        else
        {
            var found = roster.ListSnapshots(dir, launch.SnapshotRules.SnapshotTeamName(c.TeamFile));
            if (found.Count == 0)
            {
                var team = c.TeamFile is null ? "(the default team)" : $"\"{c.TeamFile}\"";
                throw new RosterFailure($"resume: no snapshot of team {team} in {roster.SnapshotsHome(dir)} — take one with `snapshot`");
            }

            snapPath = MemberPlanning.JsConvertText(found[0].Get("path"));
        }

        var doc = roster.ReadSnapshot(snapPath);
        if (!doc.IsMap)
        {
            throw new RosterFailure($"resume: {snapPath} is not a team snapshot");
        }

        var docTeam = doc.Member("team") is { IsTruthy: true, IsText: true } dt ? dt.AsString() : null;
        if (c.Opts.Text("team") is { } wanted && wanted != (docTeam ?? RosterModuleDefault()))
        {
            throw new RosterFailure(
                $"resume: --team {wanted} does not match the snapshot's team {(docTeam is null ? RosterModuleDefault() : docTeam)}");
        }

        c.TeamFile = docTeam;
        c.RepoBasename = services.Config.TeamPrefix(c.Cwd, docTeam);
        var plan = launch.SnapshotRules.PlanMembers(doc, id => launch.Snapshots.AliveReason(id));
        var opus = services.Config.Tier.Get("opus") is { IsNumber: true } o ? (int)o.AsNumber() : 3;
        var summary = new JsonObject
        {
            ["snapshot"] = snapPath,
            ["team"] = RosterJson.Text(docTeam),
            ["to_launch"] = plan.Count(p => MemberPlanning.Text(p.Member("status")) is "resume" or "fresh"),
            ["top_tier"] = launch.SnapshotRules.TopTierCount(plan, model => services.Config.TierOf(model), opus),
        };
        var planned = new JsonArray();
        foreach (var p in plan)
        {
            var row = new JsonObject();
            Put(row, "name", p.Member("name"));
            Put(row, "role", p.Member("role"));
            Put(row, "model", p.Member("model"));
            Put(row, "status", p.Member("status"));
            if (p.Member("why") is { IsTruthy: true } why)
            {
                row["why"] = why;
            }

            planned.Add(row);
        }

        summary["members"] = planned;
        if (!c.Opts.IsTrue("yes"))
        {
            var preview = new JsonObject { ["status"] = "planned" };
            RosterJson.Spread(preview, summary);
            c.Out(preview);
            return 0;
        }

        var done = Transfer(c, () => ResumeMembers(c, doc, null));
        if (done.Refusal is { } refusal)
        {
            return refusal;
        }

        var output = new JsonObject { ["status"] = "resumed" };
        if (done.Session is null)
        {
            output["session"] = "unproven";
        }

        RosterJson.Spread(output, summary);
        var rows = new JsonArray();
        rows.AddRange(done.Results.Select(r => (JsonValue)r));
        output["members"] = rows;
        if (done.Refused is { } held3)
        {
            output["ownership_refused"] = OwnershipRefusal(held3);
        }

        c.Out(output);
        return done.Results.Any(static r => r.Get("status") is { IsText: true } status && status.AsString() == "failed") ? 3 : 0;
    }

    /// <summary>What the Orchestrator hand-over did: the exit code when it refused, else what the member step returned, the proved session, whether the record already named this process, and the live holder that kept the record.</summary>
    internal sealed record Handover(int? Refusal, List<JsonObject> Results, string? Session, bool AlreadyBound, double? Refused);

    internal static string OwnershipRefusal(double held) =>
        $"held by live Orchestrator pid {JsConvert.ToJsString(JsonValue.FromNumber(held))}; its record was left as it is";

    /// <summary>
    /// Proves the process above this command can be the team's Orchestrator, runs <paramref name="launchMembers"/> before the record
    /// changes, and records that process as the Orchestrator unless a live other one holds the team.
    /// </summary>
    internal static Handover Transfer(RosterContext c, Func<List<JsonObject>> launchMembers)
    {
        var services = c.Services;
        var roster = services.Roster;
        var dir = c.Dir;
        var proof = Prove(c);
        if (proof.Refusal is { } refusal)
        {
            return Stopped(refusal);
        }

        var me = proof.Me;
        var start = proof.Start!;
        var session = proof.Session;
        var invoker = proof.Invoker!;
        var existing = roster.ReadTeamRecord(dir, c.TeamFile);
        var force = c.Opts.IsTrue("force");
        RefuseTakeover(c, existing, invoker);
        var results = launchMembers();
        double? refused = null;
        var alreadyBound = false;
        services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            current =>
            {
                refused = null;
                alreadyBound = false;
                if (current.IsNull)
                {
                    return null;
                }

                var orchestrator = current.Member("orchestrator");
                if (orchestrator is { IsMap: true } o2
                    && o2.Member("pid") is { IsNumber: true } p2 && p2.AsNumber() == me
                    && o2.Member("pid_start") is { IsText: true } recordedStart
                    && Ah.Core.Time.ProcessStart.IsSame(Ah.Core.Time.ProcessStart.Compare(start, () => null, recordedStart.AsString()))
                    && (session is null || o2.Member("session_id") is { IsText: true } recordedSession && recordedSession.AsString() == session))
                {
                    alreadyBound = true;
                    return null;
                }

                var heldValue = RosterJson.Member(orchestrator, "pid");
                var held = heldValue is { } hv2 ? JsConvert.ToNumber(hv2) : double.NaN;
                var currentLife = roster.TeamLifeOf(current);
                if (OrchestratorSession.IsClaimed(orchestrator) && held != me && currentLife != TeamLife.Orphaned && !(force && currentLife == TeamLife.Indeterminate) && !roster.IsTeamOwnedBy(current, invoker))
                {
                    refused = held;
                    return null;
                }

                var next = current.AsObject().Clone();
                next["orchestrator"] = new JsonObject
                {
                    ["session_id"] = session is null ? JsonValue.Null : session,
                    ["pid"] = me,
                    ["pid_start"] = start,
                };
                next.Remove("duty");
                return TeamFileOutcome.Write(next);
            });
        OrchestratorSession.Rebind(c, me);
        return new Handover(null, results, session, alreadyBound, refused);
    }

    /// <summary>Refuses, with nothing written, unless the team's Orchestrator is this session, is gone, or (with <c>--force</c>) cannot be proven alive.</summary>
    internal static void RefuseTakeover(RosterContext c, JsonValue existing, TeamInvoker invoker)
    {
        var roster = c.Services.Roster;
        var holderValue = RosterJson.Member(existing.Member("orchestrator"), "pid");
        var holder = holderValue is { } hv ? JsConvert.ToNumber(hv) : double.NaN;
        var (heldLife, heldWhy) = existing.IsNull ? (TeamLife.Orphaned, string.Empty) : roster.TeamLifeWhy(existing);
        if (!existing.IsNull && OrchestratorSession.IsClaimed(existing.Member("orchestrator")) && heldLife != TeamLife.Orphaned && !(c.Opts.IsTrue("force") && heldLife == TeamLife.Indeterminate) && !roster.IsTeamOwnedBy(existing, invoker))
        {
            var heldPid = RosterContext.IsInteger(holder) ? "pid " + JsConvert.ToJsString(JsonValue.FromNumber(holder)) : "no usable pid";
            throw new RosterFailure(heldLife == TeamLife.Indeterminate
                ? $"resume: the Orchestrator ({heldPid}) cannot be proven gone ({heldWhy}); nothing was launched. Restart that session, or run resume --force to take the team over anyway."
                : $"resume: the team is held by a live Orchestrator ({heldPid}; {heldWhy}); nothing was launched. Restart that session, or run resume from it.");
        }
    }

    private static Handover Stopped(int code) => new(code, [], null, false, null);

    /// <summary>Who the process above this command is: the exit code when it cannot be proved to be an Orchestrator's session, else its pid, start time, session and the invoker the team ownership check takes.</summary>
    internal sealed record Proof(int? Refusal, double Me, string? Start, string? Session, TeamInvoker? Invoker);

    internal static Proof Prove(RosterContext c)
    {
        if (c.Services.OwnerProof is null)
        {
            c.Stderr.WriteLine("ah: roster resume is not implemented natively: the owner proof is not provided");
            return new Proof(70, 0, null, null, null);
        }

        if (c.EnvText(MeshModule.MemberMarkerEnv) is not null)
        {
            return new Proof(
                Refuse(
                    c,
                    "member-cannot-resume",
                    "member-marker",
                    "ah: roster resume: this shell carries a member marker, and a member session cannot become an Orchestrator; nothing was launched or written.",
                    "owner proof: this shell carries a member marker."),
                0,
                null,
                null,
                null);
        }

        var proved = c.Proved();
        var ancestor = proved.Nearest;
        if (Unproven(c, ancestor) is { } refusal)
        {
            return new Proof(refusal, 0, null, null, null);
        }

        var start = proved.PidStart;
        if (start is null)
        {
            return new Proof(
                Refuse(
                    c,
                    "owner-unproven",
                    "unproven",
                    $"ah: roster resume: could not read the start time of pid {ancestor.Pid}, so it cannot be recorded as the Orchestrator; nothing was launched or written.",
                    "owner proof: the process table could not be read."),
                0,
                null,
                null,
                null);
        }

        return new Proof(null, ancestor.Pid, start, proved.SessionId, new TeamInvoker(ancestor.Pid, proved.SessionId, start));
    }

    internal static string RosterModuleDefault() => "@default";

    /// <summary>
    /// Refuses when the process above this command is not one that can become the team's Orchestrator, or when a pid the caller
    /// states (<c>--orchestrator-pid</c>, <c>CLAUDE_PID</c>) is not that process. The stated pids only cross-check the proved one.
    /// Returns the exit code, or null when the proved process may resume.
    /// </summary>
    private static int? Unproven(RosterContext c, NearestSession nearest)
    {
        const string Unprovable = "ah: roster resume: could not prove which Claude session runs this command; nothing was launched or written. Run it inside the Orchestrator's Claude session, not in a plain terminal.";
        switch (nearest.Kind)
        {
            case NearestKind.Unavailable:
                return Refuse(c, "owner-unproven", "unproven", Unprovable, "owner proof: the process table could not be read.");
            case NearestKind.None:
                return Refuse(c, "owner-unproven", "no-orchestrator", Unprovable, "owner proof: no registered session is an ancestor of this command.");
            case NearestKind.Member:
                return Refuse(
                    c,
                    "member-cannot-resume",
                    "member-ancestor",
                    $"ah: roster resume: this command runs under member {nearest.Member}, and a member session cannot become an Orchestrator; nothing was launched or written.",
                    $"owner proof: this command runs under member {nearest.Member}.");
        }

        foreach (var (name, stated) in new[] { ("--orchestrator-pid", c.Opts.Text("orchestrator-pid")), ("CLAUDE_PID", c.EnvText("CLAUDE_PID")) })
        {
            if (stated is null || JsConvert.ToNumber(JsonValue.FromString(stated)) == nearest.Pid)
            {
                continue;
            }

            return Refuse(
                c,
                "orchestrator-pid-mismatch",
                nearest.Kind == NearestKind.Session ? "session" : "orchestrator",
                $"ah: roster resume: {name} names pid {stated}, but this command runs under pid {nearest.Pid}; nothing was launched or written.",
                $"owner proof: {name} is {stated}; the session above this command is pid {nearest.Pid}.");
        }

        return null;
    }

    private static int Refuse(RosterContext c, string refused, string proof, string first, string why)
    {
        c.Out(new JsonObject { ["status"] = "refused", ["refused"] = refused, ["proof"] = proof });
        c.Stderr.WriteLine(first);
        c.Stderr.WriteLine(why);
        return 2;
    }

    private static void Put(JsonObject target, string key, JsonValue? value)
    {
        if (value is { } v)
        {
            target[key] = v;
        }
    }

    /// <summary>
    /// Launches the members of the snapshot that can be launched, one at a time under a per-team lock, from asking whether a member's
    /// session is alive to its launch, so two resumes cannot both launch one session.
    /// </summary>
    internal static List<JsonObject> ResumeMembers(RosterContext c, JsonValue doc, Func<string, bool>? only, Func<string, string?>? intoPane = null, Func<string, string?>? knownSession = null, Func<string, string?>? retirePane = null)
    {
        c.RequirePreflightDryRun();
        var dryRun = c.Opts.IsTrue("dry-run");
        var services = c.Services;
        var launch = services.Launch!;
        var results = new List<JsonObject>();
        var savedCwd = c.Cwd;
        var teamPath = services.Roster.TeamPath(services.Roster.TeamHome(c.Dir), c.TeamFile);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NodePath.Resolve(c.Cwd, teamPath)))).ToLowerInvariant()[..16];
        var lockFile = NodePath.Join(services.Environment.AhHome, "run", $"resume-{hash}.lock");
        var trapped = c.TrapRefusals;
        c.TrapRefusals = true;
        try
        {
            var members = (doc.Member("members") is { IsArray: true } list ? list.AsArray().Select(MemberPlanning.Obj).ToList() : [])
                .Where(m => only is null || MemberPlanning.Text(m, "name") is { } who && only(who))
                .ToList();
            if (!dryRun && doc.Member("gate_mode") is { IsText: true } gm && gm.AsString() is "off" or "log" or "enforce")
            {
                if (WorkTreeAssignment.Restore(c, gm.AsString()) is { } restoreWarning)
                {
                    c.Warn(restoreWarning);
                    services.HookErrors?.Log(new HookErrorRecord("roster-resume", restoreWarning));
                }
            }

            // a streamed member needs its stream in the team file, and a disbanded team has no file until a member without a stream creates it
            members = members.OrderBy(m => m.Get("stream") is { IsText: true } s && s.AsString().Length > 0 ? 1 : 0).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
            foreach (var m in members)
            {
                using var held = launch.ResumeLocks.Acquire(lockFile, LockWait);
                if (held is null)
                {
                    var failed = new JsonObject();
                    Put(failed, "name", m.Get("name"));
                    Put(failed, "role", m.Get("role"));
                    Put(failed, "model", m.Get("model"));
                    failed["status"] = "failed";
                    failed["why"] = "another resume of this team holds its lock";
                    results.Add(failed);
                    continue;
                }

                if (!dryRun)
                {
                    RestoreStream(c, doc, m);
                }

                var who = MemberPlanning.Text(m, "name");
                results.Add(ResumeOne(c, m, savedCwd, who is null ? null : intoPane?.Invoke(who), who is null ? null : knownSession?.Invoke(who), who is null ? null : retirePane?.Invoke(who)));
            }
        }
        finally
        {
            c.TrapRefusals = trapped;
            c.Cwd = savedCwd;
        }

        return results;
    }

    /// <summary>
    /// Writes back the snapshot's record of the member's stream when the team file has none (a disband removed the file), as an open
    /// stream with no tab, so the launch places it afresh. A team file that does not exist yet, or a record whose work tree is gone,
    /// is left alone and the launch refuses as it would for any unknown stream.
    /// </summary>
    private static void RestoreStream(RosterContext c, JsonValue doc, JsonObject m)
    {
        if (m.Get("stream") is not { IsText: true } named || named.AsString().Length == 0
            || doc.Member("streams")?.Member(named.AsString()) is not { IsMap: true } saved)
        {
            return;
        }

        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (team.IsNull || c.Services.Launch!.Streams.StreamsOf(team).Get(named.AsString()) is { IsTruthy: true })
        {
            return;
        }

        if (MemberPlanning.Text(saved.Member("worktree")) is { Length: > 0 } tree && c.Services.Files.Stat(tree) is not { Kind: FileKind.Directory })
        {
            return;
        }

        var record = saved.AsObject().Clone();
        record["state"] = "open";
        record["tab_id"] = JsonValue.Null;
        record.Remove("root_pane_id");
        StreamSupport.WriteStreamRecord(c, c.Dir, named.AsString(), record);
    }

    /// <summary>The stream a member rejoins on resume: its snapshot stream when the team file holds that stream and it is open, else null.</summary>
    private static SpawnOne.StreamRef? OpenStreamOf(RosterContext c, JsonObject m)
    {
        if (m.Get("stream") is not { IsText: true } named || named.AsString().Length == 0)
        {
            return null;
        }

        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        var record = c.Services.Launch!.Streams.StreamsOf(team).Get(named.AsString());
        return record is { IsTruthy: true } found && MemberPlanning.Text(found.Member("state")) == "open"
            ? new SpawnOne.StreamRef(named.AsString(), found)
            : null;
    }

    /// <summary>
    /// The directory a resumed member launches in and where that came from, the first that holds: its recorded work tree
    /// (<c>work_root</c>), the work tree of its open stream (<c>stream</c>), the work tree of the newest roster row of its
    /// session (<c>last_cwd</c>), its expected root (<c>expected_root</c>).
    /// </summary>
    private static (string Dir, string From) LaunchHome(RosterContext c, JsonObject m, string status, string? recordedRoot, JsonValue? openStream)
    {
        var messages = c.Services.Hierarchy.Messages;
        if (recordedRoot is not null)
        {
            return (messages.RealCwd(recordedRoot), "work_root");
        }

        if (openStream is { } stream
            && MemberPlanning.Text(stream.Member("worktree")) is { Length: > 0 } tree
            && c.Services.Files.Stat(tree) is { Kind: FileKind.Directory })
        {
            return (messages.RealCwd(tree), "stream");
        }

        if (status == "resume" && m.Get("session_id") is { IsText: true } session && session.AsString().Length > 0
            && LastWorkTree(c, session.AsString()) is { } last)
        {
            return (messages.RealCwd(last), "last_cwd");
        }

        return (messages.RealCwd(MemberPlanning.JsConvertText(m.Get("expected_root"))), "expected_root");
    }

    /// <summary>
    /// The work tree holding the <c>cwd</c> of the newest roster row of session <paramref name="sessionId"/>, searched in the team
    /// home, the main hierarchy dir and the hierarchy dir of every linked work tree (a member that moved into a work tree wrote its
    /// later rows there). Null when that cwd is not in a work tree of this repository's family other than the main checkout.
    /// </summary>
    private static string? LastWorkTree(RosterContext c, string sessionId)
    {
        var config = c.Services.Config;
        var dirs = new List<string> { c.TeamHome };
        if (c.MainDir is { Length: > 0 } main)
        {
            dirs.Add(main);
        }

        var grants = Ah.Config.ConfigModule.CreateWorktreeGrants(c.Services.Files, c.Services.Environment);
        dirs.AddRange(grants.FamilyRoots(c.TeamHome).Select(root => config.HierarchyDir(root)));

        DateTimeOffset? newestAt = null;
        string? newestCwd = null;
        foreach (var dir in dirs.Distinct(StringComparer.Ordinal))
        {
            foreach (var row in c.Services.Hierarchy.Roster.LatestRoster(dir))
            {
                if (row.Member("session_id") is not { IsText: true } id || id.AsString() != sessionId)
                {
                    continue;
                }

                var at = row.Member("ts") is { IsText: true } ts
                    && DateTimeOffset.TryParse(ts.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed
                        : DateTimeOffset.MinValue;
                if (newestAt is null || at > newestAt)
                {
                    newestAt = at;
                    newestCwd = row.Member("cwd") is { IsText: true } cwd ? cwd.AsString() : null;
                }
            }
        }

        if (string.IsNullOrEmpty(newestCwd)
            || WorkTreeAssignment.TreeOf(c, c.Services.Hierarchy.Messages.RealCwd(newestCwd)) is not { IsMainCheckout: false } tree)
        {
            return null;
        }

        return WorkTreeAssignment.IsFamilyTree(c, tree.Root) && c.Services.Files.Stat(tree.Root) is { Kind: FileKind.Directory } ? tree.Root : null;
    }

    /// <summary>
    /// Plans and, when it can be launched, launches member <paramref name="m"/> of a snapshot; the guard is asked here, under the lock.
    /// Its session is the first known of: <paramref name="knownSession"/>, the snapshot's, the team-file row's (a row survives a restart).
    /// <paramref name="retirePane"/> is a herdr pane holding the member's dead terminal: a new pane is split off it, it is closed, and the
    /// member launches in the new one; a pane that cannot be replaced leaves the member <c>blocked</c> and unlaunched.
    /// </summary>
    private static JsonObject ResumeOne(RosterContext c, JsonObject m, string savedCwd, string? intoPane = null, string? knownSession = null, string? retirePane = null)
    {
        var services = c.Services;
        var launch = services.Launch!;
        var teamRow = services.Roster.TeamMemberByName(c.Dir, MemberPlanning.Text(m, "name"), c.TeamFile);
        var session = knownSession is { Length: > 0 } ? knownSession
            : MemberPlanning.Text(m, "session_id") is { Length: > 0 } snapshotSession ? snapshotSession
            : teamRow.IsMap && MemberPlanning.KindOf(c, m) == launch.Registry.KindDefault ? MemberPlanning.Text(teamRow.Member("session_id"))
            : null;
        if (session is { Length: > 0 } && session != MemberPlanning.Text(m, "session_id"))
        {
            m = m.Clone();
            m["session_id"] = session;
        }

        var wrapper = new JsonObject { ["members"] = new JsonArray { m } };
        var p = launch.SnapshotRules.PlanMembers(wrapper, id => c.PreflightStop ? null : launch.Snapshots.AliveReason(id))[0];
        var status = MemberPlanning.Text(p.Member("status"));
        var row = new JsonObject();
        Put(row, "name", m.Get("name"));
        Put(row, "role", m.Get("role"));
        Put(row, "model", m.Get("model"));
        row["status"] = RosterJson.Text(status);
        if (p.Member("why") is { IsTruthy: true } why)
        {
            row["why"] = why;
        }

        if (status == "fresh" && !string.IsNullOrEmpty(knownSession))
        {
            row["status"] = "failed";
            row["why"] = $"would start fresh although session {knownSession} is known: {MemberPlanning.Text(p.Member("why")) ?? "no reason recorded"}";
            return row;
        }

        if (status is not ("resume" or "fresh"))
        {
            return row;
        }

        var adHoc = new JsonObject();
        Put(adHoc, "role", m.Get("role"));
        Put(adHoc, "name", m.Get("name"));
        adHoc["route"] = m.Get("route") is { IsTruthy: true } route ? route : JsonValue.FromString("peer");
        foreach (var key in new[] { "model", "effort", "autoMode" })
        {
            if (m.Get(key) is { IsTruthy: true } v)
            {
                adHoc[key] = v;
            }
        }

        if (MemberPlanning.Text(m, "kind") is { Length: > 0 } kind && kind != launch.Registry.KindDefault)
        {
            adHoc["kind"] = kind;
        }

        // A snapshot that lacks a setting falls back to the member's own row in the team file before the roster template does.
        if (teamRow.IsMap && MemberPlanning.KindOf(c, MemberPlanning.Obj(teamRow)) == MemberPlanning.KindOf(c, adHoc))
        {
            foreach (var key in new[] { "model", "effort", "autoMode" })
            {
                if (adHoc.Get(key) is not { IsTruthy: true } && teamRow.Member(key) is { IsTruthy: true } fill)
                {
                    adHoc[key] = fill;
                }
            }
        }

        if (m.Get("args") is { IsTruthy: true, IsArray: true } args)
        {
            var copy = new JsonArray();
            copy.AddRange(args.AsArray());
            adHoc["args"] = copy;
        }

        if (status == "resume")
        {
            adHoc["resumeSession"] = m.Get("session_id") ?? JsonValue.Null;
        }

        var recordedRoot = m.Get("work_root") is { IsText: true } wr && wr.AsString().Length > 0 ? wr.AsString() : null;
        if (recordedRoot is not null && !WorkTreeAssignment.IsFamilyTree(c, recordedRoot))
        {
            row["status"] = "failed";
            row["refused"] = "work-tree-missing";
            row["why"] = $"work-tree-missing: {recordedRoot} is gone or is no longer a work tree of this repository; nothing was launched";
            return row;
        }

        // The daemon's own cwd is the project root, and a launch stamps its cwd into the row as expected_root, which bounds the member's
        // writes. So a daemon relaunch starts in the root the row already records and never in another one.
        string? pinnedRoot = null;
        if (c.DaemonPrincipal && teamRow.IsMap && MemberPlanning.Text(MemberPlanning.Obj(teamRow), "expected_root") is { Length: > 0 } recordedExpected)
        {
            if (services.Files.Stat(recordedExpected) is not { Kind: FileKind.Directory })
            {
                row["status"] = "failed";
                row["refused"] = "expected-root-missing";
                row["why"] = $"expected-root-missing: {recordedExpected} is gone; nothing was launched";
                return row;
            }

            pinnedRoot = recordedExpected;
        }

        var rejoin = OpenStreamOf(c, m);
        var (home, launchFrom) = LaunchHome(c, m, status, recordedRoot, rejoin?.Record);
        c.PinnedRoot = pinnedRoot;
        c.Cwd = pinnedRoot is null ? home : services.Hierarchy.Messages.RealCwd(pinnedRoot);
        c.StreamOverride = recordedRoot is not null && m.Get("stream") is { IsText: true } recordedStream && recordedStream.AsString().Length > 0
            ? recordedStream.AsString()
            : launchFrom == "stream" ? rejoin?.Name : null;
        row["launch_from"] = launchFrom;
        if (retirePane is { Length: > 0 } dead)
        {
            var retired = ReplaceDeadPane(c, dead, session, MemberPlanning.Text(m, "name"), teamRow.IsMap ? MemberPlanning.Text(teamRow.Member("spawn_nonce")) : null);
            if (retired.Blocked is { } blocked)
            {
                c.Cwd = savedCwd;
                c.StreamOverride = null;
                row["status"] = "blocked";
                row["blocked"] = blocked;
                row["why"] = blocked;
                if (retired.CloseFailed)
                {
                    row["by_hand"] = new JsonArray { $"herdr pane close {dead}", $"ah roster resume --all --yes" };
                }

                return row;
            }

            // no new pane: the old one is gone or not provably this member's, and the member takes normal placement
            intoPane = retired.Fresh;
            row["old_pane"] = retired.OldPane ?? (retired.Fresh is null ? "unknown" : $"closed:{dead}");
        }

        c.IntoPane = intoPane;
        try
        {
            var role = MemberPlanning.Text(m, "role");
            var outcome = SpawnOne.Spawn(c, role, "resume", adHoc);
            if (outcome.Result is not { } result)
            {
                row["status"] = "failed";
                row["why"] = "layout stopped with a partial result";
                return row;
            }

            if (result.Get("spawned") is { IsBool: true } spawned && !spawned.AsBool())
            {
                row["status"] = "alive";
                row["why"] = result.Get("reason") ?? JsonValue.Null;
                return row;
            }

            row["status"] = status == "resume" ? "resumed" : "fresh";
            row["session"] = status == "resume" ? $"resumed:{session}" : "fresh";
            row["transport_id"] = result.Get("member") is { IsMap: true } member && member.Member("transport_id") is { } tid ? tid : JsonValue.Null;
            if (row["transport_id"] is { IsText: true } newPane)
            {
                row["pane"] = $"new:{newPane.AsString()}";
            }
            if (result.Get("warnings") is { IsArray: true } warnings && warnings.AsArray().Count > 0)
            {
                row["warnings"] = warnings;
            }

            return row;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var reason = string.IsNullOrEmpty(error.Message) ? error.GetType().Name : error.Message;
            if (error is RosterFailure { Refused: "main-checkout" })
            {
                reason += $" (no work tree recorded for {MemberPlanning.Text(m, "name")}; respawn with --stream <name> or resume with --allow-main)";
            }

            row["status"] = reason.Contains("already held by a live session", StringComparison.Ordinal) ? "alive" : "failed";
            row["why"] = reason;
            if (error is RosterFailure { Refused: { Length: > 0 } refused })
            {
                row["refused"] = refused;
            }

            return row;
        }
        finally
        {
            c.Cwd = savedCwd;
            c.StreamOverride = null;
            c.IntoPane = null;
        }
    }

    /// <summary>What became of a dead member's recorded pane: a new pane to launch in, none (normal placement, <c>OldPane</c> says why), or blocked.</summary>
    private sealed record Retired(string? Fresh, string? OldPane, string? Blocked, bool CloseFailed = false);

    /// <summary>
    /// Reads herdr pane <paramref name="dead"/> again, under the resume lock: only a pane that is there, runs no harness and is tied to this
    /// member (<see cref="Ah.Launch.LaunchModule.PaneIsMembers"/>) is replaced. A new pane is split off it, so it lands in the same tab and place; the pane is
    /// read once more, and closed only when it is still all three (otherwise it is left open as <c>changed</c>, and the member is blocked
    /// when the old pane still holds its name). A new pane whose old one stays open by failure is closed again. A pane that is gone or
    /// untied is left alone and the member takes normal placement; one that is unreadable, runs a harness or is the caller's own blocks it.
    /// </summary>
    private static Retired ReplaceDeadPane(RosterContext c, string dead, string? session, string? name, string? spawnNonce)
    {
        if (dead == Ah.Core.Mux.HerdrFlavour.SessionPaneId(c.Services.Environment.GetVariable))
        {
            return new Retired(null, null, $"dead pane {dead} is the caller's own pane; it was not touched");
        }

        var flavour = IRosterLaunch.FlavourOf(c.Services, c.Dir, c.TeamFile, session, name);
        var panes = c.Services.Launch!.PaneStateFor(flavour);
        var herdr = c.Services.Launch!.HerdrFor(flavour);

        // one reading of the pane: null when it is there, runs no harness and is tied to this member, else what it is instead
        Retired? NotDead()
        {
            panes?.Refresh();
            var reading = panes?.Pane("herdr", dead) ?? new PaneReading(PaneKnowledge.Unknown, null, "this build has no pane reader");
            if (reading.State == PaneKnowledge.Absent)
            {
                return new Retired(null, "absent", null);
            }

            if (reading.State != PaneKnowledge.Present || reading.RootPid is not { } root)
            {
                return new Retired(null, null, $"pane {dead} state unknown: {(string.IsNullOrWhiteSpace(reading.Reason) ? "the listing carries no pid for the pane" : reading.Reason)}; it was not touched");
            }

            if (panes!.HarnessRunning(root) != false)
            {
                return new Retired(null, null, $"a harness is running in pane {dead} (or the process table could not be read); it was not touched");
            }

            return Ah.Launch.LaunchModule.PaneIsMembers(herdr, dead, spawnNonce, name) ? null : new Retired(null, "untied, left open", null);
        }

        if (NotDead() is { } first)
        {
            return first;
        }

        string fresh;
        try
        {
            if (MemberPlanning.Text(herdr.Invoke(["pane", "split", "--pane", dead, "--direction", "down", "--cwd", c.Cwd, "--no-focus"]).Member("result")?.Member("pane")?.Member("pane_id")) is not { Length: > 0 } made)
            {
                return new Retired(null, null, $"dead pane {dead} could not be replaced: the split named no new pane");
            }

            fresh = made;
        }
        catch (InvalidOperationException e)
        {
            return new Retired(null, null, $"dead pane {dead} could not be replaced: no pane could be split off it ({e.Message})");
        }

        void DropFresh()
        {
            try
            {
                _ = herdr.InvokeAllowFailure(["pane", "close", fresh]);
            }
            catch (InvalidOperationException)
            {
                // the empty new pane stays; the report still names the member
            }
        }

        // read again right before the close: a pane that changed since is never closed
        if (NotDead() is not null)
        {
            if (name is { Length: > 0 } && Ah.Launch.LaunchModule.PaneNameHolder(herdr, name) == dead)
            {
                DropFresh();
                return new Retired(null, null, $"dead pane {dead} changed before its close and still holds the name {name}, so the launch would be refused as Taken; it was not touched");
            }

            return new Retired(fresh, "changed, left open", null);
        }

        Ah.Launch.HerdrRaw closed;
        try
        {
            closed = herdr.InvokeAllowFailure(["pane", "close", dead]);
        }
        catch (InvalidOperationException e)
        {
            closed = new Ah.Launch.HerdrRaw(false, string.Empty, e.Message, null);
        }

        if (closed.Ok)
        {
            return new Retired(fresh, null, null);
        }

        var why = closed.Stderr.Trim() is { Length: > 0 } err ? err : "herdr pane close failed";
        DropFresh();
        return new Retired(null, null, $"dead pane {dead} could not be closed: {why}", CloseFailed: true);
    }
}
