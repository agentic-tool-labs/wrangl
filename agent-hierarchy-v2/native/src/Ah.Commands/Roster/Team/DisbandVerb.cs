using Ah.Core.Json;
using Ah.Launch;

namespace Ah.Commands;

/// <summary>
/// <c>disband</c>: plan the close of a whole team (the default, read-only), or <c>--close --confirm --plan-token</c> to close its
/// sessions and reconcile the team file against what actually closed.
/// </summary>
internal static class DisbandVerb
{
    private static readonly string[] Flags = ["kill", "plan", "close", "confirm", "plan-token", "allow-global", "cwd", "team", ReleaseGate.IgnoreFlag];

    private static List<JsonValue> Members(JsonValue team) => team.Member("members") is { IsArray: true } m ? m.AsArray().ToList() : [];

    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster disband is not implemented natively: the launch layer is not composed");
            return 70;
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key) && key is not ("commit" or "keep-sessions"))
            {
                throw new RosterFailure(
                    $"disband: unrecognized flag --{key} (use --plan, or --close --confirm --plan-token <tok>; tracking-only removal is `untrack --all`)");
            }
        }

        TeardownSupport.RequireEachFlagOnce(c, "disband");
        if (c.Opts.IsTrue("commit") || c.Opts.IsTrue("keep-sessions"))
        {
            throw new RosterFailure(
                "disband: --commit and --keep-sessions were removed — `disband` now only plans or CLOSES sessions. " +
                "To forget the record and leave the session running, use `untrack --all --commit --keep-sessions`.");
        }

        if (c.Opts.IsTrue("close") && c.Opts.IsTrue("kill"))
        {
            throw new RosterFailure("disband --close cannot be combined with --kill");
        }

        var dir = c.Dir;
        return c.Opts.IsTrue("close") ? Close(c, dir) : Plan(c, dir);
    }

    // ---------------------------------------------------------------- --close

    private static int Close(RosterContext c, string dir)
    {
        var launch = c.Services.Launch!;
        var teardown = launch.Teardown;
        var context = c.LaunchContext();
        var warnings = new List<string>();
        var read = TeardownSupport.TeardownTeam(c, dir, warnings);
        if (read.Team.IsNull)
        {
            var excludedPeers = new List<string>();
            var found = TeardownSupport.WithoutInvoker(c, TeardownSupport.DedupPeers(teardown.PeerFallbackMembers(dir, TeardownSupport.NoTeamScope, context)), excludedPeers);
            warnings.AddRange(TeardownSupport.ExcludedWarnings(excludedPeers));
            var closable = teardown.ClosableMembers(found).ToList();
            var unreadable = TeardownSupport.UnreadableTeamFile(c, dir);
            var sources = teardown.SourcesField(dir, TeardownSupport.NoTeamScope, JsonValue.Null, context);
            JsonObject WithUnreadable(JsonObject o)
            {
                if (unreadable is not null)
                {
                    o["team_file_unreadable"] = unreadable;
                }

                return o;
            }

            if (found.Count == 0)
            {
                c.Out(WithUnreadable(new JsonObject
                {
                    ["closed"] = false,
                    ["reason"] = "no active team and no live peers",
                    ["sources"] = sources,
                }));
                return 0;
            }

            var notClosablePeers = TeardownSupport.NotClosableOf(c, found, null);
            if (closable.Count == 0)
            {
                var warn = new List<string> { TeardownSupport.NoRecordWarning(c, dir, found) };
                warn.AddRange(TeardownSupport.NotClosableWarnings(notClosablePeers));
                c.Out(WithUnreadable(new JsonObject
                {
                    ["closed"] = false,
                    ["reason"] = "live peers were found but none has a pane to close",
                    ["expected"] = 0,
                    ["not_closable"] = notClosablePeers,
                    ["warnings"] = TeardownSupport.Strings(warn),
                    ["sources"] = sources,
                }));
                return 0;
            }

            warnings.Add(TeardownSupport.NoRecordWarning(c, dir, found));
            warnings.AddRange(TeardownSupport.NotClosableWarnings(notClosablePeers));
            var peerRelease = ReleaseGate.Apply(c, dir, closable, ReleaseGate.NoTeamRows);
            if (peerRelease.Closable.Count == 0 && peerRelease.Held.Count > 0)
            {
                return ReleaseGate.NothingMayClose(c, peerRelease, WithUnreadable(sources), warnings);
            }

            closable = peerRelease.Closable;
            TeardownSupport.GateClose(c, "disband", closable);
            var verifiedPeers = TeardownSupport.CloseAndVerify(c, closable, null, warnings);
            var summaryPeers = TeardownSupport.CloseSummary(c, closable, verifiedPeers, warnings, peerRelease);
            var allClosed = summaryPeers.Get("closed") is { IsTruthy: true } && notClosablePeers.Count == 0 && peerRelease.Held.Count == 0;
            summaryPeers["closed"] = allClosed;
            summaryPeers["source"] = "peers";
            summaryPeers["results"] = ResultsOf(verifiedPeers);
            summaryPeers["not_closable"] = notClosablePeers;
            summaryPeers["sources"] = sources;
            summaryPeers["pruned"] = new JsonArray();
            summaryPeers["kept"] = new JsonArray();
            summaryPeers["team_removed"] = false;
            summaryPeers["team_file"] = JsonValue.Null;
            return ReleaseGate.Finish(c, WithUnreadable(summaryPeers), peerRelease);
        }

        var team = read.Team;
        var eff = TeardownSupport.MergedTeam(team, read.Extra);
        var transport = MemberPlanning.Text(team.Member("transport"));
        var healed = Members(eff);
        if (transport == "herdr")
        {
            var result = Resync.ResyncMembers(c, eff);
            if (result.QueryOk && result.Members is { } rm)
            {
                healed = rm.ToList();
            }
        }

        var excluded = new List<string>();
        healed = TeardownSupport.WithoutInvoker(c, healed, excluded);
        warnings.AddRange(TeardownSupport.ExcludedWarnings(excluded));
        var set = TeardownSupport.TeamCloseSet(c, dir, eff, healed);
        warnings.AddRange(TeardownSupport.ExcludedWarnings(set.Excluded));
        var release = ReleaseGate.Apply(c, dir, set.Closable, TeamNames(eff));
        if (release.Closable.Count == 0 && release.Held.Count > 0)
        {
            return ReleaseGate.NothingMayClose(c, release, teardown.SourcesField(dir, TeardownSupport.ScopeOf(c), team, context), warnings);
        }

        TeardownSupport.GateClose(c, "disband", release.Closable, CleanSubject(dir, team, release));
        var snapshot = TeardownSupport.AutoSnapshot(c, dir, warnings);
        var verified = TeardownSupport.CloseAndVerify(c, release.Closable, transport, warnings);
        var reconciled = teardown.ReconcileAfterClose(dir, team, verified.Results, c.Services.Roster.TeamPath(dir, c.TeamFile), context);
        foreach (var x in read.Extra)
        {
            teardown.ReconcileAfterClose(
                dir, x.Member("team") ?? JsonValue.Null, verified.Results, MemberPlanning.JsConvertText(x.Member("path")), context);
        }

        warnings.AddRange(TeardownSupport.NotClosableWarnings(set.NotClosable));
        var summary = TeardownSupport.CloseSummary(c, release.Closable, verified, warnings, release);
        summary["closed"] = summary.Get("closed") is { IsTruthy: true } && set.NotClosable.Count == 0 && release.Held.Count == 0;
        if (summary.Get("closed") is { IsTruthy: true })
        {
            StreamTabs.ReportAll(summary, StreamTabs.CloseEmpty(c, team, c.Services.Launch!.Streams.StreamsOf(team).Keys.ToList(), evenIfOpen: true, warnings));

            if (warnings.Count > 0)
            {
                summary["warnings"] = TeardownSupport.Strings(warnings);
            }
        }

        if (summary.Get("closed") is { IsTruthy: true } && team.Member("parent") is { IsMap: true } link
            && MemberPlanning.Text(link.Member("team")) is { } managerTeam && MemberPlanning.Text(link.Member("stream")) is { } laneStream
            && c.TeamFile == Ah.Hierarchy.HierarchyModule.LaneChildName(managerTeam, laneStream))
        {
            var owner = MemberPlanning.Text(c.Services.Roster.ReadTeamRecord(dir, managerTeam).Member("streams")?.Member(laneStream)?.Member("sub_orchestrator"));
            var laneWarnings = new List<string>();
            if (LaneDissolve.Revert(c, dir, managerTeam, managerTeam, laneStream, owner, laneWarnings) is { Count: > 0 } returned)
            {
                summary["reverted"] = TeardownSupport.Strings(returned);
            }

            if (laneWarnings.Count > 0)
            {
                summary["lane_warnings"] = TeardownSupport.Strings(laneWarnings);
            }
        }

        summary["snapshot"] = snapshot;
        summary["results"] = ResultsOf(verified);
        summary["not_closable"] = set.NotClosable;
        summary["team_files"] = read.Files;
        summary["sources"] = teardown.SourcesField(dir, TeardownSupport.ScopeOf(c), team, context);
        summary["pruned"] = TeardownSupport.Strings(reconciled.Pruned);
        foreach (var pruned in reconciled.Pruned)
        {
            if (WorkTreeAssignment.Release(c, pruned) is { } releaseWarning)
            {
                warnings.Add(releaseWarning);
                summary["warnings"] = TeardownSupport.Strings(warnings);
            }
        }

        // A closed member the team file keeps still has its scratch moved aside; its grants are not touched.
        var closedNames = release.Closable.Select(m => m.IsText ? m.AsString() : MemberPlanning.JsConvertText(m.Member("name")))
            .Where(n => !reconciled.Pruned.Contains(n) && !verified.StillLive.Contains(n));
        foreach (var closedName in closedNames)
        {
            if (MemberTmp.Dismiss(c, closedName) is { } tmpWarning)
            {
                warnings.Add(tmpWarning);
                summary["warnings"] = TeardownSupport.Strings(warnings);
            }
        }

        // Every member that closed leaves its open briefs abandoned, as dismiss does; a role nobody left in the team holds also takes the
        // briefs that name no member.
        var gone = release.Closable.Select(m => m.IsText ? m.AsString() : MemberPlanning.JsConvertText(m.Member("name")))
            .Where(n => !verified.StillLive.Contains(n)).ToHashSet(StringComparer.Ordinal);
        foreach (var member in healed.Where(m => gone.Contains(MemberPlanning.JsConvertText(m.Member("name")))))
        {
            var name = MemberPlanning.JsConvertText(member.Member("name"));
            var role = MemberPlanning.Text(member.Member("role"));
            var soleOfRole = role is { Length: > 0 } && !healed.Any(m => MemberPlanning.Text(m.Member("role")) == role && !gone.Contains(MemberPlanning.JsConvertText(m.Member("name"))));
            DismissVerb.AbandonOpenBriefs(c, dir, name, member, warnings, summary, soleOfRole, $"team {MemberPlanning.JsConvertText(team.Member("team_id"))} was disbanded", $"disband of {name}");
        }

        var kept = new JsonArray();
        kept.AddRange(reconciled.Kept.Select(k => (JsonValue)k));
        summary["kept"] = kept;
        summary["team_removed"] = reconciled.TeamRemoved;
        summary["team_file"] = RosterJson.Text(reconciled.TeamFile);
        return ReleaseGate.Finish(c, summary, release);
    }

    private static HashSet<string> TeamNames(JsonValue team) =>
        Members(team).Select(m => MemberPlanning.JsConvertText(m.Member("name"))).ToHashSet(StringComparer.Ordinal);

    internal static JsonArray ResultsOf(CloseVerification verified)
    {
        var results = new JsonArray();
        results.AddRange(verified.Results.Select(r => (JsonValue)r));
        return results;
    }

    // ---------------------------------------------------------------- plan

    /// <summary>The members a team disband would close, as the clean-close check reads them; null when one of them is not a member row.</summary>
    private static CleanClose.Subject? CleanSubject(string dir, JsonValue team, ReleaseGate.Outcome release) =>
        release.Closable.All(m => m.IsMap && MemberPlanning.Text(m.Member("name")) is { Length: > 0 })
            ? new(dir, team, [.. release.Closable.Select(m => CleanClose.MemberOf(MemberPlanning.Text(m.Member("name"))!, m, release))])
            : null;

    /// <summary>What a plan of closing a whole team is built from: the merged team, the healed members, the set that may close and the token that names it.</summary>
    internal sealed record TeamPlan(
        JsonValue Eff, string? Transport, List<JsonValue> Healed, JsonObject? Resync, TeardownSupport.CloseSet Set, ReleaseGate.Outcome Release, string Token);

    /// <summary>The plan of closing the team that was read, shared by <c>disband</c> and the managing orchestrator's <c>close-approve</c>.</summary>
    internal static TeamPlan PlanTeam(RosterContext c, string dir, TeardownSupport.TeamRead read, List<string> planWarnings)
    {
        var team = read.Team;
        var teardown = c.Services.Launch!.Teardown;
        var eff = TeardownSupport.MergedTeam(team, read.Extra);
        var transport = MemberPlanning.Text(team.Member("transport"));
        var healed = Members(eff);
        JsonObject? resyncSummary = null;
        if (transport == "herdr")
        {
            var result = Resync.ResyncMembers(c, eff);
            if (result.QueryOk && result.Members is { } rm)
            {
                healed = rm.ToList();
                resyncSummary = new JsonObject { ["ok"] = true, ["counts"] = result.Counts ?? new JsonObject() };
                if (result.Warning is not null)
                {
                    resyncSummary["warning"] = result.Warning;
                }
            }
            else
            {
                healed = Members(eff)
                    .Select(m =>
                    {
                        if (!m.IsMap)
                        {
                            return m;
                        }

                        var copy = m.AsObject().Clone();
                        copy["status"] = "unqueried";
                        return (JsonValue)copy;
                    })
                    .ToList();
                resyncSummary = new JsonObject { ["ok"] = false, ["reason"] = RosterJson.Text(result.QueryError) };
            }
        }

        var excludedMembers = new List<string>();
        healed = TeardownSupport.WithoutInvoker(c, healed, excludedMembers);
        planWarnings.AddRange(TeardownSupport.ExcludedWarnings(excludedMembers));
        var set = TeardownSupport.TeamCloseSet(c, dir, eff, healed);
        planWarnings.AddRange(TeardownSupport.ExcludedWarnings(set.Excluded));
        var planRelease = ReleaseGate.Apply(c, dir, set.Closable, TeamNames(eff));
        var planToken = teardown.CloseToken(team.Member("team_id") ?? JsonValue.Null, planRelease.Closable);
        return new TeamPlan(eff, transport, healed, resyncSummary, set, planRelease, planToken);
    }

    private static int Plan(RosterContext c, string dir)
    {
        var launch = c.Services.Launch!;
        var teardown = launch.Teardown;
        var context = c.LaunchContext();
        var planWarnings = new List<string>();
        var read = TeardownSupport.TeardownTeam(c, dir, planWarnings);
        if (read.Team.IsNull)
        {
            var excludedPeers = new List<string>();
            var fallback = TeardownSupport.WithoutInvoker(c, TeardownSupport.DedupPeers(teardown.PeerFallbackMembers(dir, TeardownSupport.NoTeamScope, context)), excludedPeers);
            planWarnings.AddRange(TeardownSupport.ExcludedWarnings(excludedPeers));
            var closable = teardown.ClosableMembers(fallback).ToList();
            var unreadable = TeardownSupport.UnreadableTeamFile(c, dir);
            var sources = teardown.SourcesField(dir, TeardownSupport.NoTeamScope, JsonValue.Null, context);
            if (fallback.Count == 0)
            {
                var none = new JsonObject
                {
                    ["disbanded"] = false,
                    ["reason"] = "no active team and no live peers",
                    ["sources"] = sources,
                };
                if (unreadable is not null)
                {
                    none["team_file_unreadable"] = unreadable;
                }

                c.Out(none);
                return 0;
            }

            var notClosable = TeardownSupport.NotClosableOf(c, fallback, null);
            planWarnings.Add(TeardownSupport.NoRecordWarning(c, dir, fallback));
            planWarnings.AddRange(TeardownSupport.NotClosableWarnings(notClosable));
            var strays = closable.Where(m => m.Member("stray") is { IsTruthy: true }).Select(m => MemberPlanning.JsConvertText(m.Member("name"))).ToList();
            if (strays.Count > 0)
            {
                planWarnings.Insert(0, TeardownSupport.StrayWarning(c, strays));
            }

            var peerRelease = ReleaseGate.Apply(c, dir, closable, ReleaseGate.NoTeamRows);
            closable = peerRelease.Closable;
            var token = teardown.CloseToken(TeardownSupport.Scope("no-team"), closable);
            var plan = new JsonObject();
            var close = new JsonArray();
            close.AddRange(fallback.Select(m => (JsonValue)TeardownSupport.PeerFallbackPlanEntry(m)));
            plan["close"] = close;
            plan["close_token"] = token;
            if (closable.Count > 0)
            {
                plan["next"] = TeardownSupport.CloseCommand(c, "disband", null, token);
            }

            plan["source"] = "peers";
            plan["expected"] = closable.Count;
            plan["strays"] = TeardownSupport.Strings(strays);
            plan["not_closable"] = notClosable;
            plan["team_files"] = new JsonArray();
            plan["warnings"] = TeardownSupport.Strings(planWarnings);
            plan["sources"] = sources;
            if (unreadable is not null)
            {
                plan["team_file_unreadable"] = unreadable;
            }

            ReleaseGate.Annotate(plan, peerRelease);
            c.Out(plan);
            return 0;
        }

        var team = read.Team;
        var teamPlan = PlanTeam(c, dir, read, planWarnings);
        var eff = teamPlan.Eff;
        var transport = teamPlan.Transport;
        var healed = teamPlan.Healed;
        var resyncSummary = teamPlan.Resync;
        var set = teamPlan.Set;
        var planRelease = teamPlan.Release;
        var planClean = CleanSubject(dir, team, teamPlan.Release) is { } cleanSubject ? CleanClose.Read(c, cleanSubject, teamPlan.Token) : null;
        var planToken = planClean?.Token ?? teamPlan.Token;
        var closeRows = new JsonArray();
        foreach (var m in healed)
        {
            string? command = null;
            if (MemberPlanning.HasPane(c, m.Member("route")) && m.Member("transport_id") is { IsTruthy: true } tid)
            {
                var id = MemberPlanning.JsConvertText(tid);
                command = transport == "herdr" ? $"herdr pane close {id}" : transport == "tmux" ? $"tmux kill-pane -t {id}" : null;
            }

            var entry = new JsonObject();
            Put(entry, "role", m.Member("role"));
            Put(entry, "name", m.Member("name"));
            Put(entry, "route", m.Member("route"));
            Put(entry, "transport", team.Member("transport"));
            Put(entry, "transport_id", m.Member("transport_id"));
            entry["command"] = RosterJson.Text(command);
            if (transport == "herdr")
            {
                Put(entry, "resync_status", m.Member("status"));
            }

            closeRows.Add(entry);
        }

        foreach (var extra in set.Extras)
        {
            closeRows.Add(TeardownSupport.PeerFallbackPlanEntry(extra));
        }

        var planStrays = set.Closable.Where(m => m.Member("stray") is { IsTruthy: true }).Select(m => MemberPlanning.JsConvertText(m.Member("name"))).ToList();
        planWarnings.AddRange(TeardownSupport.NotClosableWarnings(set.NotClosable));
        if (planStrays.Count > 0)
        {
            planWarnings.Insert(0, TeardownSupport.StrayWarning(c, planStrays));
        }

        var disbandOut = new JsonObject
        {
            ["close"] = closeRows,
            ["close_token"] = planToken,
            ["next"] = TeardownSupport.CloseCommand(c, "disband", null, planToken),
            ["expected"] = planRelease.Closable.Count,
            ["strays"] = TeardownSupport.Strings(planStrays),
            ["not_closable"] = set.NotClosable,
            ["team_files"] = read.Files,
            ["warnings"] = TeardownSupport.Strings(planWarnings),
            ["sources"] = teardown.SourcesField(dir, TeardownSupport.ScopeOf(c), team, context),
            ["tmp_bytes"] = JsonValue.FromNumber(MemberTmp.PlanBytes(c, planRelease.Closable.Select(m => m.IsText ? m.AsString() : MemberPlanning.JsConvertText(m.Member("name"))))),
        };
        if (planClean is not null)
        {
            disbandOut["clean_close"] = DismissVerb.CleanJson(planClean);
        }

        if (resyncSummary is not null)
        {
            disbandOut["resync"] = resyncSummary;
        }

        if (planRelease.Closable.Count == 0 && planRelease.Held.Count > 0)
        {
            disbandOut.Remove("next");
        }

        ReleaseGate.Annotate(disbandOut, planRelease);
        c.Out(disbandOut);
        return 0;
    }

    private static void Put(JsonObject target, string key, JsonValue? value)
    {
        if (value is { } v)
        {
            target[key] = v;
        }
    }
}
