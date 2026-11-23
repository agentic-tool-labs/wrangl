using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

/// <summary>
/// The status compute (b459 C1, guard G6): every rule that turns a status source into the status document, and nothing else. It reads
/// the world only through <see cref="IStatusSource"/> and the <c>now</c> it is given, so what it observed can be recorded and replayed.
/// </summary>
internal sealed partial class HierLib
{
    private static JsonArray DescribeMembers(IStatusSource src, string dir, JsonValue team, JsonArray roster, string? teamName, double now, StatusHorizon? horizon)
    {
        var teamKey = TeamNameOf(team, teamName);
        var members = new List<JsonObject>();
        var list = Member(team, "members") is { IsArray: true } ms ? ms.AsArray() : new JsonArray();
        foreach (var m in list)
        {
            if (!(m.IsTruthy && m.Member("name") is { IsText: true } nameValue))
            {
                continue;
            }

            var route = Ah.Config.ConfigModule.IsPaneMember(m) ? "pane" : "peer";
            bool? live;
            string? sessionId = null;
            JsonValue rec;
            if (route == "pane")
            {
                rec = src.Activity(dir, $"pane-{nameValue.AsString()}.json");
                live = rec.IsNull ? null : !(Member(rec, "activity") is { IsText: true } a && a.AsString() == "unknown");
            }
            else
            {
                (live, sessionId) = src.MemberLife(roster, nameValue, teamKey);
                rec = sessionId is null ? JsonValue.Null : src.Activity(dir, sessionId + ".json");
            }

            var recActivity = Member(rec, "activity");
            var activity = !rec.IsNull && recActivity is { IsText: true } ra && Array.IndexOf(Activities, ra.AsString()) >= 0 ? ra.AsString() : "unknown";
            if (HierarchyModule.IdleOnBackgroundWorkUntil(rec, now) is { } until)
            {
                activity = "working";
                horizon?.At(until);
            }

            var transport = m.Member("transport") is { IsNull: false } tr ? tr : Member(team, "transport");
            var transportId = Member(m, "transport_id");
            var row = new JsonObject
            {
                ["name"] = CleanOrNull(nameValue, NameCap),
                ["role"] = CleanOrNull(m.Member("role"), NameCap),
                ["label"] = JsonValue.Null,
                ["kind"] = CleanTo(m.Member("kind"), NameCap) is { Length: > 0 } kind ? kind : "claude",
                ["route"] = route,
                ["session_id"] = StrOrNull(sessionId is null ? null : CleanText(sessionId, int.MaxValue)),
                ["live"] = live is null ? JsonValue.Null : live.Value,
                ["activity"] = activity,
                ["activity_at"] = rec.IsNull ? JsonValue.Null : StrOrNull(CleanTo(rec.Member("at"), int.MaxValue)),
                ["blocked_by"] = activity == "blocked" ? CleanOrNull(rec.Member("blocked_by"), NameCap) : JsonValue.Null,
                ["focusable"] = transport is { IsText: true } t && t.AsString() == "herdr" && transportId is { IsText: true } tid && tid.AsString().Length > 0
                    && Ah.Config.ConfigModule.IsHerdrName(nameValue),
                ["stream"] = m.Member("stream") is { IsText: true } st ? CleanOrNull(st, NameCap) : JsonValue.Null,
                ["last_tool"] = CleanOrNull(rec.Member("tool"), NameCap),
                ["last_tool_at"] = rec.Member("tool_at") is { IsText: true } toolAt ? StrOrNull(CleanTo(toolAt, int.MaxValue)) : JsonValue.Null,
                ["blocked_note"] = route == "pane" && activity == "blocked" ? CleanOrNull(rec.Member("note"), NoteCap) : JsonValue.Null,
            };
            if (CleanContext(rec.Member("context")) is { IsNull: false } ctx)
            {
                row["context_used"] = ctx.Member("used")!.Value;
            }

            if (WorkRootOfMember(src, m, nameValue.AsString(), teamKey) is { } workTree)
            {
                row["work_tree"] = workTree;
                row["branch"] = StrOrNull(src.Branch(workTree) is { } headBranch ? ProducerText(headBranch, NameCap) : null);
            }

            if (route == "peer" && sessionId is not null && SubagentsOf(src, [sessionId], now, horizon) is { } subagents)
            {
                row["subagents"] = subagents;
            }

            members.Add(row);
        }

        foreach (var m in members)
        {
            var shared = members.Any(o => !ReferenceEquals(o, m) && o["live"] is { IsBool: true } l && l.AsBool() && StrictEquals(o["role"], m["role"]));
            m["label"] = shared ? m["name"] : m["role"];
        }

        var result = new JsonArray();
        foreach (var m in members)
        {
            result.Add(m);
        }

        return result;
    }

