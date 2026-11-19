using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>dismiss &lt;name&gt;</c>: close a clean member in one call (the default; a member that is not clean is refused with its reasons),
/// plan the close (<c>--plan</c>, read-only), or <c>--close</c> to close it whatever its state; the row drops once the close is verified. The inverse of <c>spawn-one</c>, over the same close machinery as <c>disband</c>.
/// </summary>
internal static class DismissVerb
{
    private static readonly string[] Flags = ["plan", "close", "confirm", "plan-token", "also-config", "level", "allow-global", "cwd", "team", ReleaseGate.IgnoreFlag];

    internal static List<JsonValue> Members(JsonValue team) => team.Member("members") is { IsArray: true } m ? m.AsArray().ToList() : [];

    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster dismiss is not implemented natively: the launch layer is not composed");
            return 70;
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key) && key is not ("commit" or "keep-sessions"))
            {
                throw new RosterFailure(
                    $"dismiss: unrecognized flag --{key} (use --plan, or --close --confirm --plan-token <tok> [--also-config] [--level L]; tracking-only removal is `untrack <name>`)");
            }
        }

        TeardownSupport.RequireEachFlagOnce(c, "dismiss");
        if (c.Opts.IsTrue("commit") || c.Opts.IsTrue("keep-sessions"))
        {
            throw new RosterFailure(
                "dismiss: --commit and --keep-sessions were removed — `dismiss` now only plans or CLOSES sessions. " +
                "To forget the record and leave the session running, use `untrack <name> --commit --keep-sessions`.");
        }

        var closing = c.Opts.IsTrue("close");
        if (c.Opts.IsTrue("plan") && closing)
        {
            throw new RosterFailure("dismiss --plan cannot be combined with --close");
        }

        if ((c.Opts.IsTrue("also-config") || c.Opts.Text("level") is not null) && !closing)
        {
            throw new RosterFailure("dismiss: --also-config and --level are only valid with --close — a plan removes nothing");
        }

        if (c.Opts.Positionals.Count > 1)
        {
            return DismissMany.Run(c, c.Opts.Positionals);
        }

        var name = c.Opts.Positionals.Count > 0
            ? c.Opts.Positionals[0]
            : throw new RosterFailure("dismiss needs a member name: ah roster dismiss <name> [--plan|--close]");
        var dir = c.Dir;
        var launch = c.Services.Launch;
        var teardown = launch.Teardown;
        var context = c.LaunchContext();
        var warnings = new List<string>();
        var (read, team) = TeamOf(c, dir, warnings);
        var teamMembers = team.IsNull ? [] : Members(team);
        JsonValue? target = teamMembers.Cast<JsonValue?>().FirstOrDefault(m => MemberPlanning.Text(m!.Value.Member("name")) == name);
        var scope = team.IsNull ? TeardownSupport.NoTeamScope : TeardownSupport.ScopeOf(c);
        var teamTransport = team.IsNull ? null : MemberPlanning.Text(team.Member("transport"));
        List<JsonValue> fallback = [];
        JsonValue? fbTarget = null;
        var quotedName = JsonText.Quote(name);
        var rowsNamed = teamMembers.Count(m => MemberPlanning.Text(m.Member("name")) == name);
        if (target is not null && rowsNamed > 1)
        {
            throw new RosterFailure(
                $"dismiss: {quotedName} names {rowsNamed} rows in team {MemberPlanning.JsConvertText(team.Member("team_id"))} — it is ambiguous, so nothing was changed. Remove the duplicate rows from the team file first");
        }

        if (target is null && c.LaneActor)
        {
            throw SubOrchestratorSpawn.NotOwnRow(c, "dismiss", name);
        }

        if (target is null)
        {
            fallback = TeardownSupport.DedupPeers(teardown.PeerFallbackMembers(dir, scope, context, teamMembers));
            fbTarget = TeardownSupport.ResolvePeer(c, fallback, name, teamTransport);
        }

        if ((target ?? fbTarget) is { } resolvedTarget && TeardownSupport.IsInvokerOrOrchestrator(c, resolvedTarget))
        {
            throw new RosterFailure($"dismiss: {quotedName} is the session running this command or an orchestrator — it is never closed by dismiss");
        }

        var sources = () => teardown.SourcesField(dir, scope, team, context);
        if (team.IsNull && fbTarget is null)
        {
            if (teardown.ClosableMembers(fallback).Count == 0)
            {
                c.Out(new JsonObject
                {
                    ["dismissed"] = false,
                    ["reason"] = "no active team and no live peers",
                    ["sources"] = teardown.SourcesField(dir, TeardownSupport.NoTeamScope, JsonValue.Null, context),
                });
                return 0;
            }

            var ids = new JsonArray();
            ids.AddRange(fallback.Select(m => (JsonValue)TeardownSupport.PeerIdentifiers(m)));
            throw new RosterFailure(
                $"dismiss: no member named {JsonText.Quote(name)} — no team.json; live untracked sessions: " +
                $"{JsonText.Stringify(ids, 0)} — dismiss accepts any of those name / pane_id / session_id values");
        }

        if (target is null && fbTarget is null)
        {
            var teamId = MemberPlanning.JsConvertText(team.Member("team_id"));
            var quoted = JsonText.Quote(name);
            if (teamMembers.Any(m => MemberPlanning.Text(m.Member("role")) == name))
            {
                throw new RosterFailure(
                    $"dismiss: no member named {quoted} in team {teamId} — that is a role, not a member name; dismiss takes the member's name as the team file records it");
            }

            var has = string.Join(", ", teamMembers.Select(m => MemberPlanning.JsConvertText(m.Member("name"))));
            string tail;
            if (fallback.Count > 0)
            {
                var ids = new JsonArray();
                ids.AddRange(fallback.Select(m => (JsonValue)TeardownSupport.PeerIdentifiers(m)));
                tail = $"; live untracked sessions in this team: {JsonText.Stringify(ids, 0)} — dismiss accepts any of those name / pane_id / session_id values";
            }
            else
            {
                tail = "; no live untracked sessions in this team either";
            }

            throw new RosterFailure($"dismiss: no member named {quoted} in team {teamId} — it has: {(has.Length > 0 ? has : "(none)")}{tail}");
        }

        if (fbTarget is { } peer)
        {
            return DismissPeer(c, dir, name, team, peer, warnings, sources, closing);
        }

        var targetMember = target!.Value;
        if (closing)
        {
            return Close(c, dir, name, read, team, targetMember, warnings, sources);
        }

        return c.Opts.IsTrue("plan")
            ? Plan(c, dir, name, team, targetMember, warnings, sources)
            : CloseIfClean(c, dir, name, read, team, targetMember, warnings, sources);
    }

    /// <summary>What "clean" means, in the order <see cref="CleanClose"/> checks it.</summary>
    internal const string CleanMeans =
        "A clean member is released and idle, has no open request, has finished its stream, and has no uncommitted or unmerged work (its work tree is clean and every commit is on origin).";

    /// <summary>The one clean check a bare <c>dismiss</c> closes on, and the dismiss nudge asks: the member's plan and what <see cref="CleanClose"/> reads of it. A check of several members passes the team-wide reads it made once (<see cref="ReadsOf"/>); null reads them for this one.</summary>
    internal static (MemberPlan Plan, CleanClose.Reading Clean) CleanOf(RosterContext c, string dir, string name, JsonValue team, JsonValue target, TeamReads? reads = null)
    {
        var memberPlan = PlanMember(c, dir, name, team, target, reads);
        var nothing = memberPlan.Closable.Count == 0 && memberPlan.Release.Verdicts.Count == 0;
        return (memberPlan, CleanClose.Read(c, new CleanClose.Subject(dir, team, nothing ? [] : [CleanClose.MemberOf(name, memberPlan.HealedTarget, memberPlan.Release)]), memberPlan.Token, reads));
    }

    /// <summary>The team <c>dismiss</c> acts on, as it reads it: the team record merged with any split copy it may act on.</summary>
    internal static (TeardownSupport.TeamRead Read, JsonValue Team) TeamOf(RosterContext c, string dir, List<string> warnings)
    {
        var read = TeardownSupport.TeardownTeam(c, dir, warnings);
        return (read, read.Team.IsNull ? JsonValue.Null : TeardownSupport.MergedTeam(read.Team, read.Extra));
    }

    /// <summary>A bare <c>dismiss &lt;name&gt;</c>: closes a clean member at once; a member that is not clean is refused with its reasons, unless the caller is the user or the team's own Orchestrator.</summary>
    private static int CloseIfClean(
        RosterContext c, string dir, string name, TeardownSupport.TeamRead read, JsonValue team, JsonValue target, List<string> warnings,
        Func<JsonObject> sources)
    {
        var (memberPlan, clean) = CleanOf(c, dir, name, team, target);
        if (clean.Token is not null)
        {
            return Close(c, dir, name, read, team, target, warnings, sources);
        }

        if (c.Services.OwnerProof?.IsUser() == true || OwningOrchestrator(c, read.Team))
        {
            // ah2-76, ah2-245: the user, the admin Orchestrator and the team's own Orchestrator are never refused: say why it is not clean, then close.
            c.Stderr.WriteLine($"dismiss: {name} is not clean ({string.Join("; ", clean.Failures)}); closing it because the caller is the user or the team's Orchestrator");
            warnings.AddRange(clean.Failures.Select(static f => $"not clean: {f}"));
            return Close(c, dir, name, read, team, target, warnings, sources);
        }

        var message = $"dismiss: {name} is not clean ({string.Join("; ", clean.Failures)}) — nothing was closed. {CleanMeans} Close it anyway: {TeardownSupport.CloseCommand(c, "dismiss", name, memberPlan.Token)}";
        throw c.Refuse(
            new JsonObject
            {
                ["refused"] = "not-clean",
                ["member"] = name,
                ["reasons"] = TeardownSupport.Strings(clean.Failures),
                ["next"] = TeardownSupport.CloseCommand(c, "dismiss", name, memberPlan.Token),
                ["message"] = message,
            },
            message);
    }

    /// <summary>
    /// Whether the caller is the Orchestrator that owns <paramref name="team"/>, as the owner guard proved it (pid and start, after any
    /// late registration), or the manager's Orchestrator acting on a lane child through the parent-owner path; a lane lead is not.
    /// </summary>
    internal static bool OwningOrchestrator(RosterContext c, JsonValue team)
    {
        if (team.IsNull)
        {
            return false;
        }

        if (c.ParentOwnerLane is not null)
        {
            return true;
        }

        if (c.LaneActor)
        {
            return false;
        }

        var proved = c.Proved();
        return proved.Acts && c.Services.Roster.IsTeamOwnedBy(team, new Ah.Abstractions.TeamInvoker(proved.Pid, null, proved.PidStart));
    }

    // ---------------------------------------------------------------- a live peer outside the team record

    private static int DismissPeer(
        RosterContext c, string dir, string name, JsonValue team, JsonValue peer, List<string> warnings, Func<JsonObject> sources, bool closing)
    {
        var teardown = c.Services.Launch!.Teardown;
        var scope = team.IsNull ? TeardownSupport.Scope("no-team") : team.Member("team_id") ?? JsonValue.Null;
        var closable = teardown.ClosableMembers([peer]).ToList();
        if (closing)
        {
            if (closable.Count == 0)
            {
                throw new RosterFailure($"dismiss --close: {name} has no addressable pane (live peer record without a pane_id)");
            }

            var release = ReleaseGate.Apply(c, dir, closable, ReleaseGate.NoTeamRows);
            if (release.Closable.Count == 0 && release.Held.Count > 0)
            {
                return ReleaseGate.NothingMayClose(c, release, sources(), warnings);
            }

            TeardownSupport.GateClose(c, "dismiss", release.Closable);
            var verified = TeardownSupport.CloseAndVerify(c, release.Closable, null, warnings);
            var summary = TeardownSupport.CloseSummary(c, release.Closable, verified, warnings);
            summary["source"] = "peers";
            summary["results"] = DisbandVerb.ResultsOf(verified);
            summary["sources"] = sources();
            if (verified.Results.All(r => r.Get("closed") is { IsTruthy: true } && r.Get("appserver_unverified") is not { IsTruthy: true }))
            {
                // A peer has no team row, so it is the role's only member when no row of the team holds that role.
                var role = MemberPlanning.Text(peer.Member("role"));
                AbandonOpenBriefs(c, dir, MemberPlanning.Text(peer.Member("name")) ?? name, peer, warnings, summary, soleOfRole: role is { Length: > 0 } && !Members(team).Any(m => MemberPlanning.Text(m.Member("role")) == role));
            }

            return ReleaseGate.Finish(c, summary, release);
        }

        var peerRelease = ReleaseGate.Apply(c, dir, closable, ReleaseGate.NoTeamRows);
        closable = peerRelease.Closable;
        var token = teardown.CloseToken(scope, closable);
        var strays = closable.Where(m => m.Member("stray") is { IsTruthy: true }).Select(m => MemberPlanning.JsConvertText(m.Member("name"))).ToList();
        var notClosable = TeardownSupport.NotClosableOf(c, [peer], null);
        warnings.AddRange(TeardownSupport.NotClosableWarnings(notClosable));
        if (strays.Count > 0)
        {
            warnings.Insert(0, TeardownSupport.StrayWarning(c, strays));
        }

        var plan = new JsonObject { ["member"] = TeardownSupport.PeerFallbackPlanEntry(peer) };
        if (peer.Member("live") is { } live)
        {
            plan["live"] = live;
        }

        plan["close_token"] = token;
        if (MemberTmp.Describe(c, name) is { } tmpInfo)
        {
            plan["tmp"] = tmpInfo;
            plan["tmp_bytes"] = tmpInfo.Get("size_bytes") is { IsNumber: true } bytes ? bytes : JsonValue.FromNumber(0);
        }

        if (closable.Count > 0)
        {
            plan["next"] = TeardownSupport.CloseCommand(c, "dismiss", name, token);
        }

        plan["source"] = "peers";
        plan["expected"] = closable.Count;
        plan["strays"] = TeardownSupport.Strings(strays);
        plan["not_closable"] = notClosable;
        plan["warnings"] = TeardownSupport.Strings(warnings);
        plan["sources"] = sources();
        ReleaseGate.Annotate(plan, peerRelease);
        c.Out(plan);
        return 0;
    }

    // ---------------------------------------------------------------- --close

    private static int Close(
        RosterContext c, string dir, string name, TeardownSupport.TeamRead read, JsonValue team, JsonValue target, List<string> warnings,
        Func<JsonObject> sources)
    {
        var teardown = c.Services.Launch!.Teardown;
        var transport = MemberPlanning.Text(team.Member("transport"));
        var healedMembers = Members(team);
        if (transport == "herdr")
        {
            var result = Resync.ResyncMembers(c, team);
            if (result.QueryOk && result.Members is { } rm)
            {
                healedMembers = rm.ToList();
            }
        }

        var healedTarget = healedMembers.Cast<JsonValue?>().FirstOrDefault(m => MemberPlanning.Text(m!.Value.Member("name")) == name) ?? target;
        var closable = transport == "terminal" ? [] : teardown.ClosableMembers([healedTarget]).ToList();
        SubOrchestratorClose.RequireSomethingToClose(c, "dismiss", closable.Count);
        if (closable.Count == 0)
        {
            var tid = target.Member("transport_id") is { IsNull: false } t ? MemberPlanning.JsConvertText(t) : "null";
            throw new RosterFailure(
                $"dismiss --close: {name} has no addressable pane (route={MemberPlanning.JsConvertText(target.Member("route"))}, transport_id={tid}) — use `untrack {name} --commit` to prune the record");
        }

        var release = ReleaseGate.Apply(c, dir, closable, TeamNames(team));
        if (release.Closable.Count == 0 && release.Held.Count > 0)
        {
            return ReleaseGate.NothingMayClose(c, release, sources(), warnings);
        }

        closable = release.Closable;
        TeardownSupport.GateClose(
            c, "dismiss", closable,
            new CleanClose.Subject(dir, team, [CleanClose.MemberOf(name, healedTarget, release)]));
        var verified = TeardownSupport.CloseAndVerify(c, closable, transport, warnings);
        var allClosed = verified.Results.All(r => r.Get("closed") is { IsTruthy: true } && r.Get("appserver_unverified") is not { IsTruthy: true });
        var summary = TeardownSupport.CloseSummary(c, closable, verified, warnings);
        summary["results"] = DisbandVerb.ResultsOf(verified);
        summary["untracked"] = false;
        summary["sources"] = teardown.SourcesField(dir, TeardownSupport.ScopeOf(c), team, c.LaunchContext());
        if (allClosed)
        {
            var closedStreams = closable.Select(m => MemberPlanning.Text(m.Member("stream"))).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!);
            StreamTabs.ReportAll(summary, StreamTabs.CloseEmpty(c, team, closedStreams, evenIfOpen: false, warnings));

            if (warnings.Count > 0)
            {
                summary["warnings"] = TeardownSupport.Strings(warnings);
            }

            DropClosedMember(c, dir, name, read, target, warnings, summary);
            var before = warnings.Count;
            var (stopped, unverified) = StopVerb.StopMemberJobs(c, c.TeamFile, name, warnings);
            if (stopped.Count > 0)
            {
                summary["jobs_stopped"] = stopped;
            }

            if (unverified.Count > 0)
            {
                summary["jobs_unverified"] = unverified;
            }

            if (warnings.Count > before)
            {
                summary["warnings"] = TeardownSupport.Strings(warnings);
            }
        }

        return ReleaseGate.Finish(c, summary, release);
    }

    /// <summary>What follows a verified close: the member's row leaves the team file, its work tree is released and, with <c>--also-config</c>, its config entry goes.</summary>
    internal static void DropClosedMember(
        RosterContext c, string dir, string name, TeardownSupport.TeamRead read, JsonValue target, List<string> warnings, JsonObject summary)
    {
        var templateKey = c.Opts.IsTrue("also-config") ? TeardownSupport.TemplateKey(c, dir) : null;
        DutyVerb.ClearFor(c, dir, name, warnings);
        var journal = ParentAction.Entry(c, "dismiss", name, null);
        JsonObject? journaled = null;
        var files = new List<(string Path, JsonValue Team)> { (c.Services.Roster.TeamPath(dir, c.TeamFile), read.Team) };
        files.AddRange(read.Extra.Select(x => (MemberPlanning.JsConvertText(x.Member("path")), x.Member("team") ?? JsonValue.Null)));
        for (var i = 0; i < files.Count; i++)
        {
            var (path, fileTeam) = files[i];
            // Only the team acted on is the lane child: its file stays when the last row goes and it carries the journal; a peer file in Extra is left to today's rules.
            var own = i == 0;
            if (!Members(fileTeam).Any(m => MemberPlanning.Text(m.Member("name")) == name) && !(own && c.ParentOwnerLane is not null))
            {
                continue;
            }

            var update = c.Services.Launch!.TeamRows.UpdateRows(
                dir,
                path,
                fresh => fresh.Get("members") is { IsArray: true } list
                    ? list.AsArray().Where(m => MemberPlanning.Text(m.Member("name")) != name).ToList()
                    : [],
                keepFile: own && (c.LaneActor || TeardownSupport.NamesOrchestrator(fileTeam)),
                journal: own ? journal : null);
            if (update.Cleared)
            {
                summary["team_removed"] = true;
            }

            if (own)
            {
                journaled = journal;
            }
        }

        RosterLedger.Member(c, "dismiss", "retired", name, MemberPlanning.Text(target.Member("role")));
        ParentAction.Notify(c, dir, journaled, "dismissed");
        var role = MemberPlanning.Text(target.Member("role"));
        AbandonOpenBriefs(c, dir, name, target, warnings, summary, soleOfRole: role is { Length: > 0 } && Members(read.Team).Count(m => MemberPlanning.Text(m.Member("role")) == role) == 1);

        summary["untracked"] = true;
        if (WorkTreeAssignment.Release(c, name) is { } releaseWarning)
        {
            warnings.Add(releaseWarning);
            summary["warnings"] = TeardownSupport.Strings(warnings);
        }

        if (c.Opts.IsTrue("also-config"))
        {
            var removed = new LevelFiles(c).RemoveConfigMember(MemberPlanning.JsConvertText(target.Member("name")), templateKey, sideEffect: true);
            summary["config"] = TeardownSupport.ConfigRemovalJson(removed);
        }
    }

    /// <summary>
    /// Closes each open request addressed to the dismissed member as <c>abandoned</c>, so the watch stops timing it, and leaves its linked
    /// checklist item waiting on the Orchestrator as <c>ah msg close</c> does. A request is the member's when it names the member, or, when
    /// <paramref name="soleOfRole"/>, when it names no member and goes to the member's role. A pipeline run anchor stays open. Team quit does
    /// not close checkpoint briefs (<paramref name="keepCheckpoints"/>): resume reads a checkpoint's response as the member's answer.
    /// Disband and quit pass their own <paramref name="note"/> and <paramref name="by"/>.
    /// </summary>
    internal static void AbandonOpenBriefs(RosterContext c, string dir, string name, JsonValue target, List<string> warnings, JsonObject summary, bool soleOfRole, string? note = null, string? by = null, bool keepCheckpoints = false)
    {
        var closed = new JsonArray();
        var role = MemberPlanning.Text(target.Member("role"));
        try
        {
            var core = c.Services.Hierarchy.Core;
            foreach (var e in ReleaseGate.ExchangesFor(c, dir, MemberPlanning.Text(target.Member("worktree")), []))
            {
                if (e.Get("response") is { IsMap: true } || e.Get("request") is not { IsMap: true } request || request.Member("path") is not { IsText: true } path)
                {
                    continue;
                }

                var fm = core.ReadMsgFile(path.AsString()).Member("fm");
                var toName = MemberPlanning.Text(fm?.Member("to_name"));
                var unnamed = string.IsNullOrEmpty(toName) || toName == "null";
                var mine = toName == name || (soleOfRole && unnamed && role is { Length: > 0 } && MemberPlanning.Text(fm?.Member("to")) == role);
                var slug = MemberPlanning.Text(fm?.Member("slug"));
                if (!mine || slug == Ah.Hierarchy.HierarchyModule.PipelineAnchorSlug || (keepCheckpoints && slug == CheckpointVerb.Slug))
                {
                    continue;
                }

                var result = c.Services.Hierarchy.Messages.CloseExchange(dir, e, path.AsString(), "abandoned", note ?? $"{name} was dismissed", by ?? $"dismiss of {name}", c.Cwd);
                if (result.Get("refused") is not { IsText: true } && result.Get("already") is not { IsTruthy: true })
                {
                    var id = e.Get("id") ?? JsonValue.Null;
                    closed.Add(id);
                    var team = Ah.Hierarchy.HierarchyModule.ProgressTeamOf(MemberPlanning.Text(fm?.Member("team")));
                    if (Ah.Hierarchy.HierarchyModule.ChecklistOnClose(
                        c.Services.Hierarchy.Progress, c.ProgressPool, team, MemberPlanning.JsConvertText(id), MemberPlanning.Text(fm?.Member("item")), Ah.Hierarchy.HierarchyModule.OrchestratorAddress(team), null, "abandoned", Ah.Hierarchy.HierarchyModule.ChecklistEventTime(c.Services.Clock.UtcNow)) is { } problem)
                    {
                        warnings.Add($"the checklist item of {MemberPlanning.JsConvertText(id)} was not updated: {problem}");
                        summary["warnings"] = TeardownSupport.Strings(warnings);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or Ah.Core.Json.StoreUnavailableException)
        {
            warnings.Add($"the open briefs to {name} were not closed: {e.Message}");
            summary["warnings"] = TeardownSupport.Strings(warnings);
        }

        if (closed.Count > 0)
        {
            summary["briefs_abandoned"] = closed;
        }
    }

    private static HashSet<string> TeamNames(JsonValue team) =>
        Members(team).Select(m => MemberPlanning.JsConvertText(m.Member("name"))).ToHashSet(StringComparer.Ordinal);

    // ---------------------------------------------------------------- plan

    /// <summary>What a plan of closing one member is built from: the member as the transport reports it, what may close, and the token that names it.</summary>
    internal sealed record MemberPlan(string? Transport, JsonValue HealedTarget, List<JsonValue> Closable, ReleaseGate.Outcome Release, string Token);

    /// <summary>The plan of closing one member, shared by <c>dismiss</c> and the managing orchestrator's <c>close-approve</c>.</summary>
    internal static MemberPlan PlanMember(RosterContext c, string dir, string name, JsonValue team, JsonValue target, TeamReads? reads = null)
    {
        var teardown = c.Services.Launch!.Teardown;
        var transport = MemberPlanning.Text(team.Member("transport"));
        var healedMembers = reads?.Healed ?? HealedMembers(c, team);
        var healedTarget = healedMembers.Cast<JsonValue?>().FirstOrDefault(m => MemberPlanning.Text(m!.Value.Member("name")) == name) ?? target;
        var closable = transport == "terminal" ? [] : teardown.ClosableMembers([healedTarget]).ToList();
        var planRelease = ReleaseGate.Apply(c, dir, closable, TeamNames(team), reads?.Roster);
        closable = planRelease.Closable;
        var token = teardown.CloseToken(team.Member("team_id") ?? JsonValue.Null, closable);
        return new MemberPlan(transport, healedTarget, closable, planRelease, token);
    }

    /// <summary>The team's members as the transport reports them: resynced with herdr on a herdr team (each <c>unqueried</c> when herdr cannot answer), else as recorded.</summary>
    private static List<JsonValue> HealedMembers(RosterContext c, JsonValue team)
    {
        if (MemberPlanning.Text(team.Member("transport")) != "herdr")
        {
            return Members(team);
        }

        var result = Resync.ResyncMembers(c, team);
        return result.QueryOk && result.Members is { } rm
            ? rm.ToList()
            : Members(team).Select(m =>
            {
                if (!m.IsMap)
                {
                    return m;
                }

                var copy = m.AsObject().Clone();
                copy["status"] = "unqueried";
                return (JsonValue)copy;
            }).ToList();
    }

    /// <summary>What the clean check reads that is the same for every member of one team, read once for a check of several members.</summary>
    internal sealed record TeamReads(List<JsonValue> Healed, JsonArray Roster, JsonArray Open);

    /// <summary>
    /// Reads <see cref="TeamReads"/> for <paramref name="team"/>: one herdr resync, the attributed roster and the team's open exchanges.
    /// Throws <see cref="StoreUnavailableException"/> when the store cannot answer.
    /// </summary>
    internal static TeamReads ReadsOf(RosterContext c, string dir, JsonValue team)
    {
        var healed = HealedMembers(c, team);
        using var decisions = c.Services.Hierarchy.Core.DecisionReads();
        return new TeamReads(
            healed, c.Services.Hierarchy.Roster.AttributedRoster(dir), c.Services.Launch!.Streams.OpenTeamExchanges(dir, c.TeamFile, allTeams: false));
    }

    private static int Plan(
        RosterContext c, string dir, string name, JsonValue team, JsonValue target, List<string> warnings, Func<JsonObject> sources)
    {
        var memberPlan = PlanMember(c, dir, name, team, target);
        var transport = memberPlan.Transport;
        var healedTarget = memberPlan.HealedTarget;
        string? command = null;
        if (MemberPlanning.HasPane(c, healedTarget.Member("route")) && healedTarget.Member("transport_id") is { IsTruthy: true } tid)
        {
            var id = MemberPlanning.JsConvertText(tid);
            command = transport == "herdr" ? $"herdr pane close {id}" : transport == "tmux" ? $"tmux kill-pane -t {id}" : null;
        }

        var member = new JsonObject();
        Put(member, "role", healedTarget.Member("role"));
        Put(member, "name", healedTarget.Member("name"));
        Put(member, "route", healedTarget.Member("route"));
        Put(member, "transport", team.Member("transport"));
        Put(member, "transport_id", healedTarget.Member("transport_id"));
        member["command"] = RosterJson.Text(command);
        if (transport == "herdr")
        {
            member["resync_status"] = healedTarget.Member("status") is { IsTruthy: true } st ? st : JsonValue.FromString("unqueried");
        }

        var closable = memberPlan.Closable;
        var planRelease = memberPlan.Release;
        // a member the release check held has a verdict but is not closable: its reason is the hold, not that nothing is there to close
        var nothing = closable.Count == 0 && planRelease.Verdicts.Count == 0;
        var clean = CleanClose.Read(c, new CleanClose.Subject(dir, team, nothing ? [] : [CleanClose.MemberOf(name, healedTarget, planRelease)]), memberPlan.Token);
        var token = clean.Token ?? memberPlan.Token;
        var notClosable = TeardownSupport.NotClosableOf(c, [healedTarget], transport);
        warnings.AddRange(TeardownSupport.NotClosableWarnings(notClosable));
        var plan = new JsonObject { ["member"] = member };
        var liveness = TeardownSupport.LiveField(c, dir, healedTarget);
        RosterJson.Spread(plan, liveness);
        plan["close_token"] = token;
        plan["clean_close"] = CleanJson(clean);
        if (MemberTmp.Describe(c, name) is { } tmpInfo)
        {
            plan["tmp"] = tmpInfo;
            plan["tmp_bytes"] = tmpInfo.Get("size_bytes") is { IsNumber: true } bytes ? bytes : JsonValue.FromNumber(0);
        }

        if (closable.Count > 0)
        {
            plan["next"] = TeardownSupport.CloseCommand(c, "dismiss", name, token);
        }

        plan["sources"] = sources();
        plan["expected"] = closable.Count;
        plan["strays"] = new JsonArray();
        plan["not_closable"] = notClosable;
        plan["warnings"] = TeardownSupport.Strings(warnings);
        plan["team_id"] = team.Member("team_id") ?? JsonValue.Null;
        var others = Members(team).Where(m => MemberPlanning.Text(m.Member("name")) != name).Select(m => MemberPlanning.JsConvertText(m.Member("name")));
        plan["remaining"] = TeardownSupport.Strings(others);
        if (Members(team).All(m => MemberPlanning.Text(m.Member("name")) == name) && !c.LaneActor && !TeardownSupport.NamesOrchestrator(team))
        {
            plan["team_will_be_removed"] = true;
        }

        ReleaseGate.Annotate(plan, planRelease);
        c.Out(plan);
        return 0;
    }

    /// <summary>Which kind of token a plan minted and, for the ordinary one, the first failed condition of each member.</summary>
    internal static JsonObject CleanJson(CleanClose.Reading clean) =>
        new() { ["minted"] = clean.Token is not null, ["reasons"] = TeardownSupport.Strings(clean.Failures) };

    private static void Put(JsonObject target, string key, JsonValue? value)
    {
        if (value is { } v)
        {
            target[key] = v;
        }
    }
}
