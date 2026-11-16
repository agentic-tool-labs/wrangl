using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>stream-status</c> and <c>stream-label</c>: what each stream of a team looks like now, and the one place a stream's tab label is
/// re-rendered from a hook. Neither writes the team file.
/// </summary>
internal static class StreamStatusVerbs
{
    private static readonly string[] StatusFlags = ["plain", "team", "cwd"];
    private static readonly string[] LabelFlags = ["self-pane", "self-state", "team", "cwd"];

    [Verb("roster:stream-status")]
    internal static int Status(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, StatusRun);

    [Verb("roster:stream-label")]
    internal static int Label(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, LabelRun);

    private static IRosterLaunch Launch(RosterContext c, string verb) =>
        c.Services.Launch ?? throw new RosterFailure($"{verb} is not available here: the launch layer is not composed");

    private static string TransportOf(RosterContext c, JsonValue team, IRosterLaunch launch) =>
        team.IsMap && team.Member("transport") is { IsTruthy: true } t ? MemberPlanning.JsConvertText(t) : launch.Transports.Detect();

    // ---------------------------------------------------------------- stream-status

    private static Ah.Hierarchy.TeamProgress? ProgressOf(RosterContext c, List<string> warnings)
    {
        try
        {
            return c.Services.Hierarchy.Progress.Read(c.ProgressPool, c.ProgressTeam);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            warnings.Add($"progress: the progress records could not be read: {error.Message}");
            return null;
        }
    }

    /// <summary>A stream's checklist counts and times as <c>stream-status</c> shows them; a stream with no checklist says so.</summary>
    private static JsonObject ProgressView(Ah.Hierarchy.StreamProgress? p)
    {
        if (p is not { HasChecklist: true })
        {
            return new JsonObject { ["checklist"] = false, ["journal"] = p?.HasJournal ?? false };
        }

        static JsonValue Time(DateTimeOffset? t) => t is { } at ? Ah.Core.Time.Timestamps.ToIsoString(at) : JsonValue.Null;
        return new JsonObject
        {
            ["checklist"] = true,
            ["done"] = p.Done,
            ["in_progress"] = p.InProgress,
            ["not_started"] = p.NotStarted,
            ["state"] = p.State is null ? JsonValue.Null : p.State,
            ["updated"] = Time(p.Updated),
            ["newest_entry"] = Time(p.NewestEntry),
            ["stale"] = p.Stale,
        };
    }

    /// <summary>
    /// A stream's recorded tab: its id, whether herdr lists it and, for a done stream whose tab is live, how many member panes it
    /// still holds and whether it may be closed (none).
    /// </summary>
    private static JsonObject TabView(RosterContext c, JsonValue team, JsonValue rec, string? state, StreamSupport.Snapshot snap)
    {
        var id = MemberPlanning.JsConvertText(rec.Member("tab_id"));
        var live = snap.Tabs is not null && snap.Tabs.Any(t => MemberPlanning.JsConvertText(t.Member("tab_id")) == id);
        var view = new JsonObject { ["id"] = rec.Member("tab_id") ?? JsonValue.Null, ["live"] = live };
        if (state == "done" && live)
        {
            view["member_panes"] = StreamTabs.MemberPanes(c, team, id) is { } memberPanes ? memberPanes : JsonValue.Null;
            var keep = StreamTabs.KeepReason(c, team, rec, id);
            view["closable"] = keep is null;
            if (keep is { } kept)
            {
                view["kept"] = kept.Reason;
                if (kept.Pane is not null)
                {
                    view["kept_pane"] = kept.Pane;
                }
            }
        }

        return view;
    }