    // ponytail: at most 8 rows per agent, the oldest; raise it if an agent routinely runs more
    private const int SubagentRowsCap = 8;

    /// <summary>
    /// The running subagents of <paramref name="sessions"/> as the pane lists them (pane-subagents D3): <c>[{agent_type, started_at}]</c>,
    /// oldest first, at most <see cref="SubagentRowsCap"/>, a run past the in-flight TTL dropped; null when none is kept, so the field is absent.
    /// </summary>
    private static JsonArray? SubagentsOf(IStatusSource src, IEnumerable<string> sessions, double now, StatusHorizon? horizon)
    {
        var kept = new List<(double At, JsonValue Type, string Started)>();
        foreach (var r in sessions.Distinct(StringComparer.Ordinal).SelectMany(s => src.Subagents(s)))
        {
            if (TextOf(Member(r, "started_at")) is not { } started || !Timestamps.TryParseMs(started, out var at) || now - at > HierarchyModule.SubagentInflightTtlMs)
            {
                continue;
            }

            horizon?.At(at + HierarchyModule.SubagentInflightTtlMs + 1);  // the row is dropped once its age exceeds the TTL: 1 ms past it
            kept.Add((at, Member(r, "agent_type"), started));
        }

        if (kept.Count == 0)
        {
            return null;
        }

        var rows = new JsonArray();
        foreach (var (_, type, started) in kept.OrderBy(static k => k.At).Take(SubagentRowsCap))
        {
            rows.Add(new JsonObject { ["agent_type"] = CleanOrNull(type, NameCap), ["started_at"] = StrOrNull(CleanTo((JsonValue)started, int.MaxValue)) });
        }

        return rows;
    }

    private static readonly Dictionary<string, int> Severity = new()
    {
        ["stalled"] = 0, ["blocked"] = 1, ["overdue"] = 2, ["working"] = 3, ["reported"] = 4, ["expired"] = 5,
    };

    private static JsonObject StateEntry(double atMs, string state, string? reason = null)
    {
        var o = new JsonObject { ["at"] = IsoNowOf(atMs), ["state"] = state };
        if (reason is not null)
        {
            o["reason"] = reason;
        }

        return o;
    }

    private static JsonArray DispatchStates(double nowMs, double? reportedMs, JsonObject? member, double sentMs, double tMs)
    {
        if (reportedMs is { } r)
        {
            return [StateEntry(nowMs, "reported"), StateEntry(r + ReportedWindowMs, "expired")];
        }

        if (member is not null && member["live"] is { IsBool: true } live && !live.AsBool())
        {
            return [StateEntry(nowMs, "stalled", "member-gone")];
        }

        if (member is not null && member["activity"] is { IsText: true } act && act.AsString() == "blocked")
        {
            return [StateEntry(nowMs, "blocked")];
        }

        var overdueMs = EtaClock.Compute(new EtaFacts(string.Empty, string.Empty, tMs / 1000, null, sentMs, 0, [], [], false, null), nowMs, 0).DueMs;
        return
        [
            StateEntry(sentMs, "working"),
            StateEntry(overdueMs, "overdue"),
            StateEntry(EtaClock.SilentDueAt(overdueMs, 0, sentMs, tMs, HierarchyModule.CheckinCadence[1]), "stalled", "no-report"),
        ];
    }

    private static string StateAt(JsonValue dispatch, double ms)
    {
        var states = dispatch.Prop("states").AsArray();
        var current = states[0];
        foreach (var s in states)
        {
            if (Timestamps.TryParseMs(s.Prop("at").AsString(), out var at) && at <= ms)
            {
                current = s;
            }
        }

        return current.Prop("state").AsString();
    }

