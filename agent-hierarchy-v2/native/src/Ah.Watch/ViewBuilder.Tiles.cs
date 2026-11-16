using Ah.Core.Json;

namespace Ah.Watch;

internal static partial class ViewBuilder
{
    /// <summary>
    /// What an orchestrator's tile shows, served on its child of the root document so that a layer builds a tile from that document alone:
    /// the repo and pid as fields, the unacknowledged mail in the orchestrator's boxes, a glyph per member, and 60 one-minute counts of
    /// the sessions that were busy, oldest first and ending at the current minute.
    /// </summary>
    private static JsonObject TileFacts(IViewSource src, Orch orch, Func<Orch, TeamEntry, Facts> factsOf, double now)
    {
        var cluster = new JsonArray();
        var series = new int[60];
        var counted = new HashSet<string>(StringComparer.Ordinal);
        var waiting = 0;

        void Count(string? sessionId, string dir, bool live, bool busyNow, double activityAtMs)
        {
            if (sessionId is not { Length: > 0 } sid || !counted.Add(sid) || src.ActivityOf(dir, sid) is not { } history)
            {
                return;
            }

            var cells = ActivityStrip(history, live, busyNow, activityAtMs, now)["cells"].AsArray();
            for (var i = 0; i < 60 && i < cells.Count; i++)
            {
                if (cells[i].Kind == JsonKind.Text && cells[i].AsString() == "busy")
                {
                    series[i]++;
                }
            }
        }

        foreach (var team in orch.Teams)
        {
            var facts = factsOf(orch, team);
            waiting += src.BoxCounts(src.BoxDirFor(team.File, "orchestrator"), now).Unacked;
            Count(team.Doc.Obj("orchestrator")?.Str("session_id"), team.Dir, orch.Live, false, double.NaN);
            foreach (var m in facts.Members)
            {
                cluster.Add(new JsonObject { ["role"] = Orn(Get(m, "role")), ["state"] = MemberGraphState(m) });
                var live = m.TryGetValue("live", out var l) && l.Kind == JsonKind.Bool && l.AsBool();
                Count(m.Str("session_id"), team.Dir, live, m.Str("activity") == "working", ParseMs(m.Str("activity_at")));
            }
        }

        var activity = new JsonArray();
        foreach (var n in series)
        {
            activity.Add(n);
        }

        return new JsonObject
        {
            ["repo"] = TextOrNull(src, RepoName(orch.Dir), NameCap),
            ["pid"] = orch.Pid is null ? JsonValue.Null : orch.Pid.Value,
            ["activity"] = activity,
            ["mail_waiting"] = waiting,
            ["cluster"] = cluster,
        };
    }
}
