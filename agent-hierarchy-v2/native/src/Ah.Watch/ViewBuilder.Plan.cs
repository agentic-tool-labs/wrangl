using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>The <c>plan</c> document of an orchestrator: the streams of the teams it owns, their planned items, and where each item's commits stand.</summary>
internal static partial class ViewBuilder
{
    private const int PlanItemCap = 200;
    private const int PlanDoneCap = 50;

    // The state tokens of a served plan item; Ah.Hierarchy writes the same words into the plan log.
    private const string InProgress = "in_progress";
    private const string NotStarted = "not_started";
    private const string Done = "done";
    private const string Dropped = "dropped";
    private const string WaitingUser = "waiting_user";
    private const string WaitingOrch = "waiting_orch";

    private sealed record PlanStream(string TeamKey, TeamEntry Team, StreamProgress Progress, PlanState? Plan, IReadOnlyList<PlanItem> Served, int More);

    private static BuiltView PlanDocument(ModelCtx c, Node node, string fullPath)
    {
        if (node.Kind != "orchestrator")
        {
            return c.NotFound(ParentPath(c, node));
        }

        var view = ModelEnvelope(c, "plan", node, fullPath);
        view["summary"] = new JsonObject();
        view["children"] = new JsonArray();
        view["body"] = PlanBody(c, node, c.Options.Split('&').Contains("all=1", StringComparer.Ordinal));
        return Finish(view);
    }

    private static JsonValue Opt(string? text) => text is null ? JsonValue.Null : JsonValue.FromString(text);