    private static List<(JsonValue E, JsonValue Fm, double? ReportedMs)> DispatchCandidates(JsonArray exchanges, IStatusSource src, double nowMs, StatusHorizon? horizon)
    {
        var found = new List<(JsonValue, JsonValue, double?)>();
        // ah2-367 Amendment 5: a request sent up to the orchestrator is listed too
        foreach (var e in exchanges)
        {
            // Amendment 7: the pipeline run anchor is a run marker, not a dispatch
            if (Member(e, "slug") is { IsText: true } slug && HierarchyModule.IsPipelineAnchor(slug.AsString()))
            {
                continue;
            }

            var open = Member(e, "open").IsTruthy;
            double? reportedMs = open ? null : src.ResponseMtime(Member(Member(e, "response"), "path").AsString());
            if (!open && (reportedMs is null || reportedMs.Value + ReportedWindowMs <= nowMs))
            {
                continue;
            }

            if (reportedMs is { } reported)
            {
                horizon?.At(reported + ReportedWindowMs);  // it drops out of the list then
            }

            found.Add((e, src.RequestFm(e), reportedMs));
        }

        return found;
    }

    /// <summary>The absolute work folder of a team member: its assigned <c>work_root</c>, else the checkout its registered session runs in.</summary>
    private static string? WorkRootOfMember(IStatusSource src, JsonValue teamRow, string name, string? team)
    {
        string? checkout = null;
        if (team is not null && src.Registration(name, team)?.Cwd is { } cwd)
        {
            checkout = src.Checkout(cwd);
        }

        var root = HierarchyModule.SelectWorkRoot(teamRow.Member("work_root") is { IsText: true } assigned ? assigned.AsString() : null, checkout);
        return string.IsNullOrEmpty(root) || !NodePath.IsAbsolute(root) ? null : ProducerText(root, int.MaxValue);
    }

    /// <summary>Gives each member the status line and time of its newest answered dispatch.</summary>
    private static void NoteLastReports(JsonArray members, JsonArray dispatches)
    {
        foreach (var m in members)
        {
            JsonObject? newest = null;
            foreach (var d in dispatches)
            {
                if (d.AsObject() is { } o && StrictEquals(o["member"], m.AsObject()["name"]) && o.TryGetValue("report_line", out var line) && line.IsText && o["reported_at"] is { IsText: true } at
                    && (newest is null || string.CompareOrdinal(at.AsString(), newest["reported_at"].AsString()) > 0))
                {
                    newest = o;
                }
            }

            if (newest is not null)
            {
                m.AsObject()["last_report_line"] = newest["report_line"];  // present: only a dispatch with a report_line was chosen
                m.AsObject()["last_report_at"] = newest["reported_at"];
            }
        }
    }

    /// <summary>The fields that say what a dispatch is about: its title, its checklist item, and once answered the status line of the answer.</summary>
    private static void DescribeWork(IStatusSource src, JsonObject item, JsonValue exchange, JsonValue requestFm, bool answered)
    {
        // The request's own title is cleaned from its raw text, because the shared title reader flattens a control character to a space and would leave the rest of an escape sequence.
        if (requestFm.Member("title") is { IsText: true } written && ProducerText(written.AsString(), TitleCap) is { } shownTitle)
        {
            item["title"] = shownTitle;
            item["title_source"] = "frontmatter";
        }
        else if (ProducerText(src.Title(exchange).Title, TitleCap) is { } derivedTitle)
        {
            item["title"] = derivedTitle;
            item["title_source"] = "auto";
        }

        if (requestFm.Member("item") is { IsText: true } checklistItem && ProducerText(checklistItem.AsString(), NameCap) is { } shownItem)
        {
            item["item"] = shownItem;
        }

        if (answered && Member(Member(exchange, "response"), "path") is { IsText: true } responsePath
            && ProducerText(src.ResponseStatus(responsePath.AsString()), ReportLineCap) is { } line)
        {
            item["report_line"] = line;
        }
    }

