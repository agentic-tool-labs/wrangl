using Ah.Core.Json;

namespace Ah.Watch;

internal static partial class ViewBuilder
{
    /// <summary>A mail edge is fresh when its newest entry is younger than this and not yet acked.</summary>
    private const double FreshMailMs = 5 * 60_000;

    /// <summary>A mail edge covers the entries of this long before now.</summary>
    private const double MailEdgeWindowMs = 60 * 60_000;

    private const int MailEdgeEntriesPerBox = 50;

    internal static string MemberGraphState(JsonObject member)
    {
        if (!(member.TryGetValue("live", out var live) && live.Kind == JsonKind.Bool && live.AsBool()))
        {
            return "notlive";
        }

        if (Truthy(Get(member, "blocked_by")))
        {
            return "blocked";
        }

        return member.Str("activity") switch
        {
            "working" => "busy",
            "idle" => "idle",
            "blocked" or "failed" or "blocked-on-job" => "blocked",
            _ => "live",
        };
    }

    /// <summary>The session ids the team files of <paramref name="orch"/> record for their Orchestrator.</summary>
    private static HashSet<string> OrchSessions(Orch orch) =>
        orch.Teams.Select(static t => t.Doc.Obj("orchestrator")?.Str("session_id")).OfType<string>().Where(static id => id.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The topology of one orchestrator, with no coordinates: its nodes, the edges between them and the order hints a layer lays
    /// them out by. A layer decides positions; two layers given the same hints agree on what is next to what.
    /// </summary>
    private static BuiltView GraphDocument(ModelCtx c, Node node, string fullPath)
    {
        if (node.Kind != "orchestrator")
        {
            return c.NotFound(node.Kind == "root" ? "/" : ParentPath(c, node));
        }

        var orch = node.Orch!;
        var src = c.Src;
        var nodes = new JsonArray();
        var edges = new List<JsonObject>();
        var order = 0;
        var memberNode = new Dictionary<string, string>(StringComparer.Ordinal);
        var teamOrder = new JsonArray();
        var memberOrder = new JsonObject();
        var modelled = new List<JsonObject>();

        nodes.Add(new JsonObject
        {
            ["id"] = c.OPath(orch),
            ["kind"] = "orchestrator",
            ["role"] = "orchestrator",
            ["state"] = c.OrchState(orch) == "live" ? "live" : c.OrchState(orch) == "orphaned" ? "orphaned" : "notlive",
            ["label"] = c.OrchLabel(orch),
            ["team"] = JsonValue.Null,
            ["order"] = order++,
        });

        var peers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var team in orch.Teams)
        {
            var facts = c.FactsOf(orch, team);
            teamOrder.Add(team.Tkey);
            var names = new JsonArray();
            foreach (var m in facts.Members)
            {
                var name = m.Str("name")!;
                names.Add(name);
                var id = c.MPath(orch, team, name);
                _ = memberNode.TryAdd(name, id);
                var member = new JsonObject
                {
                    ["id"] = id,
                    ["kind"] = "member",
                    ["role"] = Orn(Get(m, "role")),
                    ["model"] = TextOrNull(c.Src, Get(m.Obj("row") ?? new JsonObject(), "model"), NameCap),
                    ["model_short"] = JsonValue.Null,
                    ["state"] = MemberGraphState(m),
                    ["label"] = name,
                    ["team"] = team.Tkey,
                    ["order"] = order++,
                };
                nodes.Add(member);
                modelled.Add(member);
                edges.Add(Edge($"team:{id}", "team", c.OPath(orch), id, "out", double.NaN, false));
            }

            memberOrder[team.Tkey] = names;
            foreach (var stream in facts.Work.Streams)
            {
                var streamId = c.SPath(orch, team, stream.Name);
                nodes.Add(new JsonObject
                {
                    ["id"] = streamId,
                    ["kind"] = "stream",
                    ["role"] = JsonValue.Null,
                    ["state"] = stream.GlyphState,
                    ["label"] = TextOrNull(src, stream.Name, NameCap),
                    ["team"] = team.Tkey,
                    ["order"] = order++,
                });
                if (stream.FactsOf.TryGetValue("peers", out var pv) && pv.Kind == JsonKind.Array)
                {
                    foreach (var p in WatchJson.Objects(pv.AsArray()))
                    {
                        if (p.Str("name") is { } peerName && facts.Members.Any(m => m.Str("name") == peerName))
                        {
                            edges.Add(Edge($"stream:{streamId}>{c.MPath(orch, team, peerName)}", "stream", streamId, c.MPath(orch, team, peerName), "both", double.NaN, false));
                        }
                    }
                }
            }

            foreach (var link in facts.Work.Links)
            {
                var linkId = link.Str("id")!;
                var age = ParseMs(link.Str("granted_at")) is var granted && !double.IsNaN(granted) ? Math.Max(0, c.Now - granted) : double.NaN;
                var direction = link.Str("direction") == "both" ? "both" : "out";
                if (link.TryGetValue("peers", out var lp) && lp.Kind == JsonKind.Array)
                {
                    foreach (var peer in lp.AsArray().Where(static p => p.Kind == JsonKind.Text).Select(static p => p.AsString()))
                    {
                        if (peer == team.Name || peer.Length == 0)
                        {
                            continue;
                        }

                        var peerId = "peer:" + Seg(peer);
                        peers[peerId] = peer;
                        edges.Add(Edge($"mesh:{linkId}:{peerId}", "mesh", c.OPath(orch), peerId, direction, age, false));
                    }
                }
            }
        }

        foreach (var (peerId, peerName) in peers)
        {
            nodes.Add(new JsonObject
            {
                ["id"] = peerId,
                ["kind"] = "peer",
                ["role"] = JsonValue.Null,
                ["state"] = "unknown",
                ["label"] = TextOrNull(src, peerName, NameCap),
                ["team"] = JsonValue.Null,
                ["order"] = order++,
            });
        }

        var running = c.Runs.Value.Where(static r => r.Status == "started").ToList();
        foreach (var team in orch.Teams)
        {
            foreach (var m in c.FactsOf(orch, team).Members)
            {
                if (m.Str("session_id") is not { Length: > 0 } sid)
                {
                    continue;
                }

                var memberId = c.MPath(orch, team, m.Str("name")!);
                foreach (var run in running.Where(r => r.SessionId == sid))
                {
                    var subId = $"{memberId}/sub/{Seg(run.AgentId)}";
                    nodes.Add(new JsonObject
                    {
                        ["id"] = subId,
                        ["kind"] = "subagent",
                        ["role"] = JsonValue.Null,
                        ["state"] = "busy",
                        ["label"] = TextOrNull(src, run.AgentId, NameCap),
                    ["agent_type"] = TextOrNull(src, run.AgentType, NameCap),
                        ["team"] = team.Tkey,
                        ["order"] = order++,
                    });
                    var startedMs = ParseMs(run.StartedAt);
                    edges.Add(Edge($"subagent:{subId}", "subagent", memberId, subId, "out", double.IsNaN(startedMs) ? double.NaN : Math.Max(0, c.Now - startedMs), false));
                }
            }
        }

        // The Orchestrator's own subagents, under its node (ah2-201).
        var orchSessions = OrchSessions(orch);
        foreach (var run in running.Where(r => orchSessions.Contains(r.SessionId)))
        {
            var subId = $"{c.OPath(orch)}/sub/{Seg(run.AgentId)}";
            nodes.Add(new JsonObject
            {
                ["id"] = subId,
                ["kind"] = "subagent",
                ["role"] = JsonValue.Null,
                ["state"] = "busy",
                ["label"] = TextOrNull(src, run.AgentId, NameCap),
                ["agent_type"] = TextOrNull(src, run.AgentType, NameCap),
                ["team"] = JsonValue.Null,
                ["order"] = order++,
            });
            var startedMs = ParseMs(run.StartedAt);
            edges.Add(Edge($"subagent:{subId}", "subagent", c.OPath(orch), subId, "out", double.IsNaN(startedMs) ? double.NaN : Math.Max(0, c.Now - startedMs), false));
        }

        var withModel = modelled.Where(static n => n.Str("model") is { Length: > 0 }).ToList();
        var shorts = ModelShort.Document(withModel.Select(static n => n.Str("model")!).ToList());
        for (var i = 0; i < withModel.Count; i++)
        {
            withModel[i]["model_short"] = shorts[i];
        }

        MailEdges(c, orch, memberNode, edges);
        var edgeList = new JsonArray();
        foreach (var e in edges.OrderBy(static e => e.Str("id"), StringComparer.Ordinal))
        {
            edgeList.Add(e);
        }

        var view = ModelEnvelope(c, "graph", node, fullPath);
        view["summary"] = new JsonObject { ["nodes"] = nodes.Count, ["edges"] = edgeList.Count };
        view["children"] = new JsonArray();
        view["body"] = new JsonObject
        {
            ["nodes"] = nodes,
            ["edges"] = edgeList,
            ["hints"] = new JsonObject { ["team_order"] = teamOrder, ["member_order_in_team"] = memberOrder, ["centre"] = c.OPath(orch) },
        };
        return Finish(view);
    }

