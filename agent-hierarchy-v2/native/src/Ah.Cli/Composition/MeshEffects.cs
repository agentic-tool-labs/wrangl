using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Cli.Composition;

/// <summary>
/// The writes the link and join commands make, in the daemon's process: <c>links</c> and <c>msgs</c> state calls, one
/// mailbox entry per notice, the task mirror, and the member row. The commands hand each write the directory it belongs to,
/// and the state context is built from that directory the way the hierarchy library builds a pool's.
/// A command line run defaults to the caller's own <paramref name="pinned"/> context, sends notices as <paramref name="session"/> (the
/// daemon's own notices carry none) and delivers them through <paramref name="send"/>, except a notice from <c>ahd</c>, which goes to the mailbox directly.
/// </summary>
internal sealed class MeshEffects(
    ServiceGraph graph,
    RequestScope scope,
    RequestContext? pinned = null,
    string? session = null,
    Func<JsonObject, JsonObject>? send = null) : IJoinEffects, ILinkMirror
{
    public Task<JsonObject> LinksAsync(string op, JsonObject args, string? opId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var dir = args.Get("dir") is { IsText: true } d ? d.AsString() : throw new InvalidOperationException("a links operation needs args.dir");
        return Task.FromResult(Invoke("links", op, args, dir, opId));
    }

    public Task<bool> CreateMessageAsync(string dir, string path, string content, string opId, CancellationToken cancellation, bool ownPoolContext = false)
    {
        cancellation.ThrowIfCancellationRequested();
        var args = new JsonObject { ["dir"] = dir, ["path"] = path, ["content"] = content };
        try
        {
            _ = Invoke("msgs", "create", args, dir, opId, ownPoolContext);
            return Task.FromResult(true);
        }
        catch (InvalidOperationException e) when (e.Message.Contains("EEXIST", StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }
    }

    public Task SettleMessageAsync(string dir, string path, string content, string from, string opId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var args = new JsonObject { ["dir"] = dir, ["path"] = path, ["content"] = content, ["from"] = from };
        _ = Invoke("msgs", "settle", args, dir, opId);
        return Task.CompletedTask;
    }

    public string? LaneMembersRefusal(string dir, string team) => LaneRefusalOf(dir, graph.Roster.ReadTeamRecord(dir, team));

    private string? LaneRefusalOf(string dir, JsonValue child)
    {
        if (child.Member("parent") is not { IsMap: true } link
            || link.Member("team") is not { IsText: true } manager || link.Member("stream") is not { IsText: true } stream)
        {
            return null;
        }

        var max = RosterModule.LaneCaps(graph.Roster.ReadTeamRecord(dir, manager.AsString()), stream.AsString()).Members;
        var rows = RosterModule.LaneMemberRows(child);
        return RosterModule.LaneRowsAtCap(rows, max) ? $"lane-members-full: {rows}/{max}; dismiss one or ask the top Orchestrator to raise the cap" : null;
    }

    public Task AddMemberAsync(string dir, string team, JsonObject row, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var name = row.Get("name") is { IsText: true } n ? n.AsString() : string.Empty;
        _ = graph.Hierarchy.StateCalls.UpdateTeam(
            dir,
            team,
            doc =>
            {
                if (!doc.IsMap || doc.Member("members") is not { IsArray: true } members)
                {
                    throw new InvalidOperationException($"team {team} has no readable team file");
                }

                if (members.AsArray().Any(m => m.IsMap && m.Member("name") is { IsText: true } existing && existing.AsString() == name))
                {
                    throw new InvalidOperationException($"team {team} already has a member named {name}");
                }

                if (LaneRefusalOf(dir, doc) is { } laneFull)
                {
                    throw new InvalidOperationException(laneFull);
                }

                var next = new JsonArray();
                next.AddRange(members.AsArray());
                next.Add(row);
                var written = doc.AsObject().Clone();
                written["members"] = next;
                return Ah.Hierarchy.StateCalls.TeamFileOutcome.Write(written);
            });
        return Task.CompletedTask;
    }

    public Task NotifyAsync(LinkNotice notice, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var context = scope.Current ?? pinned;
        if (context is null)
        {
            var dir = HierarchyDirOf(notice.To) ?? throw new InvalidOperationException($"no live registration places {notice.To}");
            context = Pool(dir);
        }

        var request = new JsonObject
        {
            ["to"] = notice.To,
            ["from"] = notice.From,
            ["session_id"] = session is null || notice.From == "ahd" ? JsonValue.Null : session,
            ["cwd"] = context.Cwd,
            ["hierarchy_dir"] = context.HierarchyDirectory,
            ["id"] = graph.Hierarchy.Core.NewId(),
            ["kind"] = notice.Kind,
        };
        if (notice.Event is not null)
        {
            request["event"] = notice.Event;
        }

        request["text"] = notice.Text;
        if (notice.PostLink is not null)
        {
            request["post_link"] = notice.PostLink;
        }

        if (context.MainHierarchyDirectory.Length > 0)
        {
            request["main_hierarchy_dir"] = context.MainHierarchyDirectory;
        }

        if (context.SocketDirectory.Length > 0)
        {
            request["messaging_socket_dir"] = context.SocketDirectory;
        }

        // A notice in the daemon's own name (a link-request's auto-grant) is written here as the daemon writes it: the caller's
        // send route refuses "ahd" from every caller (atp-1717), and the refusal came after the request and its grant were filed.
        if (send is not null && notice.From != "ahd")
        {
            _ = send(request);
            return Task.CompletedTask;
        }

        var sent = graph.Mailbox.Send(request);
        if (sent.Failure is { } failure)
        {
            throw new InvalidOperationException($"{failure.Code}: {failure.Message}");
        }

        return Task.CompletedTask;
    }

    public Task MirrorAsync(LinkMirror mirror, CancellationToken cancellation)
    {
        var context = Pool(mirror.TaskHome);
        var rule = MeshModule.CreateMeshRule(graph.Links, graph.Environment);
        var inner = MeshModule.CreateLinkMirror(
            graph.Mailbox, rule, graph.Links, graph.Hierarchy, graph.Io.Clock, graph.Writer, graph.Io.Files, context, graph.RunFacts);
        return inner.MirrorAsync(mirror, cancellation);
    }

    private string? HierarchyDirOf(string address)
    {
        foreach (var registration in graph.Mailbox.LiveRegistrations())
        {
            if (graph.Mailbox.RegistrationBoxes(registration).Any(b => b.Address == address)
                && MeshModule.RegistrationTeamFileOf(registration, address) is { } file)
            {
                return RosterModule.TeamFileHome(file) is { Length: > 0 } home ? home : NodePath.Dirname(file);
            }
        }

        return null;
    }

    private RequestContext Pool(string dir, bool ownPoolContext = false)
    {
        if (pinned is not null && !ownPoolContext)
        {
            return pinned;
        }

        var root = AhPaths.RepoRootOf(dir) ?? graph.Facts.CurrentDirectory;
        var socket = graph.Environment.GetVariable("CLAUDE_CODE_MESSAGING_SOCKET");
        return new RequestContext(
            root,
            dir,
            graph.Config.MainHierarchyDir(root) ?? string.Empty,
            string.IsNullOrEmpty(socket) ? string.Empty : NodePath.Dirname(socket));
    }

    private JsonObject Invoke(string domain, string op, JsonObject args, string dir, string? opId, bool ownPoolContext = false)
    {
        var bytes = JsonText.ToUtf8(JsonValue.FromObject(args), 0);
        var result = graph.Writer.Invoke(domain, op, bytes, Pool(dir, ownPoolContext), opId is null ? null : new StateCallOptions(OperationId: opId));
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? $"{domain}.{op} failed");
        }

        if (result.ResultJson.IsEmpty)
        {
            return new JsonObject();
        }

        var parsed = JsonText.Parse(result.ResultJson.Span);
        return parsed.IsMap ? parsed.AsObject() : new JsonObject();
    }
}
