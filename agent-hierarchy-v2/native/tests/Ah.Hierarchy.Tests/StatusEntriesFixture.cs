using System.Globalization;
using System.Runtime.CompilerServices;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>
/// ah-msgs-fast-2 §3.8 S8a fixture: three status dirs (a main checkout R, its linked work tree W, an unrelated checkout Q) where W's
/// pool is read by all three; a team home shared by R and W; an open, a done and a child-done lane; an unnamed pane-only roster
/// record; two teams with one orchestrator pid; session-pid rows; dispatch rows, nudges, a pipeline run with rounds and a decision.
/// </summary>
internal sealed class StatusEntriesFixture
{
    internal const long T0 = 1_780_000_000_000;

    internal required string Root { get; init; }

    internal required string Home { get; init; }

    internal required string R { get; init; }

    internal required string W { get; init; }

    internal required string Q { get; init; }

    /// <summary>The status dirs, in a fixed order.</summary>
    internal IReadOnlyList<string> StatusDirs => [Pool(R), Pool(W), Pool(Q)];

    /// <summary>The <c>now</c> values the S8a test assembles at: just after the writes, inside and past the reported window, past an ETA, a day on.</summary>
    internal static readonly long[] Nows = [T0 + 60_000, T0 + (9 * 60_000), T0 + (11 * 60_000) + 1, T0 + (2 * 3_600_000), T0 + 86_400_000];

    internal static string Pool(string checkout) => Path.Combine(checkout, ".claude", "ah2");

    internal static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A pid no process holds: above every platform's pid range, inside int.</summary>
    internal const int DeadPid = 99_999_999;

    internal static StatusEntriesFixture Create()
    {
        var sandbox = UnitSandbox.Create();
        var root = sandbox.Root;
        var r = Path.Combine(root, "r");
        var w = Path.Combine(root, "w");
        var q = Path.Combine(root, "q");
        Directory.CreateDirectory(Path.Combine(r, ".git", "worktrees", "w"));
        File.WriteAllText(Path.Combine(r, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(r, ".git", "worktrees", "w", "HEAD"), "ref: refs/heads/feat-w\n");
        File.WriteAllText(Path.Combine(r, ".git", "worktrees", "w", "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(r, ".git", "worktrees", "w", "gitdir"), Path.Combine(w, ".git") + "\n");
        Directory.CreateDirectory(w);
        File.WriteAllText(Path.Combine(w, ".git"), "gitdir: " + Path.Combine(r, ".git", "worktrees", "w") + "\n");
        Directory.CreateDirectory(Path.Combine(q, ".git"));
        File.WriteAllText(Path.Combine(q, ".git", "HEAD"), "ref: refs/heads/q-main\n");
        foreach (var checkout in new[] { r, w, q })
        {
            Directory.CreateDirectory(Path.Combine(Pool(checkout), "msgs"));
        }

        var user = Path.Combine(sandbox.Home, ".claude", "ah2");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "agent-hierarchy.json"), "{\"enabled\":true}\n");
        var f = new StatusEntriesFixture { Root = root, Home = sandbox.Home, R = r, W = w, Q = q };
        f.Seed();
        return f;
    }

    /// <summary>The roster core of the last <see cref="Services"/> call.</summary>
    internal IRosterCore Roster { get; private set; } = null!;

    internal HierarchyServices Services(
        MsgsRoute? route = null, long nowMs = T0, IStatusEntryStore? entries = null, IStateWriter? writer = null, Func<string, JsonValue?>? backlogFacts = null, IFileReader? files = null, Func<string, JsonValue, string?>? memberVerdict = null, FakeClock? clock = null)
    {
        var env = Env();
        var io = PlatformModule.CreateIo(null);
        io = files is null ? io : io with { Files = files };
        IRosterCore roster = null!;
        var facts = new ProcessFacts(R, 4242);
        var config = ConfigModule.CreateCore(io.Files, env, () => roster, facts);
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
        Roster = roster;
        return HierarchyModule.Create(new HierarchyEnvironment(
            io.Files, env, clock ?? new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(nowMs)), new SequenceRandom(), io.Processes, config, roster, writer, facts, null, null, memberVerdict, null, route ?? TestMsgStore.SyncedRoute(env.AhHome, Root), StatusEntries: entries, BacklogFacts: backlogFacts, SessionPids: Ah.StateDb.StateDbModule.CreateSessionPids(env.AhHome), Activity: Ah.StateDb.StateDbModule.CreateActivity(env.AhHome), Gates: Ah.StateDb.StateDbModule.CreateGates(env.AhHome), RosterRecords: Ah.StateDb.StateDbModule.CreateRosterRecords(env.AhHome), ProgressLogs: Ah.StateDb.StateDbModule.CreateProgressLogs(env.AhHome), Checkpoints: Ah.StateDb.StateDbModule.CreateCheckpoints(env.AhHome)));
    }