    private static JsonArray DescribeDispatches(
        IStatusSource src,
        List<(JsonValue E, JsonValue Fm, double? ReportedMs)> candidates,
        Dictionary<string, JsonValue> origins,
        JsonValue teamKey,
        JsonArray members,
        JsonArray gates,
        double nowMs,
        StatusHorizon? horizon)
    {
        var items = new List<(JsonObject Item, double SentMs, int Severity)>();
        foreach (var (e, fm, reportedMs) in candidates)
        {
            if (!StrictEquals(Or(fm.Member("team"), JsonValue.Null), teamKey))
            {
                continue;
            }

            var id = Member(e, "id").AsString();
            JsonValue sentAt = origins.TryGetValue(id, out var o) && !o.IsNull ? o : Or(fm.Member("created"), JsonValue.Null);
            var sentMs = double.NaN;
            if (!(sentAt is { IsText: true } sa && Timestamps.TryParseMs(sa.AsString(), out sentMs)))
            {
                continue;
            }

            var eta = EtaName(fm.Member("eta"));
            var tMs = EtaThreshold(eta) * 1000;
            var toName = CleanTo(fm.Member("to_name"), NameCap);
            JsonObject? member = null;
            if (!string.IsNullOrEmpty(toName))
            {
                member = members.Select(m => m.AsObject()).FirstOrDefault(m => m["name"] is { IsText: true } n && n.AsString() == toName);
            }

            if (member is null)
            {
                var byRole = members.Select(m => m.AsObject()).Where(m => StrictEquals(m["role"], Member(e, "to"))).ToList();
                member = byRole.Count == 1 ? byRole[0] : null;
            }

            var item = new JsonObject
            {
                ["id"] = id,
                ["slug"] = StrOrNull(CleanTo(Member(e, "slug"), SlugCap)),
                ["to"] = Member(e, "to"),
                ["to_name"] = StrOrNull(toName),
                ["member"] = member is null ? JsonValue.Null : member["name"],
                ["label"] = member is not null ? member["label"] : (!string.IsNullOrEmpty(toName) ? toName : Member(e, "to")),
                ["eta"] = eta,
                ["eta_ms"] = tMs,
                ["created"] = StrOrNull(CleanTo(fm.Member("created"), int.MaxValue)),
                ["sent_at"] = StrOrNull(CleanTo(sentAt, int.MaxValue)),
                ["checkins"] = gates.Count(g => Member(g, "type") is { IsText: true } gt && gt.AsString() == "liveness-nudge" && Member(g, "request_id") is { } rid && StrictEquals(rid, Member(e, "id"))),
                ["reported_at"] = reportedMs is null ? JsonValue.Null : IsoNowOf(reportedMs.Value),
                ["states"] = DispatchStates(nowMs, reportedMs, member, sentMs, tMs),
            };
            DescribeWork(src, item, e, fm, reportedMs is not null);
            items.Add((item, sentMs, Severity[StateAt(item, nowMs)]));
            if (horizon is not null)
            {
                // the instants StateAt and BuildTimeline compare the clock with: a state begins, the order and the timeline move
                foreach (var s in item["states"].AsArray())
                {
                    if (!Timestamps.TryParseMs(s.Prop("at").AsString(), out var at))
                    {
                        horizon.At(nowMs);
                    }
                    else if (at > nowMs)
                    {
                        horizon.At(at);
                    }
                }
            }
        }

        var sorted = new JsonArray();
        foreach (var (item, _, _) in items.OrderBy(x => x.Severity).ThenBy(x => x.SentMs))
        {
            sorted.Add(item);
        }

        return sorted;
    }

    /// <summary>
    /// What <see cref="DescribePipeline"/> reads for <paramref name="teamKey"/>: the live exchanges, and when the team has a run, every
    /// exchange that shares a slug with a request of the run, since a round counts all of them. Without a run the live list is enough.
    /// </summary>
    private static JsonArray RunExchanges(IStatusSource src, string dir, JsonArray live, JsonValue teamKey)
    {
        if (OpenRunAnchorOf(src, teamKey, live).Get("id") is not { IsTruthy: true } anchorId)
        {
            return live;
        }

        var anchored = live.Where(e => Member(e, "id") is { IsText: true } i && i.AsString() == anchorId.AsString()).ToList();
        if (anchored.Count == 0 || !(src.RequestFm(anchored[0]).Member("created") is { IsText: true } started && Timestamps.TryParseMs(started.AsString(), out var startedMs)))
        {
            return live;
        }

        // a day of slack: the run's requests are filtered again, exactly, by their parsed `created`
        return src.RunExchanges(dir, teamKey.IsText ? teamKey.AsString() : null, (long)startedMs - 86_400_000);
    }

