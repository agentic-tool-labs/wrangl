using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary><c>resync</c>: heal the team's recorded panes against the live herdr topology and persist the result.</summary>
internal static class ResyncVerb
{
    private static readonly string[] Flags = ["dry-run", "cwd", "team", "bind"];

    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster resync is not implemented natively: the launch layer is not composed");
            return 70;
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"resync: unrecognized flag --{key} (use --dry-run, --team, --bind, or --cwd)");
            }
        }

        var services = c.Services;
        var dir = c.Dir;
        var team = services.Roster.ReadTeamRecord(dir, c.TeamFile);
        if (team.IsNull)
        {
            c.Out(new JsonObject { ["resynced"] = false, ["reason"] = "no active team" });
            return 0;
        }

        var transport = MemberPlanning.Text(team.Member("transport"));
        if (transport != "herdr")
        {
            c.Out(new JsonObject { ["resynced"] = false, ["reason"] = Resync.TransportNoop(transport) });
            return 0;
        }

        JsonObject? bind = null;
        if (c.Opts.Has("bind"))
        {
            var raw = c.Opts.Text("bind") ?? "true";
            if (JsJson.Describe(raw) is not null)
            {
                throw new RosterFailure("resync: --bind is not valid JSON");
            }

            var parsed = JsonText.Parse(raw);
            if (!parsed.IsMap)
            {
                throw new RosterFailure("resync: --bind must be a JSON object mapping member name to pane id");
            }

            bind = parsed.AsObject();
        }

        var members = team.Member("members") ?? JsonValue.Null;
        var basis = JsonText.Stringify(members, 0);
        var teamCwd = services.Config.FindGitRoot(c.Cwd) ?? c.Cwd;
        var result = Resync.ResyncMembers(c, team, dir, teamCwd, Ah.Core.Mux.HerdrFlavour.SessionPaneId(c.EnvText), bind);
        if (!result.QueryOk)
        {
            throw new RosterFailure(result.QueryError ?? "undefined");
        }

        if (result.BindError is not null)
        {
            throw new RosterFailure(result.BindError);
        }

        var healed = result.Members!;
        var dryRun = c.Opts.IsTrue("dry-run");
        var membersOut = new JsonArray();
        foreach (var m in healed)
        {
            if (!m.IsMap)
            {
                membersOut.Add(new JsonObject { ["status"] = "malformed", ["raw"] = m });
                continue;
            }

            var entry = new JsonObject();
            Put(entry, "role", m.Member("role"));
            Put(entry, "name", m.Member("name"));
            Put(entry, "status", m.Member("status"));
            var status = MemberPlanning.Text(m.Member("status"));
            if (status == "updated")
            {
                Put(entry, "from", m.Member("from"));
                Put(entry, "to", m.Member("to"));
                if (m.Member("match_by") is { IsTruthy: true } matchBy)
                {
                    entry["match_by"] = matchBy;
                }
            }

            if (status == "ambiguous")
            {
                Put(entry, "candidates", m.Member("candidates"));
            }

            membersOut.Add(entry);
        }

        if (!dryRun)
        {
            var stripped = new JsonArray();
            stripped.AddRange(healed.Select(Resync.StripMeta));
            services.Hierarchy.StateCalls.UpdateTeam(
                dir,
                c.TeamFile,
                doc =>
                {
                    if (doc.IsNull)
                    {
                        return null;
                    }

                    if (JsonText.Stringify(doc.Member("members") ?? JsonValue.Null, 0) != basis)
                    {
                        var conflict = new InvalidOperationException("the team changed during resync; run it again");
                        conflict.Data["code"] = "conflict";
                        throw conflict;
                    }

                    var next = doc.AsObject().Clone();
                    next["members"] = stripped;
                    return TeamFileOutcome.Write(next);
                });
        }

        var counts = result.Counts ?? new JsonObject();
        var output = new JsonObject
        {
            ["resynced"] = true,
            ["dry_run"] = dryRun,
            ["transport"] = "herdr",
            ["members"] = membersOut,
            ["counts"] = counts,
        };
        if (result.Warning is not null)
        {
            output["warning"] = result.Warning;
        }

        if (counts.Get("malformed") is { IsNumber: true } malformed && malformed.AsNumber() > 0)
        {
            output["warning_malformed"] =
                $"{MemberPlanning.JsConvertText(malformed)} member(s) in team.json are malformed (not objects) and were left untouched — re-run `create --commit` with --verified as an array of member names to repair";
        }

        c.Out(output);
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