    private FakeEnvironment Env() =>
        new(new Dictionary<string, string> { ["HOME"] = Home, ["TZ"] = "UTC" }, new PluginInfo(HierarchyTableFns.AhRoot, "wrangl", "0.0.0", "1"));

    /// <summary>The status of <paramref name="dir"/> at <paramref name="nowMs"/> without its two clock stamps, as one line of JSON.</summary>
    internal static string StatusText(HierarchyServices services, string dir, long nowMs)
    {
        var status = services.Status.ComputeStatus(dir, nowMs, dir, true);
        status.Remove("written_at");
        status.Remove("expires_at");
        return JsonText.Stringify(status, 0);
    }

    /// <summary>The golden status file beside this source file: every status dir at every <see cref="Nows"/> value, with the sandbox root replaced by <c>$ROOT</c>.</summary>
    internal static string GoldenPath([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Golden", "status-entries-s8a.txt");

    internal string Snapshot(HierarchyServices services)
    {
        var lines = new List<string>();
        foreach (var dir in StatusDirs)
        {
            foreach (var now in Nows)
            {
                lines.Add($"{Path.GetRelativePath(Root, dir)} @{now - T0}: {StatusText(services, dir, now).Replace(Root, "$ROOT", StringComparison.Ordinal)}");
            }
        }

        return string.Join("\n", lines) + "\n";
    }

    // ---------------------------------------------------------------- seeding

    private void Seed()
    {
        var home = Pool(R);
        var me = Environment.ProcessId;
        Team(home, "t", $$"""
            {"team_id":"t-t","team":"t","orchestrator":{"pid":{{me}},"session_id":"o-t"},
             "members":[
               {"name":"t-implementor","role":"implementor","expected_root":"{{W}}"},
               {"name":"t-reviewer","role":"reviewer"},
               {"name":"t-sub","role":"sub-orchestrator"},
               {"name":"t-sub2","role":"sub-orchestrator"},
               {"name":"t-sub3","role":"sub-orchestrator"}],
             "streams":{
               "s1":{"sub_orchestrator":"t-sub","state":"open","opened":"{{Iso(T0 - 600_000)}}","worktree":"{{W}}"},
               "s2":{"sub_orchestrator":"t-sub2","state":"done","opened":"{{Iso(T0 - 600_000)}}"},
               "s3":{"sub_orchestrator":"t-sub3","opened":"{{Iso(T0 - 600_000)}}"} } }
            """);
        Team(home, "c", """
            {"team_id":"t-c","team":"c","parent":{"team":"t","stream":"s3"},"workstream":{"state":"done"},"members":[]}
            """);
        Team(home, "u", $$"""
            {"team_id":"t-u","team":"u","orchestrator":{"pid":{{me}},"session_id":"o-u"},
             "members":[{"name":"u-implementor","role":"implementor","transport_id":"%7"}]}
            """);
        Team(home, "v", $$"""
            {"team_id":"t-v","team":"v","orchestrator":{"pid":{{DeadPid}},"pid_start":"2026-01-01T00:00:00.000Z","session_id":"o-v"},"members":[{"name":"v-x","role":"implementor"}]}
            """);
        Team(Pool(Q), "q", $$"""
            {"team_id":"t-q","team":"q","orchestrator":{"pid":{{me}},"session_id":"o-q"},"members":[{"name":"q-implementor","role":"implementor","expected_root":"{{W}}"}]}
            """);

        Peers(home,
            Row(new() { ["type"] = "peer", ["status"] = "up", ["name"] = "t-implementor", ["role"] = "implementor", ["session_id"] = "s-impl", ["pid"] = me, ["team"] = "t", ["ts"] = Iso(T0 - 300_000) }),
            Row(new() { ["type"] = "peer", ["status"] = "down", ["name"] = "t-reviewer", ["role"] = "reviewer", ["session_id"] = "s-rev", ["pid"] = DeadPid, ["pid_start"] = "2026-01-01T00:00:00.000Z", ["team"] = "t", ["ts"] = Iso(T0 - 200_000) }),
            Row(new() { ["type"] = "peer", ["status"] = "up", ["session_id"] = "s-pane", ["role"] = "implementor", ["pid"] = me, ["pane_id"] = "%7", ["ts"] = Iso(T0 - 100_000) }));

        // dispatch records live in the per-HOME peer store; a request with none is not a dispatch (no origin)
        var dispatched = new (string Id, long At)[]
        {
            ("20260528-000002-impl", T0 - 29_000), ("20260528-000002-impl", T0 - 30_000), ("20260528-000003-revw", T0 - 25_000),
            ("20260528-000004-orch", T0 - 15_000), ("20260528-000005-chr1", T0 - 500_000), ("20260528-000006-chr2", T0 - 500_000),
            ("20260528-000007-oldr", T0 - 7_200_000), ("20260528-000008-uimp", T0 - 10_000), ("20260528-000010-wimp", T0 - 8_000),
            ("20260528-000011-wq01", T0 - 7_000), ("20260528-000012-qown", T0 - 6_000), ("20260528-000013-feat", T0 - 5_000),
        };
        Lines(Path.Combine(Home, ".claude", "ah2"), "agent-hierarchy.peer-pending.jsonl", [.. dispatched.Select(d =>
            Row(new() { ["type"] = "dispatch", ["session_id"] = "o-t", ["request_id"] = d.Id, ["to"] = "x", ["created"] = Iso(d.At) }))]);
        Gates(home,
            Row(new() { ["type"] = "liveness-nudge", ["request_id"] = "20260528-000002-impl", ["op_id"] = "n1", ["ts"] = Iso(T0 - 5_000) }),
            Row(new() { ["type"] = "liveness-nudge", ["request_id"] = "20260528-000002-impl", ["op_id"] = "n1", ["ts"] = Iso(T0 - 4_000) }),
            Row(new() { ["type"] = "liveness-nudge", ["request_id"] = "20260528-000002-impl", ["op_id"] = "n2", ["ts"] = Iso(T0 - 3_000) }),
            Row(new() { ["type"] = "other-gate", ["request_id"] = "20260528-000002-impl", ["ts"] = Iso(T0 - 2_000) }));
        SessionPids(home, ("o-t", me), ("o-old", me), ("o-elsewhere", DeadPid));
        Activity(home, "s-impl", $$"""{"activity":"working","at":"{{Iso(T0 - 50_000)}}","tool":"Bash","tool_at":"{{Iso(T0 - 50_000)}}"}""");
        Activity(home, "s-pane", $$"""{"activity":"blocked","at":"{{Iso(T0 - 40_000)}}","blocked_by":"permission"}""");

        var anchor = "20260528-000001-anch";
        Request(home, anchor, "orchestrator", HierarchyModule.PipelineAnchorSlug, "t", T0 - 120_000, toName: null);
        Request(home, "20260528-000002-impl", "implementor", "feat", "t", T0 - 30_000, toName: "t-implementor", eta: "small", title: "Build the thing", item: "item-1");
        Request(home, "20260528-000003-revw", "reviewer", "feat", "t", T0 - 25_000, toName: "t-reviewer");
        Response(home, "20260528-000003-revw", "reviewer", "feat", "t", T0 - 20_000, "- VERDICT PASS feat@abc1234\n- looks right\n");
        Request(home, "20260528-000004-orch", "orchestrator", "feat", "t", T0 - 15_000, toName: null);
        Request(home, "20260528-000005-chr1", "sub-orchestrator", "lane-s1", "t", T0 - 500_000, toName: "t-sub");
        Response(home, "20260528-000005-chr1", "sub-orchestrator", "lane-s1", "t", T0 - 400_000, "- lane running\n");
        Request(home, "20260528-000006-chr2", "sub-orchestrator", "lane-s2", "t", T0 - 500_000, toName: "t-sub2");
        Response(home, "20260528-000006-chr2", "sub-orchestrator", "lane-s2", "t", T0 - 400_000, "- lane done\n");
        Request(home, "20260528-000007-oldr", "implementor", "old", "t", T0 - 7_200_000, toName: "t-implementor");
        Response(home, "20260528-000007-oldr", "implementor", "old", "t", T0 - 7_100_000, "- DONE old work\n");
        Request(home, "20260528-000008-uimp", "implementor", "u-work", "u", T0 - 10_000, toName: "u-implementor", eta: "large");
        Request(home, "20260528-000009-decs", "orchestrator", "dec-feat", "t", T0 - 9_000, toName: null);
        Request(Pool(W), "20260528-000010-wimp", "implementor", "w-feat", "t", T0 - 8_000, toName: "t-implementor");
        Request(Pool(W), "20260528-000011-wq01", "implementor", "q-feat", "q", T0 - 7_000, toName: "q-implementor");
        Request(Pool(Q), "20260528-000012-qown", "implementor", "q-own", "q", T0 - 6_000, toName: "q-implementor");
        Request(home, "20260528-000013-feat", "implementor", "feat", "t", T0 - 5_000, toName: "t-implementor");
        Response(home, "20260528-000013-feat", "implementor", "feat", "t", T0 - 4_000, "- DONE round two\n");

        var run = Path.Combine(home, "pipeline", anchor);
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "decisions.jsonl"), JsonText.Stringify(new JsonObject
        {
            ["id"] = "d1", ["time"] = Iso(T0 - 60_000), ["run"] = anchor, ["qkey"] = "q1",
            ["decision"] = new JsonObject { ["kind"] = "park", ["question"] = "which?" },
        }, 0) + "\n");
    }