    private static JsonValue DescribePipeline(IStatusSource src, string dir, JsonArray exchanges, JsonValue teamKey)
    {
        var anchor = OpenRunAnchorOf(src, teamKey, exchanges);
        if (!(anchor.Get("id") is { IsTruthy: true } anchorId))
        {
            return JsonValue.Null;
        }

        var anchorEx = exchanges.FirstOrDefault(e => Member(e, "id") is { IsText: true } i && i.AsString() == anchorId.AsString());
        var anchorFound = exchanges.Any(e => Member(e, "id") is { IsText: true } i && i.AsString() == anchorId.AsString());
        var started = anchorFound ? Or(src.RequestFm(anchorEx).Member("created"), JsonValue.Null) : JsonValue.Null;
        var startedMs = started.IsText && Timestamps.TryParseMs(started.AsString(), out var sm) ? sm : double.NaN;
        var read = src.Decisions(dir, anchorId.AsString());
        var slugs = new List<string>();
        foreach (var e in exchanges)
        {
            var to = Member(e, "to");
            var slug = Member(e, "slug").AsString();
            if ((to is { IsText: true } tt && tt.AsString() == "orchestrator") || slug.StartsWith("dec-", StringComparison.Ordinal))
            {
                continue;
            }

            var fm = src.RequestFm(e);
            var createdOk = fm.Member("created") is { } c && Timestamps.TryParseMs(JsConvert.ToJsString(c), out var cm) && cm >= startedMs;
            if (StrictEquals(Or(fm.Member("team"), JsonValue.Null), teamKey) && createdOk && !slugs.Contains(slug))
            {
                slugs.Add(slug);
            }
        }

        double CreatedOf(JsonValue e) =>
            src.RequestFm(e).Member("created") is { } c && Timestamps.TryParseMs(JsConvert.ToJsString(c), out var ms) ? ms : double.NaN;

        var bySlug = exchanges.Where(static e => Member(e, "slug").IsText).ToLookup(static e => Member(e, "slug").AsString(), StringComparer.Ordinal);
        var items = new List<(JsonObject Item, double Newest)>();
        foreach (var slug in slugs)
        {
            var all = bySlug[slug].ToList();
            var newest = all.Aggregate((a, b) => CreatedOf(b) > CreatedOf(a) ? b : a);
            items.Add((new JsonObject
            {
                ["slug"] = StrOrNull(CleanTo(slug, SlugCap)),
                ["rounds"] = all.Count,
                ["open"] = all.Any(e => Member(e, "open").IsTruthy),
                ["to"] = Member(newest, "to"),
            }, CreatedOf(newest)));
        }

        var ordered = items.Select((x, i) => (x, i)).OrderByDescending(p => double.IsNaN(p.x.Newest) ? double.NegativeInfinity : p.x.Newest).ThenBy(p => p.i).Select(p => p.x.Item).Take(ItemCap);
        var itemArray = new JsonArray();
        foreach (var item in ordered)
        {
            itemArray.Add(item);
        }

        var decisionSummary = SummarizeDecisions(read.Get("decisions")!.Value.AsArray(), read.Get("skipped")!.Value);
        return new JsonObject
        {
            ["anchor_id"] = anchorId,
            ["started"] = StrOrNull(CleanTo(started, int.MaxValue)),
            ["round_cap"] = RoundCapValue,
            ["waiting_on_user"] = decisionSummary.Get("waiting") ?? decisionSummary["parked"],
            ["items"] = itemArray,
        };
    }