    private static JsonObject Edge(string id, string kind, string from, string to, string direction, double ageMs, bool fresh) => new()
    {
        ["id"] = id,
        ["kind"] = kind,
        ["from"] = from,
        ["to"] = to,
        ["direction"] = direction,
        ["age_ms"] = double.IsNaN(ageMs) ? JsonValue.Null : Math.Floor(ageMs),
        ["fresh"] = fresh,
    };

    /// <summary>One mail edge per sender and receiver among this orchestrator's nodes, from the boxes' entries of the last hour; it names the newest of those entries by id and by the path of its record.</summary>
    private static void MailEdges(ModelCtx c, Orch orch, Dictionary<string, string> memberNode, List<JsonObject> edges)
    {
        var groups = new SortedDictionary<string, (string From, string To, double Newest, bool Waiting, string? RecordId, string? RecordPath)>(StringComparer.Ordinal);
        foreach (var team in orch.Teams)
        {
            var recipients = c.FactsOf(orch, team).Members.Select(static m => m.Str("name")!).Append("orchestrator").Distinct(StringComparer.Ordinal);
            foreach (var recipient in recipients)
            {
                var to = recipient == "orchestrator" ? c.OPath(orch) : c.MPath(orch, team, recipient);
                foreach (var entry in c.Src.BoxEntries(c.Src.BoxDirFor(team.File, recipient), MailEdgeEntriesPerBox, index: true))
                {
                    var from = NodeOfAddress(c, orch, memberNode, entry.Str("from"));
                    var created = ParseMs(entry.Str("created"));
                    if (from is null || double.IsNaN(created) || c.Now - created > MailEdgeWindowMs || from == to)
                    {
                        continue;
                    }

                    var key = from + ">" + to;
                    var recordId = entry.Str("id") is { Length: > 0 } id ? id : null;
                    var recordPath = recordId is null ? null : (recipient == "orchestrator" ? c.TPath(orch, team) : to) + "/mail/" + Seg(recordId);
                    var waiting = !(entry.TryGetValue("acked", out var acked) && acked.Kind == JsonKind.Bool && acked.AsBool());
                    if (!groups.TryGetValue(key, out var held) || created > held.Newest)
                    {
                        groups[key] = (from, to, created, waiting, recordId, recordPath);
                    }
                    else if (created == held.Newest && ((waiting && !held.Waiting) || (waiting == held.Waiting && string.CompareOrdinal(recordId, held.RecordId) > 0)))
                    {
                        groups[key] = (from, to, created, waiting, recordId, recordPath);
                    }
                }
            }
        }

        foreach (var (key, g) in groups)
        {
            var age = Math.Max(0, c.Now - g.Newest);
            var edge = Edge("mail:" + key, "mail", g.From, g.To, "out", age, age < FreshMailMs && g.Waiting);
            edge["mail_id"] = g.RecordId is null ? JsonValue.Null : g.RecordId;
            edge["mail_path"] = g.RecordPath is null ? JsonValue.Null : g.RecordPath;
            edges.Add(edge);
        }
    }

    private static string? NodeOfAddress(ModelCtx c, Orch orch, Dictionary<string, string> memberNode, string? address)
    {
        if (address is null)
        {
            return null;
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        var name = at < 0 ? address : address[..at];
        if (name == "orchestrator")
        {
            return c.OPath(orch);
        }

        return memberNode.TryGetValue(name, out var id) ? id : null;
    }
}