    private static string Row(JsonObject o) => JsonText.Stringify(o, 0);

    /// <summary>An activity record through <c>activity.put</c> (per-dir-files-to-db S2: the table is its only home).</summary>
    /// <summary>Gate rows (per-dir-files-to-db S3: the <c>gates</c> table) appended as given through <c>gates.put</c>.</summary>
    internal void Gates(string dir, params string[] rows)
    {
        var writer = new InProcessStateWriter { AhHome = Env().AhHome };
        writer.Domains = Services(writer: writer).StateDomains;
        foreach (var row in rows)
        {
            Assert.True(writer.Invoke("gates", "put", JsonText.ToUtf8(new JsonObject { ["dir"] = dir, ["body"] = row }, 0), new RequestContext(dir, dir, string.Empty, string.Empty)).Ok);
        }
    }

    // per-dir-files-to-db S7: the roster is roster_records rows
    internal void Peers(string dir, params string[] rows)
    {
        var writer = new InProcessStateWriter { AhHome = Env().AhHome };
        writer.Domains = Services(writer: writer).StateDomains;
        foreach (var row in rows)
        {
            Assert.True(writer.Invoke("peers", "put", JsonText.ToUtf8(new JsonObject { ["dir"] = dir, ["body"] = row }, 0), new RequestContext(dir, dir, string.Empty, string.Empty)).Ok);
        }
    }