    private static JsonObject CountsAt(IReadOnlyList<JsonValue> members, IReadOnlyList<JsonValue> dispatches, double ms)
    {
        var live = members.Where(m => !(Member(m, "live") is { IsBool: true } l && !l.AsBool())).ToList();
        var states = dispatches.Select(d => StateAt(d, ms)).ToList();
        return new JsonObject
        {
            ["live"] = live.Count,
            ["out"] = states.Count(s => s is not ("reported" or "expired")),
            ["blocked"] = live.Count(m => Member(m, "activity") is { IsText: true } a && a.AsString() == "blocked"),
            ["overdue"] = states.Count(s => s == "overdue"),
            ["stalled"] = states.Count(s => s == "stalled"),
        };
    }

    private static JsonObject TimelineEntry(string at, JsonObject c, bool enabled, bool anyPipeline)
    {
        var extra = new[] { ("blocked", "b"), ("overdue", "o"), ("stalled", "s") }.Where(p => c[p.Item1].AsNumber() > 0).ToList();
        var tone = c["overdue"].AsNumber() + c["stalled"].AsNumber() > 0 ? "bad" : c["blocked"].AsNumber() > 0 ? "warn" : c["out"].AsNumber() > 0 ? "work" : "idle";
        var entry = new JsonObject
        {
            ["at"] = at,
            ["visible"] = enabled && (c["live"].AsNumber() > 0 || c["out"].AsNumber() > 0 || anyPipeline),
            ["tone"] = tone,
        };
        foreach (var k in c.Keys)
        {
            entry[k] = c[k];
        }

        var live = JsConvert.ToJsString(c["live"]);
        var outCount = JsConvert.ToJsString(c["out"]);
        entry["text"] = string.Join(" · ", new[] { $"{live} live · {outCount} out" }.Concat(extra.Select(p => $"{JsConvert.ToJsString(c[p.Item1])} {p.Item1}")));
        entry["short"] = string.Join(' ', new[] { $"{live}/{outCount}" }.Concat(extra.Select(p => $"{JsConvert.ToJsString(c[p.Item1])}{p.Item2}")));
        return entry;
    }

    private static JsonArray BuildTimeline(double nowMs, IReadOnlyList<JsonValue> members, IReadOnlyList<JsonValue> dispatches, bool enabled, bool anyPipeline)
    {
        var instants = dispatches
            .SelectMany(d => d.Prop("states").AsArray().Select(s => Timestamps.TryParseMs(s.Prop("at").AsString(), out var ms) ? ms : double.NaN))
            .Where(ms => ms > nowMs)
            .Distinct()
            .Order()
            .ToList();
        var timeline = new JsonArray();
        string? last = null;
        foreach (var ms in new[] { nowMs }.Concat(instants))
        {
            var counts = CountsAt(members, dispatches, ms);
            var key = JsonText.Stringify(counts, 0);
            if (key == last)
            {
                continue;
            }

            last = key;
            timeline.Add(TimelineEntry(IsoNowOf(ms), counts, enabled, anyPipeline));
        }

        return timeline;
    }