    private static JsonArray Strs(IEnumerable<string> items)
    {
        var list = new JsonArray();
        foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static TeamProgress? ReadTeamProgress(IViewProgress progress, TeamEntry team)
    {
        try
        {
            return progress.Read(team.Dir, team.Name ?? "_default");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static PlanState? ReadBacklogPlan(IViewProgress? progress, string dir)
    {
        try
        {
            return progress?.ReadBacklog(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The items to serve for a stream: in progress, then those waiting on the Orchestrator or the user, then not started, then done, then dropped, each newest first, done items beyond the newest 50 and everything beyond 200 only counted.</summary>
    private static (IReadOnlyList<PlanItem> Served, int More) PlanItemsOf(PlanState plan, bool all)
    {
        static string Newest(PlanItem i, string state) => state switch
        {
            InProgress => i.StartedAt ?? i.AddedAt,
            Done => i.DoneAt ?? i.AddedAt,
            _ => i.AddedAt,
        };

        var served = new List<PlanItem>();
        foreach (var state in new[] { InProgress, WaitingOrch, WaitingUser, NotStarted, Done, Dropped })
        {
            var group = plan.Items.Where(i => i.State == state).OrderByDescending(i => Newest(i, state), StringComparer.Ordinal).ToList();
            served.AddRange(state == Done && !all ? group.Take(PlanDoneCap) : group);
        }

        var shown = served.Take(PlanItemCap).ToList();
        return (shown, plan.Items.Count - shown.Count);
    }

    private static JsonObject PlanBody(ModelCtx c, Node node, bool all)
    {
        var orch = node.Orch!;
        var reads = c.Src as IDetailReads;
        var progress = reads?.Progress;
        var read = orch.Teams.Select(t => (Team: t, Progress: progress is null ? null : ReadTeamProgress(progress, t))).ToList();
        var present = read.Any(static r => r.Progress is { Off: false } p && (p.RollupPresent || p.Streams.Count > 0));
        var anyOff = read.Any(static r => r.Progress is { Off: true });

        var trees = reads?.Trees(orch.Dir, [.. orch.Teams.Select(static t => t.Name ?? "team")]) ?? [];
        var repo = trees.Count > 0 ? trees[0] : null;
        var config = repo is null || reads is null ? null : reads.ReadText(NodePath.Join(repo, ".git", "config"));
        var origin = config is null ? null : PlanLinkage.OriginUrl(config);
        var linkage = reads?.Git is { } git && repo is not null ? new PlanLinkage(git, repo, config is not null && PlanLinkage.HasRemote(config)) : null;

        var streams = new List<PlanStream>();
        if (progress is not null)
        {
            foreach (var (team, tp) in read)
            {
                if (tp is not { Off: false })
                {
                    continue;
                }

                var teamKey = team.Name ?? "_default";
                foreach (var sp in tp.Streams.Where(static s => s.HasChecklist || s.Planned))
                {
                    var plan = sp.Planned ? progress.ReadPlan(team.Dir, teamKey, sp.Stream) : null;
                    var (served, more) = plan is null ? ([], 0) : PlanItemsOf(plan, all);
                    streams.Add(new PlanStream(teamKey, team, sp, plan, served, more));
                }
            }
        }

        // the project's backlog is listed first: one more stream, with no team
        var backlog = ReadBacklogPlan(progress, orch.Dir);
        var (backlogServed, backlogMore) = backlog is null ? ([], 0) : PlanItemsOf(backlog, all);
        var links = linkage?.Resolve([.. backlogServed.Concat(streams.SelectMany(static s => s.Served)).Select(static i => (IReadOnlyList<string>)i.Commits)]);
        var linkAt = 0;
        JsonArray ItemsJson(IReadOnlyList<PlanItem> served)
        {
            var items = new JsonArray();
            foreach (var item in served)
            {
                var link = links is null
                    ? new ItemLink(item.Commits.Count == 0 ? PlanLinkage.NoCommits : GitRunner.UnavailableReason, null, [], null)
                    : links[linkAt];
                linkAt++;
                items.Add(PlanItemJson(item, link, origin));
            }

            return items;
        }

        static JsonObject Counts(PlanState p) => new()
        {
            ["done"] = p.Items.Count(static i => i.State == Done),
            ["in_progress"] = p.Items.Count(static i => i.State == InProgress),
            ["not_started"] = p.Items.Count(static i => i.State == NotStarted),
            ["dropped"] = p.Items.Count(static i => i.State == Dropped),
        };

        var streamsJson = new JsonArray();
        if (backlog is not null)
        {
            streamsJson.Add(new JsonObject
            {
                ["team"] = string.Empty,
                ["stream"] = HierarchyModule.BacklogStream,
                ["title"] = "Backlog",
                ["owner"] = JsonValue.Null,
                ["branch"] = JsonValue.Null,
                ["state"] = JsonValue.Null,
                ["updated_at"] = JsonValue.Null,
                ["stale"] = false,
                ["planned"] = true,
                ["counts"] = Counts(backlog),
                ["items"] = ItemsJson(backlogServed),
                ["more"] = backlogMore,
            });
        }

        foreach (var s in streams)
        {
            var info = progress!.Describe(s.Team.Dir, s.TeamKey, s.Progress.Stream);
            var items = ItemsJson(s.Served);
            var counts = s.Plan is { } p
                ? Counts(p)
                : new JsonObject { ["done"] = s.Progress.Done, ["in_progress"] = s.Progress.InProgress, ["not_started"] = s.Progress.NotStarted, ["dropped"] = 0 };
            streamsJson.Add(new JsonObject
            {
                ["team"] = s.TeamKey,
                ["stream"] = s.Progress.Stream,
                ["title"] = Opt(s.Progress.Title),
                ["owner"] = Opt(info.Owner is "—" ? null : info.Owner),
                ["branch"] = Opt(info.Branch is "—" ? null : info.Branch),
                ["state"] = Opt(s.Progress.State),
                ["updated_at"] = s.Progress.Updated is { } u ? PlanTimeText(u) : JsonValue.Null,
                ["stale"] = s.Progress.Stale,
                ["planned"] = s.Progress.Planned,
                ["counts"] = counts,
                ["items"] = items,
                ["more"] = s.More,
            });
        }

        var releases = new JsonArray();
        foreach (var r in linkage?.Releases().Releases ?? [])
        {
            releases.Add(new JsonObject { ["tag"] = r.Tag, ["date"] = r.Date });
        }

        JsonArray? queue = null;
        foreach (var (team, tp) in read)
        {
            var teamKey = team.Name ?? "_default";
            if (progress?.ReadQueue(team.Dir, teamKey) is { } rows)
            {
                queue ??= [];
                foreach (var row in rows)
                {
                    if (row.IsMap)
                    {
                        row.AsObject()["team"] = teamKey;
                    }

                    queue.Add(row);
                }
            }
        }

        var body = new JsonObject
        {
            ["node"] = new JsonObject { ["kind"] = node.Kind, ["path"] = node.Path },
            ["generated_at"] = IsoOf(c.Now),
            ["source"] = new JsonObject
            {
                ["present"] = present || backlog is not null,
                ["reason"] = present || backlog is not null ? JsonValue.Null : anyOff ? "progress is off" : "no progress records",
            },
            ["releases"] = releases,
            ["streams"] = streamsJson,
            ["unknown_events"] = streams.Sum(static s => s.Plan?.UnknownEvents ?? 0) + (backlog?.UnknownEvents ?? 0),
        };
        if (queue is not null)
        {
            body["queue"] = queue;
        }

        return body;
    }

    private static string PlanTimeText(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject PlanItemJson(PlanItem item, ItemLink link, string? origin)
    {
        var json = new JsonObject
        {
            ["item"] = item.Item,
            ["title"] = item.Title,
            ["state"] = item.State,
            ["added_at"] = item.AddedAt,
            ["started_at"] = Opt(item.StartedAt),
            ["done_at"] = Opt(item.DoneAt),
            ["commits"] = Strs(item.Commits),
            ["branch"] = Opt(item.Branch),
            ["release"] = link.Release is { } r ? new JsonObject { ["tag"] = r.Tag, ["date"] = r.Date } : JsonValue.Null,
            ["release_state"] = link.ReleaseState,
            ["branches"] = Strs(link.Branches),
            ["pushed"] = link.Pushed is { } pushed ? pushed : JsonValue.Null,
            ["issue"] = item.Issue is { } number
                ? new JsonObject { ["number"] = number, ["url"] = Opt(PlanLinkage.IssueUrl(origin, number)) }
                : JsonValue.Null,
            ["note"] = Opt(item.Note),
        };
        if (item.Role is not null)
        {
            json["role"] = item.Role;
        }

        if (item.StateSince is not null)
        {
            json["state_since"] = item.StateSince;
        }

        void Set(string key, string? value)
        {
            if (value is not null)
            {
                json[key] = value;
            }
        }

        Set("outcome", item.Outcome);
        Set("size", item.Size);
        Set("wave", item.Wave);
        if (item.Depends is { } depends)
        {
            json["depends"] = Strs(depends);
        }

        if (item.Sources is { } sources)
        {
            json["sources"] = Strs(sources);
        }

        Set("ext", item.Ext);

        // only a backlog item has a wave; it also serves what the backlog verbs filter and order on
        if (item.Wave is not null)
        {
            Set("theme", item.Area);
            Set("holder", item.Holder);
            if (item.Rank is { } rank)
            {
                json["rank"] = rank;
            }
        }

        return json;
    }

    /// <summary>A backlog item as the plan view serves it, for the <c>ah backlog</c> verbs: no git linkage is resolved.</summary>
    internal static JsonObject BacklogItemJson(PlanItem item, string? origin)
    {
        var json = PlanItemJson(item, new ItemLink(item.Commits.Count == 0 ? PlanLinkage.NoCommits : GitRunner.UnavailableReason, null, [], null), origin);

        // kind and parent are added here, not in PlanItemJson, so the plan: slices stay as they are
        if (item.Kind is not null)
        {
            json["kind"] = item.Kind;
        }

        if (item.Parent is not null)
        {
            json["parent"] = item.Parent;
        }

        return json;
    }
}