    internal void Activity(string dir, string subject, string body)
    {
        var writer = new InProcessStateWriter { AhHome = Env().AhHome };
        writer.Domains = Services(writer: writer).StateDomains;
        var args = new JsonObject { ["dir"] = dir, ["subject"] = subject, ["body"] = body };
        Assert.True(writer.Invoke("activity", "put", JsonText.ToUtf8(args, 0), new RequestContext(dir, dir, string.Empty, string.Empty)).Ok);
    }

    /// <summary>Session-pid rows through <c>session-pids.append</c>, in order (per-dir-files-to-db S1: the table is their only home).</summary>
    private void SessionPids(string dir, params (string Session, int Pid)[] rows)
    {
        var writer = new InProcessStateWriter { AhHome = Env().AhHome };
        writer.Domains = Services(writer: writer).StateDomains;
        foreach (var (session, pid) in rows)
        {
            var args = new JsonObject { ["dir"] = dir, ["session_id"] = session, ["pid"] = pid };
            Assert.True(writer.Invoke("session-pids", "append", JsonText.ToUtf8(args, 0), new RequestContext(dir, dir, string.Empty, string.Empty)).Ok);
        }
    }

    private static void Lines(string dir, string name, params string[] rows) => File.WriteAllText(Path.Combine(dir, name), string.Join("\n", rows) + "\n");

