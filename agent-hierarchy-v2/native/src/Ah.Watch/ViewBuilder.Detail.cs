using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>The eight groups the orchestrator detail adds, and the member badge they share.</summary>
internal static partial class ViewBuilder
{
    private static readonly string[] BadgeTokens = ["working", "idle", "blocked", "failed"];

    private static readonly IReadOnlySet<string> PeerTypes = new HashSet<string>(StringComparer.Ordinal) { "watch-event", "dispatch", "outage", "resume", "overdue-resume" };

    private static readonly IReadOnlySet<string> OutageType = new HashSet<string>(StringComparer.Ordinal) { "outage" };

    private const int RosterCap = 12;
    private const int RequestsCap = 8;
    private const int FeedCap = 50;
    private const int DecisionsCap = 20;
    private const int TreesCap = GitRunner.MaxTrees;
    private const int FeedTextCap = 300;
    private const double ResumedWindowMs = 60 * 60_000;

    /// <summary>The state badge of a member: <c>held</c> first, else its recorded activity, else <c>unknown</c>.</summary>
    internal static string BadgeOf(JsonValue activity, bool held) =>
        held ? "held" : activity.IsText && Array.IndexOf(BadgeTokens, activity.AsString()) >= 0 ? activity.AsString() : "unknown";

    /// <summary>The sessions the latest outage row holds, or none when that row is over or there is no row.</summary>
    private static HashSet<string> HeldSessionsOf(IViewSource src)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var latest = src.PeerRows(OutageType).OrderBy(static r => r.Str("ts") ?? string.Empty, StringComparer.Ordinal).LastOrDefault();
            if (latest is not null && (latest.Str("status") ?? "open") == "open" && latest.Get("held") is { IsArray: true } list)
            {
                foreach (var h in list.AsArray())
                {
                    if (h.IsMap && h.AsObject().Str("session_id") is { Length: > 0 } id)
                    {
                        _ = held.Add(id);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
        {
            // an unreadable peer record file holds nobody
        }

        return held;
    }

    private static List<DetailField> OrchestratorGroups(ModelCtx c, Node node, string? session) => new Detail(c, node.Orch!, session).Fields();

    /// <summary>One build of the eight groups of an orchestrator's detail.</summary>
    private sealed class Detail
    {
        private readonly ModelCtx _c;
        private readonly Orch _orch;
        private readonly string? _session;
        private readonly IDetailReads? _reads;
        private readonly Lazy<IReadOnlyList<JsonObject>?> _peers;
        private readonly Lazy<IReadOnlyList<DispatchCandidate>> _dispatches;
        private readonly Lazy<Dictionary<string, (string Name, string? Role)>> _members;
        private readonly Lazy<(IReadOnlyList<(string Tree, string Path, GitReading<GitTreeState> Reading, string State)> Rows, int More)?> _trees;

        internal Detail(ModelCtx c, Orch orch, string? session)
        {
            _c = c;
            _orch = orch;
            _session = session;
            _reads = c.Src as IDetailReads;
            _peers = new Lazy<IReadOnlyList<JsonObject>?>(ReadPeers);
            _dispatches = new Lazy<IReadOnlyList<DispatchCandidate>>(() => _session is null ? [] : _c.Src.OpenDispatches(_session, Now));
            _members = new Lazy<Dictionary<string, (string, string?)>>(ReadMembers);
            _trees = new Lazy<(IReadOnlyList<(string, string, GitReading<GitTreeState>, string)>, int)?>(ReadTrees);
        }

        private double Now => _c.Now;

        internal List<DetailField> Fields() => [Vitals(), Roster(), Flight(), Feed(), Activity(), Pipeline(), Decisions(), Worktrees()];

        private static JsonValue S(string? text) => text is null ? JsonValue.Null : text;

        private static string Basename(string path) => Path.GetFileName(path.TrimEnd('/'));

        private static JsonValue Tenth(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);


        private static DetailField Group(string key, string label, JsonValue value, string source, bool verified) => new(key, label, value, source, verified, null);

        private string? Clean(string? text, int cap = 400) => text is null ? null : _c.Src.Clean(text, cap) is { Length: > 0 } cleaned ? cleaned : null;

        private IReadOnlyList<JsonObject>? ReadPeers()
        {
            try
            {
                return _c.Src.PeerRows(PeerTypes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
            {
                return null;
            }
        }

        /// <summary>Every member session of the orchestrator's teams, with its member name and role.</summary>
        private Dictionary<string, (string Name, string? Role)> ReadMembers()
        {
            var map = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
            foreach (var team in _orch.Teams)
            {
                foreach (var m in _c.FactsOf(_orch, team).Members)
                {
                    if (m.Str("session_id") is { Length: > 0 } id && m.Str("name") is { } name)
                    {
                        map[id] = (name, m.Str("role"));
                    }
                }
            }

            return map;
        }

        private HashSet<string> OrchestratorSessions()
        {
            var sessions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var team in _orch.Teams)
            {
                if (team.Doc.Obj("orchestrator")?.Str("session_id") is { Length: > 0 } id)
                {
                    _ = sessions.Add(id);
                }
            }

            return sessions;
        }

        private string? NameOf(string? session) => session is not null && _members.Value.TryGetValue(session, out var m) ? m.Name : null;

        // ------------------------------------------------------------------ C1

        private DetailField Vitals()
        {
            var tap = _session is null || _reads?.Tap is not { } reader ? TapReading.Off(TapReading.TapOff) : reader.Read(_session);
            var registration = _session is null ? (Pid: (long?)null, CheckoutRoot: (string?)null, Cwd: (string?)null) : _c.Registrations.Value(_session);
            var subs = new List<DetailField>();
            var tapGap = tap.OffReason ?? TapReading.NotInData;

            subs.Add(tap.Model is { } tapModel
                ? Present("model", "model", new JsonObject { ["id"] = S(Clean(tapModel.Id, NameCap)), ["display"] = S(Clean(tapModel.Display, NameCap)), ["short"] = ModelShort.Of(tapModel.Id).Short }, "statusline")
                : Absent("model", "model", "statusline", tapGap));
            subs.Add(tap.Effort is { } tapEffort
                ? Present("effort", "effort", new JsonObject { ["level"] = S(Clean(tapEffort, NameCap)) }, "statusline")
                : Absent("effort", "effort", "statusline", tapGap));

            if (tap.Cwd is { } tapCwd)
            {
                subs.Add(Present("cwd", "cwd", S(Clean(tapCwd)), "statusline"));
            }
            else if (registration.Cwd is { } cwd)
            {
                subs.Add(Present("cwd", "cwd", S(Clean(cwd)), "registration"));
            }
            else
            {
                subs.Add(Absent("cwd", "cwd", "registration", "not registered"));
            }

            subs.Add(BranchField(registration.CheckoutRoot));
            subs.Add(ContextField(tap));
            subs.Add(RateLimitsField(tap));
            return Group("vitals", "Session vitals", Fields(subs), "composite", subs.All(static f => f.Verified));
        }

        private DetailField BranchField(string? checkoutRoot)
        {
            const string source = "git HEAD file";
            if (checkoutRoot is null || _reads is null)
            {
                return Absent("branch", "branch", source, BranchReading.NoGit);
            }

            var head = GitHead.Read(checkoutRoot, _reads.ReadText);
            return head.Value is null
                ? Absent("branch", "branch", source, head.Reason ?? BranchReading.NoGit)
                : Present(
                    "branch",
                    "branch",
                    head.Reason == BranchReading.Detached
                        ? new JsonObject { ["name"] = JsonValue.Null, ["detached"] = true, ["sha"] = head.Value }
                        : new JsonObject { ["name"] = S(Clean(head.Value, NameCap)), ["detached"] = false, ["sha"] = JsonValue.Null },
                    source);
        }

        private DetailField ContextField(TapReading tap)
        {
            if (tap.Context is { } fromTap)
            {
                return Present(
                    "context",
                    "context",
                    new JsonObject
                    {
                        ["used"] = fromTap.Used is { } used ? (double)used : JsonValue.Null,
                        ["window"] = (double)(long)fromTap.Window,
                        ["pct"] = Tenth(fromTap.Pct),
                        ["as_of"] = S(tap.AsOf),
                        ["stale"] = tap.Stale,
                    },
                    "statusline");
            }

            const string inferred = "activity record; window from its model";
            var history = _session is null ? null : _c.Src.ActivityOf(_orch.Dir, _session);
            if (history is null)
            {
                return Absent("context", "context", inferred, "no activity record");
            }

            if (history.ContextUsed is not { } fill)
            {
                return Absent("context", "context", inferred, "no context reading");
            }

            if (ContextReading.WindowFor(history.ContextModel, fill) is not { } window)
            {
                return Absent("context", "context", inferred, "window unknown");
            }

            return Present(
                "context",
                "context",
                new JsonObject { ["used"] = (double)fill, ["window"] = (double)window, ["pct"] = (double)ContextReading.Of(history.ContextModel, fill).Pct!.Value, ["as_of"] = S(history.ContextAt), ["stale"] = false },
                inferred,
                verified: false);
        }

        private static DetailField RateLimitsField(TapReading tap)
        {
            const string source = "statusline";
            if (tap.OffReason is { } off)
            {
                return Absent("rate_limits", "rate limits", source, off);
            }

            if (tap.RateLimits is not { } limits)
            {
                return Absent("rate_limits", "rate limits", source, TapReading.NotInData);
            }

            var windows = new JsonArray();
            foreach (var w in limits)
            {
                windows.Add(new JsonObject { ["name"] = w.Name, ["pct"] = Tenth(w.Pct), ["resets_at"] = S(w.ResetsAt) });
            }

            return Present("rate_limits", "rate limits", new JsonObject { ["windows"] = windows, ["as_of"] = S(tap.AsOf), ["stale"] = tap.Stale }, source);
        }

        private static JsonArray Fields(IEnumerable<DetailField> fields)
        {
            var list = new JsonArray();
            foreach (var f in fields)
            {
                list.Add(FieldJson(f));
            }

            return list;
        }

        // ------------------------------------------------------------------ C2

        private DetailField Roster()
        {
            var held = _c.Held.Value;
            var picked = new List<(TeamEntry Team, Facts Facts, JsonObject Member)>();
            var total = 0;
            foreach (var team in _orch.Teams)
            {
                var facts = _c.FactsOf(_orch, team);
                foreach (var m in facts.Members)
                {
                    total++;
                    if (picked.Count < RosterCap)
                    {
                        picked.Add((team, facts, m));
                    }
                }
            }

            var bodies = picked.Select(p => MemberView(_c.Src, _orch, p.Team, p.Facts, p.Member, new JsonArray(), Now, _c.MPath, _c.Registrations).Body.Obj("body")!).ToList();
            var models = bodies.Select(static b => b.Str("model")).ToList();
            var shorts = ModelShort.Document([.. models.Where(static m => m is not null).Select(static m => m!)]);
            var next = 0;
            var members = new JsonArray();
            for (var i = 0; i < picked.Count; i++)
            {
                var (team, facts, member) = picked[i];
                var body = bodies[i];
                var name = member.Str("name")!;
                var session = member.Str("session_id");
                var brief = facts.Open.Where(d => d.Str("member") == name).OrderBy(static d => d.Str("sent_at") ?? string.Empty, StringComparer.Ordinal).LastOrDefault();
                var history = session is { Length: > 0 } ? _c.Src.ActivityOf(team.Dir, session) : null;
                var busyNow = body["activity"].IsText && body["activity"].AsString() == "working";
                var strip = ActivityStrip(
                    history ?? new ActivityHistory(null, []),
                    body["live"].AsBool(),
                    busyNow,
                    ParseMs(body["activity_at"].IsText ? body["activity_at"].AsString() : null),
                    Now);
                members.Add(new JsonObject
                {
                    ["name"] = name,
                    ["path"] = _c.MPath(_orch, team, name),
                    ["team"] = _c.TeamLabel(team),
                    ["role"] = body["role"],
                    ["model_short"] = models[i] is null ? JsonValue.Null : shorts[next++],
                    ["effort"] = body["effort"],
                    ["badge"] = BadgeOf(body["activity"], session is not null && held.Contains(session)),
                    ["brief"] = brief is null ? JsonValue.Null : new JsonObject { ["id"] = Get(brief, "id"), ["slug"] = Get(brief, "slug") },
                    ["last_activity_at"] = body["activity_at"].IsText ? body["activity_at"] : JsonValue.Null,
                    ["activity"] = strip,
                });
            }

            return Group("roster", "Team roster", new JsonObject { ["members"] = members, ["more"] = Math.Max(0, total - picked.Count) }, "member facts", true);
        }

        // ------------------------------------------------------------------ C3

        private DetailField Flight()
        {
            const string source = "dispatch clock, mailbox, peer records";
            var unavailable = _reads?.Unavailable ?? [];
            var requests = new JsonArray();
            var more = 0;
            if (_session is not null)
            {
                var open = _dispatches.Value.Where(static x => x.Open).OrderByDescending(static x => x.Created ?? string.Empty, StringComparer.Ordinal).ToList();
                foreach (var x in open.Take(RequestsCap))
                {
                    requests.Add(new JsonObject
                    {
                        ["id"] = x.RequestId,
                        ["to"] = DispatchClock.NameOf(x.ToName, x.ToAddr),
                        ["slug"] = x.Slug.Length > 0 ? x.Slug : JsonValue.Null,
                        ["sent_at"] = S(x.Created),
                        ["eta"] = x.Eta,
                        ["level"] = LevelFor(x.RequestId),
                        ["pool"] = unavailable.Contains("pool") ? JsonValue.Null : PoolOf(x.HierarchyDir),
                    });
                }

                more = Math.Max(0, open.Count - RequestsCap);
            }

            var held = new JsonArray();
            var resumed = new JsonArray();
            JsonValue outage = JsonValue.Null;
            if (_peers.Value is { } rows)
            {
                var latest = LatestOutage(rows);
                if (latest is not null && !unavailable.Contains("outage"))
                {
                    outage = new JsonObject
                    {
                        ["status"] = latest.Str("status") ?? "open",
                        ["started_at"] = S(latest.Str("started_at")),
                        ["ended_at"] = S(latest.Str("ended_at")),
                        ["reason"] = S(Clean(latest.Str("reason"))),
                        ["probe"] = S(NameOf(latest.Str("probe_session"))),
                        ["probes"] = latest.Int("probes") is { } probes ? (double)probes : 0,
                    };
                }

                if (latest is not null && (latest.Str("status") ?? "open") == "open" && latest.Get("held") is { IsArray: true } list && !unavailable.Contains("held"))
                {
                    foreach (var h in list.AsArray().Where(static h => h.IsMap).Select(static h => h.AsObject()))
                    {
                        var id = h.Str("session_id");
                        held.Add(new JsonObject { ["session_id"] = S(id), ["name"] = S(NameOf(id)), ["role"] = S(h.Str("role")), ["failed_at"] = S(h.Str("failed_at")) });
                    }
                }

                if (!unavailable.Contains("resumed"))
                {
                    AddResumed(resumed, rows);
                }
            }

            var flight = new JsonObject
            {
                ["requests"] = requests,
                ["requests_more"] = more,
                ["mail"] = MailBoxes(),
                ["held"] = held,
                ["resumed"] = resumed,
                ["outage"] = outage,
                ["unavailable"] = Strings(unavailable),
            };
            return Group("flight", "Work in flight", flight, source, true);
        }

        /// <summary>What the user was told about a request: <c>silent</c> once a SILENT event was emitted for it, else <c>checkin</c> once a CHECK-IN was, else <c>ok</c>.</summary>
        private string LevelFor(string requestId)
        {
            var kinds = (_peers.Value ?? [])
                .Where(r => r.Str("type") == "watch-event" && r.Str("session_id") == _session && r.Str("request_id") == requestId)
                .Select(static r => r.Str("kind"))
                .ToList();
            return kinds.Contains("SILENT") ? "silent" : kinds.Contains("CHECK-IN") ? "checkin" : "ok";
        }

        private static JsonArray Strings(IEnumerable<string> items)
        {
            var list = new JsonArray();
            foreach (var item in items)
            {
                list.Add(item);
            }

            return list;
        }

        private string PoolOf(string hierarchyDir) =>
            hierarchyDir == _orch.Dir ? "main" : Basename(Path.GetDirectoryName(Path.GetDirectoryName(hierarchyDir.TrimEnd('/'))) ?? hierarchyDir);

        private JsonArray MailBoxes()
        {
            var counted = new List<(string Box, int Unacked, int Overdue)>();
            foreach (var team in _orch.Teams)
            {
                var facts = _c.FactsOf(_orch, team);
                try
                {
                    var (u, o) = _c.Src.BoxCounts(_c.Src.BoxDirFor(team.File, "orchestrator"), Now);
                    counted.Add((AddressOf(team, "orchestrator"), u, o));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
                {
                    // an unreadable box owes nothing the view can count
                }

                foreach (var m in facts.Members)
                {
                    if (m.Str("name") is { } name && facts.Boxes.TryGetValue(name, out var box))
                    {
                        counted.Add((AddressOf(team, name), box.Unacked, box.Overdue));
                    }
                }
            }

            var mail = new JsonArray();
            foreach (var (box, unacked, overdue) in counted.Where(static b => b.Unacked > 0).OrderByDescending(static b => b.Unacked).ThenBy(static b => b.Box, StringComparer.Ordinal))
            {
                mail.Add(new JsonObject { ["box"] = box, ["unacked"] = unacked, ["overdue"] = overdue });
            }

            return mail;
        }

        /// <summary>The newest outage row that names a session of this orchestrator's teams.</summary>
        private JsonObject? LatestOutage(IReadOnlyList<JsonObject> rows) =>
            OutageRows(rows).OrderBy(static r => r.Str("ts") ?? string.Empty, StringComparer.Ordinal).LastOrDefault();

        private IEnumerable<JsonObject> OutageRows(IReadOnlyList<JsonObject> rows)
        {
            var mine = new HashSet<string>(_members.Value.Keys, StringComparer.Ordinal);
            mine.UnionWith(OrchestratorSessions());
            return rows.Where(r => r.Str("type") == "outage" && NamesOneOf(r, mine));
        }

        private static bool NamesOneOf(JsonObject outage, HashSet<string> sessions)
        {
            if (outage.Str("probe_session") is { } probe && sessions.Contains(probe))
            {
                return true;
            }

            return outage.Get("held") is { IsArray: true } held && held.AsArray().Any(h => h.IsMap && h.AsObject().Str("session_id") is { } id && sessions.Contains(id));
        }

        private void AddResumed(JsonArray resumed, IReadOnlyList<JsonObject> rows)
        {
            var orchestrators = OrchestratorSessions();
            var requests = _dispatches.Value.ToDictionary(static x => x.RequestId, StringComparer.Ordinal);
            var entries = new List<(double Ms, JsonObject Item)>();
            foreach (var r in rows)
            {
                var type = r.Str("type");
                if (type is not ("resume" or "overdue-resume") || r.Str("ts") is not { } ts || !Timestamps.TryParseMs(ts, out var ms) || Now - ms > ResumedWindowMs)
                {
                    continue;
                }

                string? subject;
                string? name;
                if (type == "resume")
                {
                    subject = r.Str("subject");
                    name = NameOf(subject);
                    if (subject is null || name is null)
                    {
                        continue;
                    }
                }
                else
                {
                    if (r.Str("session_id") is not { } owner || !orchestrators.Contains(owner) || r.Str("request_id") is not { } request || !requests.TryGetValue(request, out var candidate))
                    {
                        continue;
                    }

                    name = DispatchClock.NameOf(candidate.ToName, candidate.ToAddr);
                    subject = _members.Value.FirstOrDefault(m => m.Value.Name == name).Key;
                    if (subject is null)
                    {
                        continue;
                    }
                }

                entries.Add((ms, new JsonObject { ["session_id"] = subject, ["name"] = name, ["kind"] = type!, ["n"] = r.Int("n") is { } n ? (double)n : 0, ["ts"] = ts }));
            }

            foreach (var (_, item) in entries.OrderByDescending(static e => e.Ms))
            {
                resumed.Add(item);
            }
        }

        // ------------------------------------------------------------------ C4

        private static readonly Dictionary<string, string> WatchKinds = new(StringComparer.Ordinal)
        {
            ["CHECK-IN"] = "check-in",
            ["SILENT"] = "silent",
            ["RESUME"] = "resume",
            ["API-FAILED"] = "api-failed",
            ["CLASSIFIER-DENIED"] = "step-needs-user",
            ["LANDED"] = "report",
            ["BLOCKED"] = "blocked",
            ["QUEUE-STALL"] = "queue-stall",
            ["MEMBER-WAITING"] = "member-waiting",
            ["UNLINKED-DISPATCH"] = "unlinked dispatch",
            ["JOB-OVERDUE"] = "job-overdue",
            ["LANE-ESCALATED"] = "lane-escalated",
            ["LANE-RECOVERED"] = "lane-recovered",
            ["member-resume"] = "member-resume",
        };

        private DetailField Feed()
        {
            const string source = "peer records";
            if (_peers.Value is not { } rows)
            {
                return Absent("feed", "Events", source, "peer records unreadable");
            }

            var requests = _dispatches.Value.ToDictionary(static x => x.RequestId, StringComparer.Ordinal);
            var items = new List<(double Ms, JsonObject Item)>();
            void Add(string ts, string kind, string? subject, string text, string? requestId)
            {
                _ = Timestamps.TryParseMs(ts, out var ms);
                items.Add((ms, new JsonObject { ["ts"] = ts, ["kind"] = kind, ["subject"] = S(subject), ["text"] = Capped(text), ["request_id"] = S(requestId) }));
            }

            foreach (var r in rows)
            {
                var type = r.Str("type");
                if (type == "watch-event" && r.Str("session_id") == _session && _session is not null && r.Str("ts") is { } ts && r.Str("kind") is { } kind)
                {
                    var mapped = kind.StartsWith("CONTEXT-", StringComparison.Ordinal) ? "context"
                        : kind == "CLASSIFIER-DENIED" && r.Str("verdict") == "no-verdict" ? "step-unfinished"
                        : WatchKinds.GetValueOrDefault(kind);
                    if (mapped is null)
                    {
                        continue;
                    }

                    var requestId = r.Str("request_id");
                    var subject = r.Str("member") is { } member ? NameFromAddress(member) : r.Str("to") ?? (requestId is not null && requests.TryGetValue(requestId, out var c) ? DispatchClock.NameOf(c.ToName, c.ToAddr) : null);
                    Add(ts, mapped, subject, r.Str("text") ?? string.Empty, requestId);
                }
                else if (type == "dispatch" && r.Str("session_id") == _session && _session is not null && r.Str("created") is { } created)
                {
                    var requestId = r.Str("request_id");
                    var subject = r.Str("to_addr") is { } addr ? NameFromAddress(addr) : r.Str("to");
                    var path = r.Str("path");
                    Add(created, "dispatch", subject, (path is null ? null : SlugOf(path) is { IsText: true } slug ? slug.AsString() : null) ?? requestId ?? string.Empty, requestId);
                }
            }

            foreach (var latest in OutageRows(rows).GroupBy(static r => r.Str("started_at") ?? string.Empty, StringComparer.Ordinal).Select(static g => g.OrderBy(static r => r.Str("ts") ?? string.Empty, StringComparer.Ordinal).Last()))
            {
                AddOutage(latest, Add);
            }

            var sorted = items.OrderByDescending(static i => i.Ms).ToList();
            var list = new JsonArray();
            foreach (var (_, item) in sorted.Take(FeedCap))
            {
                list.Add(item);
            }

            return Group("feed", "Events", new JsonObject { ["items"] = list, ["more"] = Math.Max(0, sorted.Count - FeedCap) }, source, true);
        }

        private void AddOutage(JsonObject row, Action<string, string, string?, string, string?> add)
        {
            if (row.Str("started_at") is not { } started)
            {
                return;
            }

            var held = row.Get("held") is { IsArray: true } list ? list.AsArray().Where(static h => h.IsMap).Select(static h => h.AsObject().Str("session_id")).OfType<string>().ToList() : [];
            add(started, "outage-start", null, $"connection outage: {held.Count.ToString(CultureInfo.InvariantCulture)} sessions held ({string.Join(", ", held.Select(h => NameOf(h) ?? h))})", null);
            if (row.Str("ended_at") is { } ended && Timestamps.TryParseMs(started, out var startMs) && Timestamps.TryParseMs(ended, out var endMs))
            {
                add(ended, "outage-over", null, $"outage over after {DispatchClock.FmtAge((endMs - startMs) / 1000)}", null);
            }

            if (row.Str("ts") is { } ts && row.Get("dropped_probes") is { IsArray: true } dropped)
            {
                foreach (var d in dropped.AsArray().Where(static d => d.IsMap).Select(static d => d.AsObject()))
                {
                    var id = d.Str("session_id");
                    add(ts, "probe-dropped", NameOf(id), $"probe {NameOf(id) ?? id ?? "?"} dropped: {d.Str("reason") ?? "unknown"}", null);
                }
            }
        }

        private static string? NameFromAddress(string address)
        {
            var at = address.IndexOf('@', StringComparison.Ordinal);
            var name = at < 0 ? address : address[..at];
            return name.Length > 0 ? name : null;
        }

        private string Capped(string text)
        {
            var clean = _c.Src.Clean(text, FeedTextCap + 1);
            return clean.Length > FeedTextCap ? clean[..(FeedTextCap - 1)] + "…" : clean;
        }

        // ------------------------------------------------------------------ C5

        private DetailField Activity()
        {
            const string source = "activity record";
            var history = _session is null ? null : _c.Src.ActivityOf(_orch.Dir, _session);
            if (history is null || (history.Minutes.Count == 0 && history.StateSince is null))
            {
                return Absent("activity", "Activity", source, "no activity record");
            }

            var strip = ActivityStrip(history, _orch.Live, history.Activity == "working", double.NaN, Now);
            return Group("activity", "Activity", strip, source, true);
        }

        // ------------------------------------------------------------------ C8, C6

        private (IReadOnlyList<(string Tree, string Path, GitReading<GitTreeState> Reading, string State)> Rows, int More)? ReadTrees()
        {
            if (_reads is null)
            {
                return null;
            }

            IReadOnlyList<string> paths;
            try
            {
                paths = _reads.Trees(_orch.Dir, [.. _orch.Teams.Select(static t => t.Name ?? "team")]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
            {
                return null;
            }

            var rows = new List<(string, string, GitReading<GitTreeState>, string)>();
            foreach (var path in paths.Take(TreesCap))
            {
                if (!_c.Src.DirExists(path))
                {
                    rows.Add((Basename(path), path, new GitReading<GitTreeState>(null, "missing", null), "missing"));
                    continue;
                }

                var reading = _reads.Git?.Status(path) ?? new GitReading<GitTreeState>(null, GitRunner.UnavailableReason, null);
                var state = reading.Value is not null ? "ok" : reading.Reason ?? GitRunner.UnavailableReason;
                rows.Add((Basename(path), path, reading, state));
            }

            return (rows, Math.Max(0, paths.Count - TreesCap));
        }

        private DetailField Worktrees()
        {
            const string source = "pool enumeration, git";
            if (_trees.Value is not { } trees)
            {
                return Absent("worktrees", "Worktrees", source, "view unavailable");
            }

            var list = new JsonArray();
            foreach (var (tree, path, reading, state) in trees.Rows)
            {
                var g = reading.Value;
                list.Add(new JsonObject
                {
                    ["tree"] = tree,
                    ["path"] = S(Clean(path)),
                    ["branch"] = g is null ? JsonValue.Null : S(Clean(g.Branch, NameCap)),
                    ["upstream"] = g is null ? JsonValue.Null : S(Clean(g.Upstream, NameCap)),
                    ["ahead"] = g?.Ahead is { } ahead ? ahead : JsonValue.Null,
                    ["behind"] = g?.Behind is { } behind ? behind : JsonValue.Null,
                    ["dirty"] = g is null ? JsonValue.Null : g.Dirty,
                    ["state"] = state,
                    ["as_of"] = g is null ? JsonValue.Null : S(reading.AsOf),
                });
            }

            return Group("worktrees", "Worktrees", new JsonObject { ["trees"] = list, ["more"] = trees.More }, source, true);
        }

        private DetailField Pipeline()
        {
            var subs = new List<DetailField>();
            var run = RunOf();
            subs.Add(run is null
                ? Absent("run", "run", "pipeline anchor", "no pipeline run")
                : Present("run", "run", RunValue(run), "pipeline anchor"));

            if (_trees.Value is not { } trees)
            {
                subs.Add(Absent("branches", "branches", "worktrees", "view unavailable"));
            }
            else
            {
                var branches = new JsonArray();
                foreach (var (tree, _, reading, _) in trees.Rows.Where(static r => r.Reading.Value is not null))
                {
                    var g = reading.Value!;
                    branches.Add(new JsonObject
                    {
                        ["tree"] = tree,
                        ["branch"] = S(Clean(g.Branch, NameCap)),
                        ["state"] = g.Upstream is not null && g.Ahead == 0 ? "pushed" : "pending",
                        ["ahead"] = g.Ahead is { } a ? a : JsonValue.Null,
                    });
                }

                subs.Add(Present("branches", "branches", branches, "worktrees"));
            }

            subs.Add(ReleaseTag());
            return Group("pipeline", "Pipeline / release", Fields(subs), "composite", subs.All(static f => f.Verified));
        }

        /// <summary>The checkout a <c>&lt;tree&gt;/.claude/ah2</c> hierarchy directory belongs to, or null for any other directory.</summary>
        private static string? CheckoutOf(string hierarchyDir)
        {
            var claude = Path.GetDirectoryName(hierarchyDir.TrimEnd('/'));
            return Path.GetFileName(hierarchyDir.TrimEnd('/')) == "ah2" && Path.GetFileName(claude) == ".claude" ? Path.GetDirectoryName(claude) : null;
        }

        private DetailField ReleaseTag()
        {
            const string source = "git";
            var main = _trees.Value is { } t && t.Rows.Count > 0 ? t.Rows[0].Path : CheckoutOf(_orch.Dir);
            if (main is null || _reads?.Git is not { } git || !_c.Src.DirExists(main))
            {
                return Absent("release_tag", "last release tag", source, GitRunner.UnavailableReason);
            }

            var tag = git.Describe(main);
            return tag.Value is { } name
                ? Present("release_tag", "last release tag", new JsonObject { ["name"] = S(Clean(name, NameCap)), ["as_of"] = S(tag.AsOf) }, source)
                : Absent("release_tag", "last release tag", source, tag.Reason ?? GitRunner.UnavailableReason);
        }

        // ------------------------------------------------------------------ C6 run, C7

        private JsonObject? _run;
        private bool _runRead;

        private JsonObject? RunOf()
        {
            if (_runRead)
            {
                return _run;
            }

            _runRead = true;
            if (_reads is null)
            {
                return null;
            }

            foreach (var team in _orch.Teams)
            {
                try
                {
                    if (_reads.PipelineRun(_orch.Dir, team.Name) is { } found)
                    {
                        _run = found;
                        break;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
                {
                    // a run that cannot be read is no run
                }
            }

            return _run;
        }

        private static IEnumerable<JsonObject> RowsOf(JsonObject run) =>
            run.Get("rows") is { IsArray: true } rows ? rows.AsArray().Where(static r => r.IsMap).Select(static r => r.AsObject()) : [];

        private JsonObject RunValue(JsonObject run)
        {
            var rows = RowsOf(run).ToList();
            var last = rows.Select(static r => r.Str("time")).OfType<string>().OrderBy(static t => t, StringComparer.Ordinal).LastOrDefault();
            return new JsonObject
            {
                ["run_id"] = S(Clean(run.Str("run_id"), NameCap)),
                ["started_at"] = S(run.Str("started_at")),
                ["decisions"] = rows.Count,
                ["last_decision_at"] = S(last),
            };
        }

        private DetailField Decisions()
        {
            const string source = "pipeline decisions log";
            var openQuestions = _reads?.ParkedQuestions(_orch.Dir).Where(static q => q.Open).OrderByDescending(static q => q.At, StringComparer.Ordinal).ToList() ?? [];
            var run = RunOf();
            if (run is null && openQuestions.Count == 0)
            {
                return Absent("decisions", "Issues & decisions", source, "no pipeline run");
            }

            var rows = run is null ? [] : RowsOf(run).ToList();
            var answered = rows.Where(static r => r.Str("kind") == "decided").Select(static r => r.Str("qkey")).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var decided = rows.Where(static r => r.Str("kind") == "decided" && r.Str("decider") != "user").OrderByDescending(static r => r.Str("time") ?? string.Empty, StringComparer.Ordinal).ToList();
            var userAnswered = rows.Where(static r => r.Str("kind") == "answered").Select(static r => r.Str("answers")).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var parked = rows.Where(r => r.Str("kind") == "parked" && !(r.Str("qkey") is { } q && answered.Contains(q)) && !(r.Str("id") is { } parkedId && userAnswered.Contains(parkedId))).OrderByDescending(static r => r.Str("time") ?? string.Empty, StringComparer.Ordinal).ToList();

            var decisions = new JsonArray();
            foreach (var r in decided.Take(DecisionsCap))
            {
                decisions.Add(new JsonObject
                {
                    ["at"] = S(r.Str("time")),
                    ["item"] = S(Clean(r.Str("item"))),
                    ["choice"] = r.Get("decision") is { IsText: true } choice ? S(Clean(choice.AsString())) : S(Clean(r.Str("choice"))),
                    ["rationale"] = S(Clean(r.Str("rationale"))),
                    ["dangerous"] = r.Get("dangerous") is { IsBool: true } d && d.AsBool(),
                });
            }

            var questions = new JsonArray();
            foreach (var q in openQuestions.Take(DecisionsCap))
            {
                var labels = new JsonArray();
                foreach (var label in q.Labels)
                {
                    labels.Add(S(Clean(label)));
                }

                questions.Add(new JsonObject
                {
                    ["at"] = S(q.At),
                    ["item"] = S(Clean(q.Id)),
                    ["question"] = S(Clean(q.Header + ": " + q.Text)),
                    ["options"] = labels,
                    ["why_user"] = S("parked while work was in flight; answer with: ah msg decision answer " + q.Id),
                    ["dangerous"] = false,
                });
            }

            foreach (var r in parked.Take(DecisionsCap))
            {
                var options = new JsonArray();
                if (r.Get("options") is { IsArray: true } list)
                {
                    foreach (var o in list.AsArray().Where(static o => o.IsText))
                    {
                        options.Add(S(Clean(o.AsString())));
                    }
                }

                questions.Add(new JsonObject
                {
                    ["at"] = S(r.Str("time")),
                    ["item"] = S(Clean(r.Str("item"))),
                    ["question"] = S(Clean(r.Str("question"))),
                    ["options"] = options,
                    ["why_user"] = S(r.Str("why_user")),
                    ["dangerous"] = r.Get("dangerous") is { IsBool: true } d && d.AsBool(),
                });
            }

            return Group(
                "decisions",
                "Issues & decisions",
                new JsonObject { ["decisions"] = decisions, ["questions"] = questions, ["decisions_more"] = Math.Max(0, decided.Count - DecisionsCap), ["questions_more"] = Math.Max(0, parked.Count - DecisionsCap) },
                source,
                true);
        }
    }
}