/// <summary><c>move &lt;name&gt;</c>: move one member's pane to another tab or workspace, then re-read where every pane is.</summary>
internal static class MoveVerb
{
    private static readonly string[] Flags =
        ["tab", "split", "new-tab", "workspace", "new-workspace", "dry-run", "allow-global", "cwd", "team", "stream", "replace-live"];

    private const string Usage = "ah roster move <name> --tab <id> [--split right|down] | --new-tab [--workspace <id>] | --new-workspace | --stream <name> [--replace-live]";

    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster move is not implemented natively: the launch layer is not composed");
            return 70;
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure(
                    $"move: unrecognized flag --{key} (use --tab/--split, --new-tab[/--workspace], --new-workspace, or --allow-global)");
            }
        }

        var name = c.Opts.Positionals.Count > 0
            ? c.Opts.Positionals[0]
            : throw new RosterFailure($"move needs a member name: {Usage}");
        var services = c.Services;
        var dir = c.Dir;
        var team = services.Roster.ReadTeamRecord(dir, c.TeamFile);
        var teamMembers = team.Member("members") is { IsArray: true } list ? list.AsArray().ToList() : [];
        JsonValue? found = teamMembers.Cast<JsonValue?>().FirstOrDefault(m => MemberPlanning.Text(m!.Value.Member("name")) == name);
        if (found is not { } member)
        {
            var known = string.Join(
                ", ",
                teamMembers.Select(m => m.Member("name")).Where(n => n is { IsTruthy: true }).Select(n => MemberPlanning.JsConvertText(n)));
            throw new RosterFailure(
                $"move: no member named {JsonText.Quote(name)} — known members: {(known.Length > 0 ? known : "(none)")}");
        }

        if (c.Opts.Has("stream"))
        {
            return MoveToStream(c, dir, team, member, name);
        }

        if (!MemberPlanning.HasPane(c, member.Member("route")) || member.Member("transport_id") is not { IsNull: false })
        {
            throw new RosterFailure($"move: member {name} has no pane to move");
        }

        var transport = MemberPlanning.Text(team.Member("transport"));
        if (transport != "herdr")
        {
            c.Out(new JsonObject { ["moved"] = false, ["reason"] = Resync.TransportNoop(transport) });
            return 0;
        }

        var opts = c.Opts;
        var modeCount = new[] { opts.Has("tab"), opts.IsTrue("new-tab"), opts.IsTrue("new-workspace") }.Count(b => b);
        if (modeCount != 1)
        {
            throw new RosterFailure("move: exactly one of --tab, --new-tab, --new-workspace is required");
        }

        if (opts.Has("tab") && opts.Text("tab") is null)
        {
            throw new RosterFailure($"move: --tab requires a value: {Usage}");
        }

        if (opts.Has("tab") && !opts.Has("split"))
        {
            throw new RosterFailure($"move: --tab requires --split right|down: {Usage}");
        }

        if (opts.Has("workspace") && opts.Text("workspace") is null)
        {
            throw new RosterFailure($"move: --workspace requires a value: {Usage}");
        }

        if (opts.Has("split") && !opts.Has("tab"))
        {
            throw new RosterFailure("move: --split is only valid with --tab");
        }

        var herdrArgs = new List<string> { "pane", "move", MemberPlanning.JsConvertText(member.Member("transport_id")) };
        if (opts.Has("tab"))
        {
            herdrArgs.AddRange(["--tab", opts.Text("tab")!]);
            if (opts.Has("split"))
            {
                herdrArgs.AddRange(["--split", PanePlacement.SplitValue(c, "move")]);
            }
        }
        else if (opts.IsTrue("new-tab"))
        {
            herdrArgs.Add("--new-tab");
            if (opts.Text("workspace") is { } workspace)
            {
                herdrArgs.AddRange(["--workspace", workspace]);
            }
        }
        else
        {
            herdrArgs.Add("--new-workspace");
        }

        var commandString = $"herdr {string.Join(' ', herdrArgs)}";
        if (opts.IsTrue("dry-run"))
        {
            c.Out(new JsonObject { ["moved"] = false, ["dry_run"] = true, ["command"] = commandString });
            return 0;
        }

        var movedPane = MemberPlanning.JsConvertText(member.Member("transport_id"));
        var beforeMove = PaneZoom.Read(c, movedPane);
        var zoomed = beforeMove.Zoomed;
        if (zoomed is not null && PaneZoom.Off(c, zoomed) is { } unzoomFailure)
        {
            c.Out(new JsonObject { ["moved"] = false, ["command"] = commandString, ["reason"] = unzoomFailure });
            return 0;
        }

        JsonValue reply;
        string? rezoom = null;
        try
        {
            // the moved pane lives on its member's own server; with no tag of its own (registration or row) the env's, as before
            var session = MemberPlanning.Text(member.Member("session_id"));
            reply = services.Launch.HerdrFor(Ah.Core.Mux.HerdrFlavour.Resolve(
                string.IsNullOrEmpty(session) ? null : services.Mailbox?.ReadRegistration(session), null, null, member)).Invoke(herdrArgs);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new RosterFailure(error.Message);
        }
        finally
        {
            if (zoomed is not null)
            {
                rezoom = PaneZoom.Back(c, zoomed, movedPane);
            }
        }

        if (reply.Member("result")?.Member("move_result") is { IsMap: true } refusal && JsConvert.ToJsString(refusal.Member("changed")) == "false")
        {
            var refused = new JsonObject
            {
                ["moved"] = false,
                ["command"] = commandString,
                ["reason"] = MemberPlanning.Text(refusal.Member("reason")) is { Length: > 0 } why ? why : $"herdr did not move pane {movedPane}",
            };
            if (rezoom is not null)
            {
                refused["rezoom"] = rezoom;
            }

            c.Out(refused);
            return 0;
        }

        var basis = JsonText.Stringify(team.Member("members") ?? JsonValue.Null, 0);
        var attach = opts.Text("tab") is { } targetTab ? StreamAttach(c, team, name, targetTab) : null;
        var result = Resync.ResyncMembers(c, team);
        if (!result.QueryOk)
        {
            c.Out(new JsonObject
            {
                ["moved"] = true,
                ["command"] = commandString,
                ["resync"] = new JsonObject { ["ok"] = false, ["reason"] = RosterJson.Text(result.QueryError) },
                ["stream_attach"] = new JsonObject
                {
                    ["stream"] = attach?.Get("stream") ?? JsonValue.Null,
                    ["attached"] = false,
                    ["why"] = "resync query failed; stream not attached, run ah roster move again",
                },
            });
            return 0;
        }

        var healedMembers = result.Members!.ToList();
        var stripped = new JsonArray();
        JsonObject? journaled = null;
        stripped.AddRange(healedMembers.Select(Resync.StripMeta));
        services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            doc =>
            {
                if (doc.IsNull)
                {
                    return null;
                }

                if (JsonText.Stringify(doc.Member("members") ?? JsonValue.Null, 0) != basis)
                {
                    var conflict = new InvalidOperationException("the team changed during move; run resync");
                    conflict.Data["code"] = "conflict";
                    throw conflict;
                }

                var movedRow = stripped.FirstOrDefault(m => MemberPlanning.Text(m.Member("name")) == name);
                if (attach is { } a && a.Get("attached") is { IsTruthy: true } && movedRow.IsMap)
                {
                    movedRow.AsObject()["stream"] = a.Get("stream")!.Value;
                }

                if (movedRow.IsMap && ParentAction.ActorOf(c) is { } placedBy)
                {
                    movedRow.AsObject()["placed_by"] = placedBy;
                }

                var next = doc.AsObject().Clone();
                next["members"] = stripped;
                journaled = ParentAction.Entry(c, "move", name, attach?.Get("stream") is { IsText: true } attachedStream ? attachedStream.AsString() : null);
                if (journaled is not null)
                {
                    next["parent_actions"] = Ah.Launch.LaunchModule.AppendedParentAction(doc, journaled);
                }

                return TeamFileOutcome.Write(next);
            });
        ParentAction.Notify(c, dir, journaled, "moved");
        JsonValue? healedFound = healedMembers.Cast<JsonValue?>().FirstOrDefault(m => MemberPlanning.Text(m!.Value.Member("name")) == name);
        if (healedFound is not { } healed)
        {
            throw new InvalidOperationException("Cannot read properties of undefined (reading 'status')");
        }

        var status = MemberPlanning.Text(healed.Member("status"));
        var resync = new JsonObject { ["ok"] = true, ["status"] = RosterJson.Text(status) };
        if (status == "updated")
        {
            if (healed.Member("from") is { } from)
            {
                resync["from"] = from;
            }

            if (healed.Member("to") is { } to)
            {
                resync["to"] = to;
            }
        }

        if (result.Warning is not null)
        {
            resync["warning"] = result.Warning;
        }

        var afterTab = status switch
        {
            "updated" => MemberPlanning.Text(healed.Member("to")?.Member("tab_id")),
            "unchanged" => MemberPlanning.Text(member.Member("tab_id")),
            _ => null,
        };
        var confirmable = beforeMove.Tab is not null && afterTab is not null;
        var noop = !confirmable || afterTab == beforeMove.Tab;
        var payload = new JsonObject { ["moved"] = !noop };
        var who = new JsonObject();
        if (member.Member("role") is { } role)
        {
            who["role"] = role;
        }

        if (member.Member("name") is { } memberName)
        {
            who["name"] = memberName;
        }

        payload["member"] = who;
        payload["command"] = commandString;
        if (rezoom is not null)
        {
            payload["rezoom"] = rezoom;
        }

        payload["resync"] = resync;
        if (attach is { } outcome)
        {
            payload["stream_attach"] = outcome;
        }
        else
        {
            payload["stream_attach"] = JsonValue.Null;
        }

        if (noop)
        {
            payload["reason"] = confirmable
                ? $"no-op: herdr accepted the command but pane {MemberPlanning.JsConvertText(member.Member("transport_id"))} is in the same tab and " +
                    "workspace as before (re-query reports no change)"
                : $"cannot confirm the move: the pane's tab could not be read {(beforeMove.Tab is null ? "before" : "after")} it, so the move is not reported";
        }

        c.Out(payload);
        return 0;
    }

    /// <summary>
    /// <c>move &lt;name&gt; --stream B</c>: the fix for a member that works in the wrong tree. In one step it sets the member's
    /// <c>stream</c> to B and its work root to B's worktree, in the team file and in the grants. It moves no pane. A writer is
    /// refused when another live writer holds B's tree, unless <c>--replace-live</c> is given.
    /// </summary>
    internal static int MoveToStream(RosterContext c, string dir, JsonValue team, JsonValue member, string name, Func<JsonObject, JsonObject>? editRow = null, Action<JsonObject>? editResult = null)
    {
        var opts = c.Opts;
        if (opts.Text("stream") is not { Length: > 0 } stream)
        {
            throw new RosterFailure($"move: --stream requires a value: {Usage}");
        }

        if (opts.Has("tab") || opts.Has("split") || opts.Has("workspace") || opts.IsTrue("new-tab") || opts.IsTrue("new-workspace"))
        {
            throw new RosterFailure("move: --stream cannot be combined with --tab, --split, --workspace, --new-tab or --new-workspace");
        }

        var streams = c.Services.Launch!.Streams.StreamsOf(team);
        if (streams.Get(stream) is not { IsTruthy: true } record)
        {
            var unknown = $"move: no stream named {JsonText.Quote(stream)}; nothing was changed";
            throw c.Refuse(new JsonObject { ["refused"] = "unknown-stream", ["stream"] = stream, ["message"] = unknown }, unknown);
        }

        if (MemberPlanning.Text(record.Member("state")) == "done")
        {
            var done = $"move: stream {stream} is done; re-open it first; nothing was changed";
            throw c.Refuse(new JsonObject { ["refused"] = "stream-done", ["stream"] = stream, ["message"] = done }, done);
        }

        var tree = record.Member("worktree") is { IsText: true } worktree && worktree.AsString().Length > 0
            ? WorkTreeAssignment.WorkRootOf(c, worktree.AsString())
            : throw new RosterFailure($"move: stream {stream} has no work tree to move {name} into");
        var role = MemberPlanning.Text(member.Member("role"));
        WorkTreeAssignment.RefuseLiveOccupant(c, "move", name, role, tree, team);
        WorkTreeAssignment.RefuseLiveReroot(c, name, member, tree);

        if (opts.IsTrue("dry-run"))
        {
            var planned = new JsonObject { ["moved"] = false, ["dry_run"] = true, ["stream"] = stream, ["work_root"] = tree };
            editResult?.Invoke(planned);
            c.Out(planned);
            return 0;
        }

        JsonObject? journaled = null;
        c.Services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            doc =>
            {
                if (doc.IsNull)
                {
                    return null;
                }

                var rows = new JsonArray();
                foreach (var row in doc.Member("members") is { IsArray: true } list ? list.AsArray() : [])
                {
                    if (MemberPlanning.Text(row.Member("name")) != name || !row.IsMap)
                    {
                        rows.Add(row);
                        continue;
                    }

                    var moved = row.AsObject().Clone();
                    moved["stream"] = stream;
                    moved["expected_root"] = tree;
                    if (ParentAction.ActorOf(c) is { } placedBy)
                    {
                        moved["placed_by"] = placedBy;
                    }

                    if (Ah.Roster.RosterModule.IsolatedTreeOf(row) is not { } recorded || !WorkTreeAssignment.SameTree(c, recorded, tree))
                    {
                        _ = moved.Remove(Ah.Roster.RosterModule.IsolatedTreeKey);
                        _ = moved.Remove(Ah.Roster.RosterModule.IsolatedPaneKey);
                    }

                    rows.Add(editRow is null ? moved : editRow(moved));
                }

                var next = doc.AsObject().Clone();
                next["members"] = rows;
                journaled = ParentAction.Entry(c, "move", name, stream);
                if (journaled is not null)
                {
                    next["parent_actions"] = Ah.Launch.LaunchModule.AppendedParentAction(doc, journaled);
                }

                return TeamFileOutcome.Write(next);
            });
        RosterLedger.Member(c, "move", "moved", name, role);
        ParentAction.Notify(c, dir, journaled, $"moved to stream {stream}");
        var result = new JsonObject { ["moved"] = true, ["member"] = name, ["stream"] = stream, ["work_root"] = tree };
        try
        {
            if (WorkTreeAssignment.Assign(c, name, role, tree, stream) is { } warning)
            {
                result["warning"] = warning;
            }
        }
        finally
        {
            editResult?.Invoke(result);
        }

        c.Out(result);
        return 0;
    }

    /// <summary>
    /// Whether a member moved into tab <paramref name="tab"/> joins the open stream that owns that tab: null when no open stream
    /// does; otherwise <c>{stream, attached}</c> plus <c>why</c> when not attached. A member belongs to a stream when it works in the
    /// stream's work tree (or the stream has none); the tab is display.
    /// </summary>
    private static JsonObject? StreamAttach(RosterContext c, JsonValue team, string member, string tab)
    {
        var streams = c.Services.Launch!.Streams.StreamsOf(team);
        foreach (var stream in streams.Keys)
        {
            if (streams.Get(stream) is not { IsMap: true } record
                || MemberPlanning.Text(record.Member("state")) != "open"
                || MemberPlanning.Text(record.Member("tab_id")) != tab)
            {
                continue;
            }

            var outcome = new JsonObject { ["stream"] = stream };
            if (record.Member("worktree") is not { IsText: true } tree || tree.AsString().Length == 0)
            {
                outcome["attached"] = true;
                return outcome;
            }

            var workRoot = WorkTreeAssignment.RecordedRootOf(c, member);
            var attached = workRoot is not null && Real(c, workRoot) == Real(c, tree.AsString());
            outcome["attached"] = attached;
            if (!attached)
            {
                outcome["why"] = workRoot is null
                    ? $"member has no recorded work tree; respawn it with --stream {stream}"
                    : $"member works in {workRoot}, stream work tree is {tree.AsString()}; respawn it with --stream {stream}";
            }

            return outcome;
        }

        return null;
    }

    private static string Real(RosterContext c, string path) => c.Services.Files.Exists(path) ? c.Services.Files.RealPath(path) : path;
}