    private static void Team(string dir, string name, string json)
    {
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        File.WriteAllText(Path.Combine(dir, "teams", name + ".json"), JsonText.Stringify(JsonText.Parse(json), 0) + "\n");
    }

    private static void Request(string pool, string id, string to, string slug, string team, long createdMs, string? toName, string eta = "medium", string? title = null, string? item = null)
    {
        var path = Path.Combine(pool, "msgs", RequestName(id, to, slug));
        File.WriteAllText(path, RequestText(id, to, slug, team, createdMs, toName, eta, title, item));
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(createdMs).UtcDateTime);
    }

    private static void Response(string pool, string id, string from, string slug, string team, long createdMs, string body)
    {
        var path = Path.Combine(pool, "msgs", ResponseName(id, slug));
        File.WriteAllText(path, ResponseText(id, from, slug, team, createdMs, body));
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(createdMs).UtcDateTime);
    }

    internal static string RequestName(string id, string to, string slug) => $"{id}--{to}--{slug}--request.md";

    internal static string ResponseName(string id, string slug) => $"{id}--orchestrator--{slug}--response.md";

    internal static string RequestText(string id, string to, string slug, string team, long createdMs, string? toName, string eta = "medium", string? title = null, string? item = null, string? decision = null, string? created = null)
    {
        var extra = (title is null ? string.Empty : $"title: {title}\n") + (item is null ? string.Empty : $"item: {item}\n") + (decision is null ? string.Empty : $"decision: {decision}\n");
        return $"---\nid: {id}\ntype: request\nto: {to}\nfrom: orchestrator\nslug: {slug}\nparent: null\nreason: null\neta: {eta}\nto_name: {toName ?? "null"}\nfrom_name: null\nteam: {team}\n{extra}created: {created ?? Iso(createdMs)}\n---\n\n## [0] tldr\n- do {slug}\n";
    }

    internal static string ResponseText(string id, string from, string slug, string team, long createdMs, string body) =>
        $"---\nid: {id}\ntype: response\nto: orchestrator\nfrom: {from}\nslug: {slug}\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: {team}\ncreated: {Iso(createdMs)}\n---\n\n## [0] tldr\n- [1] status: {body.TrimStart('-', ' ')}";
}
