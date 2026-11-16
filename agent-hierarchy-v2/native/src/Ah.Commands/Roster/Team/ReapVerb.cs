using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;
using Ah.Launch;

namespace Ah.Commands;

/// <summary>
/// <c>reap</c>: list the teams whose orchestrator is provably gone (the default, read-only) or, with <c>--commit</c>, remove them.
/// A team that live sessions still depend on is kept: clearing it would forget the records of live sessions.
/// </summary>
internal static class ReapVerb
{
    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster reap is not implemented natively: the launch layer is not composed");
            return 70;
        }

        foreach (var key in c.Opts.Keys)
        {
            if (key is not ("commit" or "cwd"))
            {
                throw new RosterFailure($"reap: unrecognized flag --{key} (use --commit)");
            }
        }

        var services = c.Services;
        var teardown = services.Launch!.Teardown;
        var context = c.LaunchContext();
        var dir = c.Dir;
        var invoker = c.Invoker();
        var rows = TeamsVerb.AllTeamRows(c, dir, invoker);
        // A lane child is removed from its manager, so reap leaves it to the manager's owner and says so.
        static bool LaneChildOfAnotherOwner(RosterContext c, JsonObject row, Ah.Abstractions.TeamInvoker invoker, JsonArray skipped)
        {
            if (row.Get("name") is not { IsText: true } named)
            {
                return false;
            }

            var name = named.AsString();
            var roster = c.Services.Roster;
            if (OwnerGuard.ValidLaneChild(c, name, roster.ReadTeamRecord(c.Dir, name)) is not { } lane)
            {
                return false;
            }

            var manager = roster.ReadTeamRecord(c.Dir, lane.Manager);
            if (!manager.IsNull && roster.IsTeamOwnedBy(manager, invoker))
            {
                return false;
            }

            skipped.Add(new JsonObject { ["team"] = RosterJson.Text(name), ["why"] = RosterJson.Text($"lane-child: disband from {lane.Manager} owner") });
            return true;
        }

        var skippedLane = new JsonArray();
        // A team a user closed waits to be reopened: nothing is deleted behind the marker. A retired one nobody chose to keep, so it is reaped as if open.
        bool Closed(JsonObject row)
        {
            if (row.Get("name") is not { IsText: true } named || !TeamCloseVerb.UserClosed(services.Roster.ReadTeamRecord(dir, named.AsString())))
            {
                return false;
            }

            skippedLane.Add(new JsonObject { ["team"] = named, ["why"] = "closed" });
            return true;
        }

        var orphans = rows.Where(r => r.Get("orphaned") is { IsTruthy: true } && !LaneChildOfAnotherOwner(c, r, invoker, skippedLane) && !Closed(r)).ToList();
        var tracked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var name = row.Get("name") is { IsText: true } n ? n.AsString() : null;
            if (services.Roster.ReadTeamRecord(dir, name).Member("members") is { IsArray: true } list)
            {
                foreach (var m in list.AsArray())
                {
                    if (m.Member("name") is { IsTruthy: true, IsText: true } mn)
                    {
                        tracked.Add(mn.AsString());
                    }
                }
            }
        }

        var untrackedLive = teardown.UntrackedLive(dir, TeardownSupport.NoTeamScope, tracked, context);
        foreach (var t in orphans)
        {
            var name = t.Get("name") is { IsText: true } n ? n.AsString() : null;
            JsonArray untracked = new();
            if (name is not null)
            {
                var memberNames = new HashSet<string>(StringComparer.Ordinal);
                if (services.Roster.ReadTeamRecord(dir, name).Member("members") is { IsArray: true } list)
                {
                    foreach (var m in list.AsArray())
                    {
                        if (m.IsMap && m.Member("name") is { IsText: true } mn)
                        {
                            memberNames.Add(mn.AsString());
                        }
                    }
                }

                untracked = teardown.UntrackedLive(dir, new TeamScope(name), memberNames, context);
            }

            RosterJson.Spread(t, TeamsVerb.OrphanDependents(c, teardown, dir, name ?? string.Empty, untracked));
        }

        if (!c.Opts.IsTrue("commit"))
        {
            var listed = new JsonArray();
            listed.AddRange(orphans.Select(o => (JsonValue)o));
            c.Out(new JsonObject { ["committed"] = false, ["orphans"] = listed, ["untracked_live"] = untrackedLive, ["skipped"] = skippedLane });
            return 0;
        }

        bool Depended(JsonObject t) => t.Get("live_members") is { IsTruthy: true } || t.Get("attributed_live") is { IsTruthy: true };
        var kept = orphans.Where(Depended).ToList();
        var reaped = new List<JsonObject>();
        foreach (var t in orphans.Where(t => !Depended(t)))
        {
            var name = t.Get("name") is { IsText: true } n ? n.AsString() : null;
            // the plan above took its time over process scans, so a close made meanwhile is read here, on the doc the clear would remove
            var closedSince = false;
            services.Hierarchy.StateCalls.UpdateTeam(
                dir,
                name,
                doc =>
                {
                    closedSince = TeamCloseVerb.UserClosed(doc);
                    return !closedSince && !doc.IsNull && services.Roster.TeamIsOrphaned(doc) ? TeamFileOutcome.Clear() : null;
                });
            if (closedSince)
            {
                skippedLane.Add(new JsonObject { ["team"] = RosterJson.Text(name), ["why"] = "closed" });
            }
            else
            {
                reaped.Add(t);
            }
        }

        var keptRows = new JsonArray();
        foreach (var t in kept)
        {
            var name = t.Get("name") is { IsText: true } n ? n.AsString() : null;
            var row = new JsonObject { ["team"] = RosterJson.Text(name) };
            var hasLive = t.Get("live_members") is { IsTruthy: true };
            if (hasLive)
            {
                row["live_members"] = t.Get("live_members")!.Value;
            }

            if (t.Get("attributed_live") is { IsTruthy: true } attributed)
            {
                row["attributed_live"] = attributed;
            }

            row["next"] = hasLive ? RosterJson.Text(AdoptCommand(c, name)) : JsonValue.Null;
            row["why"] = hasLive
                ? "its records are live sessions"
                : $"live sessions claim this team (through WRANGL_TEAM_FILE or a `{name}-` Herdr name) but match no record. Adopting would relaunch its dead records beside them. Run reap again after those sessions exit.";
            keptRows.Add(row);
        }

        var result = new JsonObject { ["committed"] = true, ["skipped"] = skippedLane };
        var reapedRows = new JsonArray();
        reapedRows.AddRange(reaped.Select(r => (JsonValue)r));
        result["reaped"] = reapedRows;
        if (keptRows.Count > 0)
        {
            result["kept"] = keptRows;
        }

        result["untracked_live"] = untrackedLive;
        c.Out(result);
        return 0;
    }

    /// <summary>The adopt command that makes the invoking session the owner of a team, or null when no pid resolves.</summary>
    private static string? AdoptCommand(RosterContext c, string? teamName)
    {
        var pid = c.OwnerPid();
        if (!RosterContext.IsInteger(pid) || pid <= 0)
        {
            return null;
        }

        var words = new List<string> { "adopt", "--orchestrator-pid", ((long)pid).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (teamName is not null)
        {
            words.AddRange(["--team", teamName]);
        }

        words.AddRange(["--cwd", c.Cwd]);
        return c.SelfCommand + " " + string.Join(' ', words.Select(TeardownSupport.ShellWord));
    }
}
