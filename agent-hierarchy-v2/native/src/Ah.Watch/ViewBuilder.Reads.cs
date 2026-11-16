using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

internal static partial class ViewBuilder
{
    private const int InboxEntries = 50;

    /// <summary>The team's object in a directory's status document, or null when the status holds none for it.</summary>
    private static JsonObject? StatusTeamOf(JsonObject status, TeamEntry team)
    {
        if (!status.TryGetValue("teams", out var teamsValue) || teamsValue.Kind != JsonKind.Array)
        {
            return null;
        }

        foreach (var t in WatchJson.Objects(teamsValue.AsArray()))
        {
            var tn = Get(t, "team");
            if (tn.Kind == JsonKind.Null ? team.Name is null : tn.Kind == JsonKind.Text && tn.AsString() == team.Name)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>The body of a stream's node view: its state, branch, work tree, peers and what it waits on.</summary>
    private static JsonObject StreamBody(IViewSource src, Facts facts, StreamRow row)
    {
        var worktree = row.FactsOf.Obj("worktree");
        var peers = new JsonArray();
        if (row.FactsOf.TryGetValue("peers", out var pv) && pv.Kind == JsonKind.Array)
        {
            foreach (var p in WatchJson.Objects(pv.AsArray()))
            {
                peers.Add(new JsonObject
                {
                    ["name"] = TextOrNull(src, Get(p, "name"), NameCap),
                    ["role"] = TextOrNull(src, Get(p, "role"), NameCap),
                    ["status"] = TextOrNull(src, Get(p, "status"), NameCap),
                    ["in_stream_tab"] = p.TryGetValue("in_stream_tab", out var inTab) && inTab.Kind == JsonKind.Bool && inTab.AsBool(),
                });
            }
        }

        var waiting = new JsonArray();
        if (row.FactsOf.TryGetValue("waiting_on", out var wv) && wv.Kind == JsonKind.Array)
        {
            foreach (var x in WatchJson.Objects(wv.AsArray()))
            {
                var req = x.Str("request");
                var createdAt = req is not null && facts.Work.CreatedOf.TryGetValue(req, out var cv) && cv.Kind == JsonKind.Text && Timestamps.TryParseMs(cv.AsString(), out var cms)
                    ? IsoOf(cms)
                    : JsonValue.Null;
                waiting.Add(new JsonObject
                {
                    ["on"] = TextOrNull(src, Get(x, "on"), NameCap),
                    ["request"] = TextOrNull(src, Get(x, "request"), NameCap),
                    ["since"] = createdAt,
                    ["age_sec"] = x.TryGetValue("age_sec", out var ag) && ag.Kind == JsonKind.Number && double.IsFinite(ag.AsNumber()) ? ag : JsonValue.Null,
                });
            }
        }

        return new JsonObject
        {
            ["state"] = TextOrNull(src, Get(row.Rec, "state"), NameCap),
            ["branch"] = TextOrNull(src, Get(row.FactsOf, "branch"), NameCap),
            ["base"] = TextOrNull(src, Get(row.FactsOf, "base"), NameCap),
            ["worktree"] = worktree is not null
                ? new JsonObject { ["path"] = TextOrNull(src, Get(worktree, "path"), 400), ["exists"] = worktree.TryGetValue("exists", out var ex) && ex.Kind == JsonKind.Bool && ex.AsBool() }
                : new JsonObject { ["path"] = JsonValue.Null, ["exists"] = false },
            ["glyph_state"] = row.GlyphState,
            ["peers"] = peers,
            ["waiting_on"] = waiting,
            ["reason"] = facts.Work.Reason is null ? JsonValue.Null : facts.Work.Reason,
        };
    }

    /// <summary>The mailbox address of a member of a team: <c>name@team</c>, or the bare name for the default team.</summary>
    private static string AddressOf(TeamEntry team, string name) => team.Name is null ? name : $"{name}@{team.Name}";

    private static BuiltView ReadDocument(ModelCtx c, string kind, Node node, string fullPath)
    {
        JsonObject body;
        switch (kind)
        {
            case "status" when node.Kind is "team":
                body = TeamStatus(c, node.Orch!, node.Team!);
                break;
            case "status" when node.Kind is "orchestrator":
                var teams = new JsonArray();
                foreach (var t in node.Orch!.Teams)
                {
                    teams.Add(TeamStatus(c, node.Orch, t));
                }

                body = new JsonObject { ["teams"] = teams };
                break;
            case "stream-status" when node.Kind is "team" or "stream":
                body = StreamStatus(c, node);
                break;
            case "inbox" when node.Kind is "member" or "orchestrator":
                body = Inbox(c, node);
                break;
            default:
                return c.NotFound(ParentPath(c, node));
        }

        var view = ModelEnvelope(c, kind, node, fullPath);
        view["summary"] = new JsonObject();
        view["children"] = new JsonArray();
        view["body"] = body;
        return Finish(view);
    }

    private static JsonObject TeamStatus(ModelCtx c, Orch orch, TeamEntry team) => new()
    {
        ["team"] = team.Name is null ? JsonValue.Null : team.Name,
        ["status"] = StatusTeamOf(c.StatusOf(team.Dir), team) is { } status ? status : JsonValue.Null,
    };

    private static JsonObject StreamStatus(ModelCtx c, Node node)
    {
        var team = node.Team!;
        var facts = node.Facts!;
        var streams = new JsonArray();
        foreach (var row in facts.Work.Streams.Where(r => node.Stream is null || r.Name == node.Stream.Name))
        {
            var entry = StreamBody(c.Src, facts, row);
            entry["name"] = row.Name;
            streams.Add(entry);
        }

        var body = new JsonObject
        {
            ["team"] = team.Name is null ? JsonValue.Null : team.Name,
            ["transport"] = TextOrNull(c.Src, team.Doc.Str("transport"), NameCap),
            ["streams"] = streams,
        };
        if (facts.Work.Reason is { } reason)
        {
            body["degraded"] = reason;
        }

        return body;
    }

    private static JsonObject Inbox(ModelCtx c, Node node)
    {
        var boxes = new JsonArray();
        if (node.Kind == "member")
        {
            boxes.Add(BoxOf(c, node.Team!, node.Member!.Str("name")!));
        }
        else
        {
            foreach (var team in node.Orch!.Teams)
            {
                boxes.Add(BoxOf(c, team, "orchestrator"));
            }
        }

        return new JsonObject { ["boxes"] = boxes };
    }

    private static JsonObject BoxOf(ModelCtx c, TeamEntry team, string member)
    {
        var entries = new JsonArray();
        foreach (var e in c.Src.BoxEntries(c.Src.BoxDirFor(team.File, member), InboxEntries, index: true))
        {
            entries.Add(e);
        }

        return new JsonObject { ["address"] = AddressOf(team, member), ["entries"] = entries };
    }
}