    /// <summary>The status of <paramref name="dir"/> from the inputs <paramref name="src"/> gives: the one implementation of every status rule.</summary>
    /// <remarks>
    /// The caller resolves <paramref name="dir"/> and <paramref name="now"/>. <paramref name="horizon"/>, when given, is told every later
    /// instant at which the document's content changes with the clock alone (b459 C1): a new comparison with <paramref name="now"/> must tell it.
    /// </remarks>
    internal static JsonObject ComputeStatusFrom(IStatusSource src, string? cwd, double now, string? dir, bool? enabledOverride = null, StatusHorizon? horizon = null)
    {
        var enabled = enabledOverride ?? src.Enabled(cwd);
        var teams = new JsonArray();
        var allMembers = new List<JsonValue>();
        var allDispatches = new List<JsonValue>();
        var owners = new List<(JsonValue Pid, List<string> Sessions, JsonArray Teams, List<JsonValue> Members, List<JsonValue> Dispatches, bool AnyPipeline)>();
        var kept = new List<(string? Key, JsonValue Record, int Owner, JsonArray Members, JsonArray Dispatches)>();
        var deputies = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(dir) && src.HasDir(dir))
        {
            var rosterStart = Ah.Core.Time.PhaseTrace.Begin();
            var roster = src.Roster(dir);
            Ah.Core.Time.PhaseTrace.End("status.attributed-roster", rosterStart);
            var sessionRows = src.SessionRows(dir);
            var exchangesStart = Ah.Core.Time.PhaseTrace.Begin();
            var exchanges = src.Exchanges(dir);
            Ah.Core.Time.PhaseTrace.End("status.list-exchanges", exchangesStart);
            var gatesStart = Ah.Core.Time.PhaseTrace.Begin();
            var gates = src.Gates(dir);
            Ah.Core.Time.PhaseTrace.End("status.read-gates", gatesStart);
            List<(JsonValue E, JsonValue Fm, double? ReportedMs)>? candidates = null;
            Dictionary<string, JsonValue>? origins = null;
            JsonArray? resources = null;
            var keys = new List<string?> { null };
            keys.AddRange(src.TeamNames(dir));
            foreach (var key in keys)
            {
                var t = src.TeamRecord(dir, key);
                // A team drops out of the status only when its Orchestrator is proven gone; age and a pid nothing proves never drop it.
                if (!t.IsTruthy || src.Orphaned(t))
                {
                    continue;
                }

                var keyValue = key is null ? JsonValue.Null : (JsonValue)key;
                if (candidates is null)
                {
                    var candidatesStart = Ah.Core.Time.PhaseTrace.Begin();
                    candidates = DispatchCandidates(exchanges, src, now, horizon);
                    Ah.Core.Time.PhaseTrace.End("status.dispatch-candidates", candidatesStart);
                    var requests = new JsonArray();
                    foreach (var (e, fm, _) in candidates)
                    {
                        requests.Add(new JsonObject { ["id"] = Member(e, "id"), ["created"] = Or(fm.Member("created"), JsonValue.Null) });
                    }

                    var originStart = Ah.Core.Time.PhaseTrace.Begin();
                    origins = src.Origins(requests);
                    Ah.Core.Time.PhaseTrace.End("status.dispatch-origin", originStart);
                }

                var membersStart = Ah.Core.Time.PhaseTrace.Begin();
                var members = DescribeMembers(src, dir, t, roster, key, now, horizon);
                Ah.Core.Time.PhaseTrace.End("status.describe-members", membersStart);
                var dispatchesStart = Ah.Core.Time.PhaseTrace.Begin();
                var dispatches = DescribeDispatches(src, candidates, origins!, keyValue, members, gates, now, horizon);
                Ah.Core.Time.PhaseTrace.End("status.describe-dispatches", dispatchesStart);
                NoteLastReports(members, dispatches);
                allMembers.AddRange(members);
                allDispatches.AddRange(dispatches);
                var capped = new JsonArray();
                foreach (var d in dispatches.Take(DispatchCap))
                {
                    capped.Add(d);
                }

                var pipelineStart = Ah.Core.Time.PhaseTrace.Begin();
                var team = new JsonObject
                {
                    ["team"] = keyValue,
                    ["members"] = members,
                    ["dispatches"] = capped,
                    ["dispatches_truncated"] = Math.Max(0, dispatches.Count - DispatchCap),
                    ["pipeline"] = DescribePipeline(src, dir, RunExchanges(src, dir, exchanges, keyValue), keyValue),
                };

                // ah2-509: absent when the team holds and waits for nothing, so a document with no resource keeps its recorded shape
                if (TeamResources(resources ??= src.Resources(), members, key) is { Count: > 0 } held)
                {
                    team["resources"] = held;
                }

                // ah2-512: absent when the team has counted nothing and has no cap
                if (src.Spend(dir, key) is { Count: > 0 } spend)
                {
                    team["spend"] = spend;
                }

                Ah.Core.Time.PhaseTrace.End("status.describe-pipeline", pipelineStart);
                teams.Add(team);
                var pid = Member(Member(t, "orchestrator"), "pid");
                var idx = owners.FindIndex(o => StrictEquals(o.Pid, pid));
                if (idx < 0)
                {
                    var rowSessions = new List<string>();
                    for (var i = sessionRows.Count - 1; i >= 0; i--)
                    {
                        var r = sessionRows[i];
                        if (r.Member("pid") is { } rp && StrictEquals(rp, pid) && r.Member("session_id") is { IsText: true } rs && rs.AsString().Length > 0 && !rowSessions.Contains(rs.AsString()))
                        {
                            rowSessions.Add(rs.AsString());
                        }
                    }

                    owners.Add((pid, rowSessions, new JsonArray(), [], [], false));
                    idx = owners.Count - 1;
                }

                var g = owners[idx];
                if (Member(Member(t, "orchestrator"), "session_id") is { IsText: true } sid && sid.AsString().Length > 0 && !g.Sessions.Contains(sid.AsString()))
                {
                    g.Sessions.Add(sid.AsString());
                }

                // ah2-367 L7: the deputy on duty is acting lead, so its session is an owner session of the team
                if (Ah.Roster.RosterModule.DutyOf(t) is { } duty
                    && members.FirstOrDefault(m => TextOf(Member(m, "name")) == duty["deputy"].AsString()) is { } deputy
                    && TextOf(Member(deputy, "session_id")) is { Length: > 0 } deputySession && !g.Sessions.Contains(deputySession))
                {
                    g.Sessions.Add(deputySession);
                    deputies.Add(deputySession);
                }

                g.Teams.Add(keyValue);
                g.Members.AddRange(members);
                g.Dispatches.AddRange(dispatches);
                if (!team["pipeline"].IsNull)
                {
                    g = g with { AnyPipeline = true };
                }

                owners[idx] = g;
                kept.Add((key, t, idx, members, dispatches));
            }

            // ah2-367 L3: an open lane is listed with its parent team's owner too, so the parent's Orchestrator sees its dispatches.
            // One level: a lane of a lane is listed with its own parent only.
            foreach (var (key, t, _, members, dispatches) in kept)
            {
                var parent = Member(t, "parent");
                var parentTeam = TextOf(Member(parent, "team"));
                var stream = TextOf(Member(parent, "stream"));
                var host = kept.FindIndex(k => k.Key is not null && k.Key == parentTeam);
                if (key is null || stream is null || host < 0 || TextOf(Member(Member(Member(kept[host].Record, "streams"), stream), "state")) != "open")
                {
                    continue;
                }

                var g = owners[kept[host].Owner];
                if (!g.Teams.Any(x => x.IsText && x.AsString() == key))
                {
                    g.Teams.Add(key);
                    g.Members.AddRange(members);
                    g.Dispatches.AddRange(dispatches);
                }
            }
        }