    private static int StatusRun(RosterContext c)
    {
        StreamSupport.RejectUnknownFlags(c, StatusFlags, "stream-status");
        var launch = Launch(c, "stream-status");
        var rules = launch.Streams;
        var dir = c.Dir;
        var team = c.Services.Roster.ReadTeamRecord(dir, c.TeamFile);
        var transport = TransportOf(c, team, launch);
        var streams = rules.StreamsOf(team);
        var want = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        if (want is not null && !(streams.Get(want) is { IsTruthy: true }))
        {
            var message = $"stream-status: no stream named {PaneMembers.Js(JsonValue.FromString(want))} in {c.Services.Roster.TeamPath(dir, c.TeamFile)}";
            throw c.Refuse(new JsonObject { ["refused"] = "unknown-stream", ["stream"] = want, ["message"] = message }, message);
        }

        var herdr = transport == "herdr";
        var warnings = new List<string>();
        var names = streams.Keys.Where(n => want is null || n == want).ToList();
        var snap = herdr && names.Count > 0 ? StreamSupport.HerdrSnapshot(c, warnings) : new StreamSupport.Snapshot(null, null);
        var exchanges = names.Count > 0 ? StreamSupport.TeamOpenExchanges(c, dir) : new JsonArray();
        var owedTo = exchanges.Select(e => MemberPlanning.JsConvertText(e.Member("to_name"))).ToList();
        var rows = new List<JsonObject>();
        var records = ProgressOf(c, warnings);
        foreach (var n in names)
        {
            var rec = streams[n];
            var members = rules.StreamMembers(team, n);
            var statuses = rules.StreamMemberStatuses(members, snap.Agents);
            var state = MemberPlanning.Text(rec.Member("state"));
            var derived = $"{rules.StreamGlyph(state, statuses, owedTo)} {n}";
            if (herdr && state == "open")
            {
                StreamSupport.RenderStream(c, dir, team, n, snap, warnings);
            }

            var hasTab = rec.Member("tab_id") is { IsTruthy: true };
            var row = new JsonObject
            {
                ["name"] = n,
                ["state"] = rec.Member("state") ?? JsonValue.Null,
                ["label"] = herdr && hasTab ? derived : JsonValue.Null,
                ["tab"] = herdr && hasTab
                    ? TabView(c, team, rec, state, snap)
                    : JsonValue.Null,
            };
            RosterJson.Spread(row, rules.StreamFacts(rec, members, statuses, exchanges, snap.Agents));
            if (records is { Off: false })
            {
                var progress = records.Streams.FirstOrDefault(s => s.Stream == n);
                row["progress"] = ProgressView(progress);
                warnings.AddRange((progress?.Warnings ?? []).Select(w => $"{n}.md: {w}"));
            }

            rows.Add(row);
        }

        // open streams first, then oldest first within a state
        var sorted = rows
            .OrderBy(r => MemberPlanning.Text(r, "state") == "open" ? 0 : 1)
            .ThenBy(r => r.Get("opened") is { } o ? MemberPlanning.JsConvertText(o) : "undefined", StringComparer.Ordinal)
            .ToList();
        if (c.Opts.IsTrue("plain"))
        {
            foreach (var r in sorted)
            {
                var peers = r.Get("peers") is { IsArray: true } p ? p.AsArray() : new JsonArray();
                var working = peers.Count(x => MemberPlanning.Text(x.Member("status")) == "working");
                var waiting = r.Get("waiting_on") is { IsArray: true } w
                    ? string.Join(", ", w.AsArray().Select(x => MemberPlanning.JsConvertText(x.Member("on"))))
                    : string.Empty;
                var open = r.Get("exchanges") is { IsArray: true } ex ? ex.AsArray().Count : 0;
                var labelText = r.Get("label") is { IsTruthy: true } l ? MemberPlanning.JsConvertText(l) : MemberPlanning.JsConvertText(r.Get("name"));
                var branch = r.Get("branch") is { IsTruthy: true } b ? MemberPlanning.JsConvertText(b) : "-";
                c.Stdout.Write(
                    $"{labelText}  {branch}  {peers.Count} peers ({working} working)  open:{open}  waiting: {(waiting.Length == 0 ? "-" : waiting)}\n");
            }

            return 0;
        }

        var list = new JsonArray();
        foreach (var r in sorted)
        {
            list.Add(r);
        }

        var result = new JsonObject
        {
            ["team"] = c.TeamFile is null ? JsonValue.Null : c.TeamFile,
            ["transport"] = transport,
            ["streams"] = list,
        };
        if (!herdr)
        {
            result["degraded"] = "no-tabs";
        }

        if (warnings.Count > 0)
        {
            result["warnings"] = Strings(warnings);
        }

        c.Out(result);
        return 0;
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(v);
        }

        return array;
    }

    // ---------------------------------------------------------------- stream-label

    /// <summary>
    /// The stream-label hook's entry point: re-renders one open stream's tab label, with the calling member's own state standing in
    /// for what herdr reports about it.
    /// </summary>
    private static int LabelRun(RosterContext c)
    {
        StreamSupport.RejectUnknownFlags(c, LabelFlags, "stream-label");
        var name = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        var selfPane = StreamSupport.ValueFlag(c, "self-pane", "stream-label");
        var selfState = StreamSupport.ValueFlag(c, "self-state", "stream-label");
        if ((selfPane is null) != (selfState is null))
        {
            throw new RosterFailure("stream-label: --self-pane and --self-state go together");
        }

        if (selfState is not null && !StreamSupport.SelfStates.Contains(selfState))
        {
            throw new RosterFailure(
                $"stream-label: --self-state must be one of {string.Join(", ", StreamSupport.SelfStates)}, got {PaneMembers.Js(JsonValue.FromString(selfState))}");
        }

        var launch = Launch(c, "stream-label");
        var dir = c.Dir;
        var team = c.Services.Roster.ReadTeamRecord(dir, c.TeamFile);
        var rec = name is null ? default : launch.Streams.StreamsOf(team).Get(name) ?? default;

        int Skip(string reason)
        {
            c.Out(new JsonObject { ["rendered"] = false, ["reason"] = reason });
            return 0;
        }

        if (!rec.IsMap)
        {
            return Skip($"no stream named {(name is null ? "null" : PaneMembers.Js(JsonValue.FromString(name)))}");
        }

        if (MemberPlanning.Text(rec.Member("state")) == "done")
        {
            return Skip($"stream {name} is done");
        }

        var transport = TransportOf(c, team, launch);
        if (transport != "herdr")
        {
            return Skip($"transport {transport} has no tabs");
        }

        if (!(rec.Member("tab_id") is { IsTruthy: true } tabId))
        {
            return Skip($"stream {name} has no tab");
        }

        var warnings = new List<string>();
        var self = selfPane is not null ? new JsonObject { ["pane"] = selfPane, ["state"] = selfState! } : null;
        var rendered = StreamSupport.RenderStream(c, dir, team, name!, StreamSupport.HerdrSnapshot(c, warnings), warnings, self);
        var label = rendered.Label is null ? JsonValue.Null : rendered.Label;
        if (rendered.TabLive == false)
        {
            c.Out(new JsonObject
            {
                ["rendered"] = false,
                ["reason"] = $"stream {name}: its tab {MemberPlanning.JsConvertText(tabId)} is gone",
                ["label"] = label,
            });
            return 0;
        }

        var result = new JsonObject { ["rendered"] = true, ["label"] = label };
        if (rendered.Renamed && rendered.Previous is { } previous)
        {
            result["previous"] = previous;
        }

        if (warnings.Count > 0)
        {
            result["warnings"] = Strings(warnings);
        }

        c.Out(result);
        return 0;
    }
}