        var memberSessions = new JsonArray();
        var seenSessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in allMembers)
        {
            if (!Ah.Config.ConfigModule.IsPaneMember(m) && Member(m, "session_id") is { IsText: true } s && s.AsString().Length > 0 && seenSessions.Add(s.AsString()))
            {
                memberSessions.Add(s);
            }
        }

        var anyPipeline = teams.Any(t => !t.Prop("pipeline").IsNull);
        var ownerArray = new JsonArray();
        foreach (var g in owners)
        {
            var owner = new JsonObject
            {
                ["sessions"] = ToArray(g.Sessions.Take(OwnerSessionsCap)),
                ["teams"] = g.Teams,
                ["timeline"] = BuildTimeline(now, g.Members, g.Dispatches, enabled, g.AnyPipeline),
            };

            // pane-subagents Amendment 1 (Q1): the lead's own running subagents; a deputy's are on its member row already
            if (SubagentsOf(src, g.Sessions.Where(s => !deputies.Contains(s)), now, horizon) is { } subagents)
            {
                owner["subagents"] = subagents;
            }

            ownerArray.Add(owner);
        }

        var document = new JsonObject
        {
            ["schema"] = StatusSchemaVersion,
            ["written_at"] = IsoNowOf(now),
            ["expires_at"] = IsoNowOf(now + ExpiresAfterMs),
            ["enabled"] = enabled,
            ["member_sessions"] = memberSessions,
            ["teams"] = teams,
            ["timeline"] = BuildTimeline(now, allMembers, allDispatches, enabled, anyPipeline),
            ["owners"] = ownerArray,
        };
        if (src.Backlog(dir!, now, horizon is null ? null : horizon.At) is { } backlog)
        {
            document["backlog"] = HierarchyModule.BacklogHealthJson(backlog, full: false);
        }

        return document;
    }
}
