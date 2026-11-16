using System.Reflection;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>Runs an op as the engine would, in this process: the domain's body with a real-file write context, then its after-write hook.</summary>
internal sealed class InProcessStateWriter : IStateWriter
{
    private readonly FsStoreWriteContext _context = new();

    internal IReadOnlyList<IStateDomain> Domains { get; set; } = [];

    /// <summary>The home whose database a message write lands in: a message has no file body.</summary>
    internal string? AhHome { get; set; }

    /// <summary>Whether a decisions write runs its database body, as the engine does while <c>mode.decisions</c> is <c>db</c> (per-dir-files-to-db S9).</summary>
    internal bool DecisionsInDb { get; set; }

    public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null)
    {
        var d = Domains.Single(x => x.Name == domain);
        var o = d.Ops.Single(x => x.Name == op);
        var write = o.Kind == OpKind.Write;
        var call = new OpCall(op, argsJson, context, write ? options?.OperationId ?? Guid.NewGuid().ToString() : null);
        var result = write && (domain == "msgs" || d.DbOnly || o.ModeKey is not null || (DecisionsInDb && domain == "decisions")) && o.DbBody is { } dbBody ? InDatabase(o.ModeKey ?? domain, call, dbBody, d.DbOnly) : o.Body(call, write ? _context : null);
        if (!result.Ok)
        {
            return new StateResult(false, default, result.Error, "op_failed");
        }

        if (write)
        {
            d.AfterWrite(call, result);
        }

        return new StateResult(true, result.ResultJson);
    }

    private OpResult InDatabase(string domain, OpCall call, Func<OpCall, IDbOpTransaction, OpResult> body, bool dbOnly)
    {
        var home = AhHome ?? throw new InvalidOperationException("a database write needs AhHome");
        var outcome = Ah.StateDb.StateDbModule.CreateOpRunner(home).Run(new DbOpRequest(domain, call.OperationId, "direct", tx => body(call, tx), DbOnly: dbOnly));
        return outcome.Status == DbOpStatus.Done ? new OpResult(true, outcome.ResultJson) : new OpResult(false, default, outcome.Error);
    }

    public bool IsHomeScoped(string domain) => Domains.Any(d => d.Name == domain && d.HomeScoped);

    public bool IsDbOnly(string domain) => Domains.Any(d => d.Name == domain && d.DbOnly);

    public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null) =>
        throw new NotSupportedException();
}

/// <summary>Calls the native function a unit table names (<c>lib/fn</c>), with the case's arguments.</summary>
internal static class HierarchyTableFns
{
    private static readonly string NativeRoot = typeof(HierarchyTableFns).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    /// <summary>The agent-hierarchy plugin root the fake environment names; tables write it as <c>$AHROOT</c>.</summary>
    internal static readonly string AhRoot = Path.GetFullPath(Path.Combine(NativeRoot, ".."));

    internal static readonly HashSet<string> All =
    [
        "lib-usage/roleFor", "lib-usage/zeroUsage", "lib-usage/addUsage", "lib-usage/sumTranscript", "lib-usage/stateDirs",
        "lib-recovery/delaySec", "lib-recovery/streakFor", "lib-recovery/oneLine", "lib-recovery/errorKind",
        "lib-recovery/transcriptPathOk", "lib-recovery/transcriptFailure",
        "lib-context/readContext", "lib-context/ownsTeam",
        "lib-hier/etaOf", "lib-hier/thresholdFor", "lib-hier/msgFilename", "lib-hier/msgsDir", "lib-hier/archiveDir", "lib-hier/specsDir",
        "lib-hier/peersPath", "lib-hier/gatesPath", "lib-hier/readJsonl","lib-hier/ensureHierarchyDir",
        "lib-hier/localIso", "lib-hier/fmtAge", "lib-hier/ageSecOf", "lib-hier/parseFrontmatter", "lib-hier/indexAnchors",
        "lib-hier/messageHome", "lib-hier/findByIdAndType", "lib-hier/createMessage", "lib-hier/responsePlan", "lib-hier/fillPlan",
        "lib-hier/responseFrontmatter",
        "lib-hier/rootOf", "lib-hier/listDownstreamDispatches", "lib-hier/openExchanges", "lib-hier/pipelineRunLive", "lib-hier/sweep",
        "lib-hier/exchangeAgeSec", "lib-hier/validateRequestToken", "lib-hier/validateResponseToken", "lib-hier/missingIntent",
        "lib-hier/reviewIntentGap", "lib-hier/reviewIntentReason", "lib-hier/reportStatus", "lib-hier/outcomeTokens",
        "lib-hier/firstSection", "lib-hier/statusFollowUp", "lib-hier/followUpRule", "lib-hier/followUpRules",
        "lib-hier/reportFollowUp", "lib-hier/hasResponseToken", "lib-hier/responseLanded", "lib-hier/readGates",
        "lib-hier/sessionRouteRecord", "lib-hier/recordRoute", "lib-hier/effectiveRoute",
        "lib-hier/readRoster", "lib-hier/latestRoster", "lib-hier/attributedRoster", "lib-hier/upRecordFor", "lib-hier/recordLiveness",
        "lib-hier/attributedLiveness", "lib-hier/synthesizedPeerName", "lib-hier/livePeerSlots", "lib-hier/roster",
        "lib-hier/describeInstance", "lib-hier/rosterLine", "lib-hier/sessionModel", "lib-hier/cacheSessionModel",
        "lib-hier/roleTier", "lib-hier/roleModelLabel", "lib-hier/tierLine", "lib-hier/routeLine", "lib-hier/buildStateBlock",
        "lib-hier/msgRoles", "lib-hier/roleForPeerName", "lib-hier/roleForAnyPeerName", "lib-hier/realCwd", "lib-hier/pathException",
        "lib-hier/list", "lib-hier/index",
        "lib-hier/append", "lib-hier/read", "lib-hier/afterWrite", "lib-hier/gatesAfterWrite", "lib-hier/stateDirs", "lib-hier/hasGate",
        "lib-peer/peerPendingPath", "lib-peer/stripRef", "lib-peer/parseSentinel", "lib-peer/parseWrapper",
        "lib-peer/unwrapCrossSession", "lib-peer/extractPendingRecord", "lib-peer/readPeerRecords", "lib-peer/latestByKey",
        "lib-peer/appendPeerRecord", "lib-peer/cliRootAlreadyEmitted", "lib-peer/recordCliRootEmitted",
        "lib-peer/recordPeerRow", "lib-peer/appendRelayRecord", "lib-peer/pendingFor", "lib-peer/appendTurnMarker",
        "lib-peer/latestTurnMarker", "lib-peer/appendDispatchRecord", "lib-peer/appendReportRecord", "lib-peer/reportShown",
        "lib-peer/dispatchRecordsFor", "lib-peer/latestDispatchRows", "lib-peer/appendSubagentRecord",
        "lib-peer/latestSubagentRecords", "lib-peer/hasInflightSubagent", "lib-peer/dispatchOrigin",
        "lib-peer/targetSatisfiesRecord", "lib-peer/unconsumedWatchEvents", "lib-peer/stateDirs", "lib-peer/append",
        "lib-peer/read", "lib-peer/MAX_NUDGES", "lib-peer/SUBAGENT_INFLIGHT_TTL_MS",
        "lib-decisions/DECISION_ITEM_CAP", "lib-decisions/DECISION_RUN_CAP", "lib-decisions/DECISION_INPUT_MAX",
        "lib-decisions/MERGE_METHODS", "lib-decisions/decisionLogPath", "lib-decisions/openRunAnchor",
        "lib-decisions/knownRun", "lib-decisions/readDecisions", "lib-decisions/decisionSummary",
        "lib-decisions/questionKey", "lib-decisions/appendDecision", "lib-decisions/constraintLines",
        "lib-decisions/liveRun", "lib-decisions/mergeCommand", "lib-decisions/parseMergeForm",
        "lib-decisions/appendMergeRecord", "lib-decisions/readDecisionInput", "lib-decisions/runLiveness",
        "lib-decisions/append", "lib-decisions/add", "lib-decisions/read", "lib-decisions/stateDirs",
        "lib-decisions/afterWrite",
        "lib-status/STATUS_SCHEMA", "lib-status/STATUS_SIZE_CAP", "lib-status/ROUND_CAP", "lib-status/clean",
        "lib-status/readActivity", "lib-status/readActivityRecord", "lib-status/recordActivity", "lib-status/clearActivity",
        "lib-status/sweepActivity", "lib-status/memberFills", "lib-status/describeMembers",
        "lib-status/computeStatus",
        "lib-status/record", "lib-status/clear", "lib-status/sweep", "lib-status/stateDirs",
        "lib-status/activityAfterWrite", "lib-status/refreshStatus", "lib-status/refreshStatusSync",
        "lib-status/recordActivityRouted", "lib-status/clearActivityRouted", "lib-status/sweepActivityRouted",
        "lib-status/plainStatus",
        "lib-hier/CHECKIN_CADENCE", "lib-hier/ETAS", "lib-hier/ETA_THRESHOLD_SEC", "lib-hier/FILL_BODY_MAX",
        "lib-hier/LANDED_QUIET_MS", "lib-hier/MSG_TYPES", "lib-hier/NOTE_RULE", "lib-hier/PEERS_FILE",
        "lib-hier/PIPELINE_ANCHOR_SLUG", "lib-hier/REASONS", "lib-hier/REQUEST_KEYS", "lib-hier/RESPONSE_KEYS",
        "lib-hier/ROSTER_FRESH_SEC", "lib-hier/SELF_STATE", "lib-hier/SWEEP_DAYS", "lib-hier/TOOL_WRITE_INTERVAL_SEC",
        "lib-hier/TEAM_GUIDE", "lib-hier/ID_RE", "lib-hier/SLUG_RE", "lib-hier/MSG_TOKEN_RE", "lib-hier/appendRosterRecord",
        "lib-hier/healFalseDown", "lib-usage/CACHE_FILE", "lib-usage/PRUNE_AT", "lib-usage/PRUNE_TO",
        "lib-recovery/RESUME_PARAGRAPH", "lib-recovery/RETRYABLE",
        "lib-recovery/SAME_FAILURE_MS", "lib-recovery/STREAK_WINDOW_MS", "lib-context/TAIL_BYTES",
    ];

    internal sealed record Fixture(
        IFileReader Files,
        IAhEnvironment Environment,
        FakeClock Clock,
        IConfigCore Config,
        IRosterCore Roster,
        IProcessInspector Processes,
        HierarchyServices Services,
        FsStoreWriteContext Writer,
        IHierarchyBootstrap Bootstrap,
        string Cwd);

    internal static Fixture Build(UnitCase c, UnitSandbox sandbox)
    {
        var envVars = sandbox.Environment(c);
        var plugin = new PluginInfo(AhRoot, "wrangl", "0.0.0", "1");
        var env = new FakeEnvironment(envVars, plugin);
        var io = PlatformModule.CreateIo(null);
        var cwd = sandbox.WorkingDirectory(c);
        var instant = c.Now is not null ? DateTimeOffset.Parse(c.Now, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
        if (c.Args.Count > 0 && c.Args[0].IsMap && c.Args[0].AsObject().Get("$date") is { IsText: true } d)
        {
            instant = DateTimeOffset.Parse(d.AsString(), System.Globalization.CultureInfo.InvariantCulture);
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(envVars.GetValueOrDefault("TZ", "UTC"));
        var clock = new FakeClock(instant) { Zone = zone };
        IRosterCore roster = null!;
        var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(cwd, 4242));
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner()));
        var writer = new InProcessStateWriter { AhHome = env.AhHome };
        var bootstrap = HierarchyModule.CreateBootstrap(io.Writer);
        var services = HierarchyModule.Create(new HierarchyEnvironment(io.Files, env, clock, new SequenceRandom(), io.Processes, config, roster, writer, new ProcessFacts(cwd, 4242), bootstrap) { MsgsRoute = TestMsgStore.Route(env.AhHome), SessionPids = Ah.StateDb.StateDbModule.CreateSessionPids(env.AhHome), Activity = Ah.StateDb.StateDbModule.CreateActivity(env.AhHome), Gates = Ah.StateDb.StateDbModule.CreateGates(env.AhHome), RosterRecords = Ah.StateDb.StateDbModule.CreateRosterRecords(env.AhHome), ProgressLogs = Ah.StateDb.StateDbModule.CreateProgressLogs(env.AhHome), Checkpoints = Ah.StateDb.StateDbModule.CreateCheckpoints(env.AhHome), ProgressDocs = Ah.StateDb.StateDbModule.CreateProgressDocs(env.AhHome) });
        writer.Domains = services.StateDomains;
        SeedSessionPids(writer, sandbox.Root);
        SeedActivity(writer, sandbox.Root);
        SeedGates(writer, sandbox.Root);
        SeedPeers(writer, sandbox.Root);
        return new Fixture(io.Files, env, clock, config, roster, io.Processes, services, new FsStoreWriteContext(), bootstrap, cwd);
    }

    /// <summary>
    /// A recorded case's <c>session-pids.jsonl</c> input (per-dir-files-to-db S1): each line goes through <c>session-pids.append</c>
    /// in file order, which keeps the rows the writer keeps, and the file is removed, as no shipped reader reads it.
    /// </summary>
    private static void SeedSessionPids(InProcessStateWriter writer, string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "session-pids.jsonl", SearchOption.AllDirectories).ToList())
        {
            var dir = Path.GetDirectoryName(file)!;
            foreach (var line in File.ReadAllLines(file).Where(static l => l.Length > 0))
            {
                var row = JsonText.Parse(line);
                var args = new JsonObject { ["dir"] = dir, ["session_id"] = row.Member("session_id") ?? JsonValue.Null, ["pid"] = row.Member("pid") ?? JsonValue.Null };
                _ = writer.Invoke("session-pids", "append", JsonText.ToUtf8(args, 0), new RequestContext(dir, dir, string.Empty, string.Empty));
            }

            File.Delete(file);
        }
    }

    /// <summary>
    /// A recorded case's <c>activity/&lt;subject&gt;.json</c> inputs (per-dir-files-to-db S2): each regular file goes through
    /// <c>activity.put</c> with its text and mtime, and is removed, as the table is the records' only home.
    /// </summary>
    private static void SeedActivity(InProcessStateWriter writer, string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Where(static f => Path.GetFileName(Path.GetDirectoryName(f)) == "activity").ToList())
        {
            // a record reached through a link was never read as a record
            if (new FileInfo(file).LinkTarget is not null || new DirectoryInfo(Path.GetDirectoryName(file)!).LinkTarget is not null)
            {
                continue;
            }

            var dir = Path.GetDirectoryName(Path.GetDirectoryName(file))!;
            var args = new JsonObject
            {
                ["dir"] = dir,
                ["subject"] = Path.GetFileNameWithoutExtension(file),
                ["body"] = File.ReadAllText(file),
                ["updated_at_ms"] = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds(),
            };
            _ = writer.Invoke("activity", "put", JsonText.ToUtf8(args, 0), new RequestContext(dir, dir, string.Empty, string.Empty));
            File.Delete(file);
        }
    }

    /// <summary>
    /// A recorded case's <c>gates.jsonl</c> input (per-dir-files-to-db S3): each non-blank line goes through <c>gates.put</c> as given, in file
    /// order, and the file is removed, as the table is the log's only home.
    /// </summary>
    private static void SeedGates(InProcessStateWriter writer, string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "gates.jsonl", SearchOption.AllDirectories).Where(static f => new FileInfo(f).LinkTarget is null).ToList())
        {
            var dir = Path.GetDirectoryName(file)!;
            foreach (var line in File.ReadAllLines(file).Where(static l => l.Trim().Length > 0))
            {
                _ = writer.Invoke("gates", "put", JsonText.ToUtf8(new JsonObject { ["dir"] = dir, ["body"] = line }, 0), new RequestContext(dir, dir, string.Empty, string.Empty));
            }

            File.Delete(file);
        }
    }

    /// <summary>A recorded case's <c>peers.jsonl</c> input (per-dir-files-to-db S7): each non-blank line goes through <c>peers.put</c> as given, in file order, and the file is removed.</summary>
    private static void SeedPeers(InProcessStateWriter writer, string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "peers.jsonl", SearchOption.AllDirectories).Where(static f => new FileInfo(f).LinkTarget is null).ToList())
        {
            var dir = Path.GetDirectoryName(file)!;
            var lines = File.ReadAllLines(file).Where(static l => l.Trim().Length > 0).ToList();

            // removed first: a put's status refresh reads the roster, and a file still there would be taken as well
            File.Delete(file);
            foreach (var line in lines)
            {
                _ = writer.Invoke("peers", "put", JsonText.ToUtf8(new JsonObject { ["dir"] = dir, ["body"] = line }, 0), new RequestContext(dir, dir, string.Empty, string.Empty));
            }
        }
    }

    /// <summary>Writes the roster rows of the dir an expected <c>&lt;dir&gt;/peers.jsonl</c> names out to that file, one per line (none: no file), for the comparison.</summary>
    internal static void ProjectPeers(string ahHome, string file)
    {
        if (Path.GetFileName(file) != "peers.jsonl" || Directory.Exists(file))
        {
            return;
        }

        if (File.Exists(file))
        {
            File.Delete(file);
        }

        if (Ah.TestKit.TestPeers.Text(ahHome, Path.GetDirectoryName(file)!) is { Length: > 0 } text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
    }

    /// <summary>Writes the <c>gates</c> rows of the dir an expected <c>&lt;dir&gt;/gates.jsonl</c> names out to that file, one per line (none: no file), for the comparison.</summary>
    internal static void ProjectGates(string ahHome, string file)
    {
        if (Path.GetFileName(file) != "gates.jsonl" || Directory.Exists(file))
        {
            return;
        }

        if (File.Exists(file))
        {
            File.Delete(file);
        }

        if (Ah.TestKit.TestGates.Text(ahHome, Path.GetDirectoryName(file)!) is { Length: > 0 } text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
    }

    /// <summary>Writes the <c>activity</c> row an expected <c>&lt;dir&gt;/activity/&lt;subject&gt;.json</c> names out to that file (none: no file), for the comparison.</summary>
    internal static void ProjectActivity(string ahHome, string file)
    {
        // a path the case made a file or a link has no row to show
        if (Path.GetFileName(Path.GetDirectoryName(file)) != "activity" || !file.EndsWith(".json", StringComparison.Ordinal) || File.Exists(Path.GetDirectoryName(file)) || new DirectoryInfo(Path.GetDirectoryName(file)!).LinkTarget is not null)
        {
            return;
        }

        if (File.Exists(file))
        {
            File.Delete(file);
        }

        if (Ah.StateDb.StateDbModule.CreateActivity(ahHome).Row(Path.GetDirectoryName(Path.GetDirectoryName(file))!, Path.GetFileNameWithoutExtension(file)) is { } row)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, row.Body);
        }
    }

    private static DateTimeOffset? Instant(JsonValue? v) =>
        v is { IsText: true } t ? DateTimeOffset.Parse(t.AsString(), System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// A liveness answer as the recorded Node oracle wrote it: without the verdict fields only the native code adds
    /// (<c>proven_dead</c>, <c>liveness</c>, <c>why</c>, <c>last_life_at</c>, <c>dead_at</c>, <c>registered</c>); <c>live</c> and the rest are compared as recorded.
    /// </summary>
    private static JsonValue WithoutNativeLivenessFields(JsonObject answer)
    {
        foreach (var key in new[] { "proven_dead", "liveness", "why", "last_life_at", "dead_at", "registered" })
        {
            _ = answer.Remove(key);
        }

        return answer;
    }

    internal static JsonValue? Dispatch(string lib, string fn, UnitCase c, UnitSandbox sandbox)
    {
        var f = Build(c, sandbox);
        var env = f.Environment;
        var io = f.Files;
        var clock = f.Clock;
        var roster = f.Roster;
        var writer = f.Writer;
        var m = f.Services.Messages;
        var cwd = f.Cwd;
        var request = new RequestContext(cwd, cwd + "/.claude/ah2", cwd + "/.claude/ah2", cwd + "/sock");

        JsonValue? Arg(int i) => i < c.Args.Count ? sandbox.Decode(c.Args[i]) : null;
        string? Text(int i) => Arg(i) is { IsText: true } t ? t.AsString() : null;
        double? Num(int i) => Arg(i) is { IsNumber: true } n ? n.AsNumber() : null;
        JsonObject Obj(int i) => Arg(i) is { IsMap: true } o ? o.AsObject() : throw new InvalidOperationException("argument is not an object");

        switch (lib + "/" + fn)
        {
            case "lib-usage/roleFor":
                return HierarchyModule.CreateUsage(io, env).RoleFor(Arg(0), Arg(1));
            case "lib-usage/zeroUsage":
                return HierarchyModule.CreateUsage(io, env).ZeroUsage();
            case "lib-usage/addUsage":
                return HierarchyModule.CreateUsage(io, env).AddUsage(Arg(0)!.Value.AsObject(), Arg(1));
            case "lib-usage/sumTranscript":
                return HierarchyModule.CreateUsage(io, env).SumTranscript(Text(0)!);
            case "lib-usage/stateDirs":
                return new JsonArray { f.Services.StateDomains.Single(d => d.Name == "usage").StateDirectory(request) };

            case "lib-recovery/delaySec":
                return HierarchyModule.CreateRecovery(io, env, clock).DelaySec(Arg(0), Arg(1));
            case "lib-recovery/streakFor":
                return HierarchyModule.CreateRecovery(io, env, clock).StreakFor(Arg(0), Arg(1), JsConvert.ToNumber(Arg(2)));
            case "lib-recovery/oneLine":
                return HierarchyModule.CreateRecovery(io, env, clock).OneLine(Arg(0), Arg(1));
            case "lib-recovery/errorKind":
                return HierarchyModule.CreateRecovery(io, env, clock).ErrorKind(Arg(0));
            case "lib-recovery/transcriptPathOk":
                return HierarchyModule.CreateRecovery(io, env, clock).TranscriptPathOk(Arg(0)) is { } real ? real : JsonValue.Null;
            case "lib-recovery/transcriptFailure":
                return HierarchyModule.CreateRecovery(io, env, clock).TranscriptFailure(Arg(0), SeenMap(c.Args.Count > 1 ? c.Args[1] : (JsonValue?)null));

            case "lib-context/readContext":
                return HierarchyModule.CreateContext(io, clock, roster).ReadContext(Arg(0), Arg(1));
            case "lib-context/ownsTeam":
                return HierarchyModule.CreateContext(io, clock, roster).OwnsTeam(Text(0)!, Arg(1), Arg(2));

            case "lib-hier/etaOf":
                return m.EtaOf(Arg(0));
            case "lib-hier/thresholdFor":
                return m.ThresholdFor(Arg(0));
            case "lib-hier/msgFilename":
                return m.MsgFilename(Arg(0));
            case "lib-hier/msgsDir":
                return m.MsgsDir(Text(0)!);
            case "lib-hier/archiveDir":
                return m.ArchiveDir(Text(0)!);
            case "lib-hier/specsDir":
                return m.SpecsDir(Text(0)!);
            case "lib-hier/peersPath":
                return m.PeersPath(Text(0)!);
            case "lib-hier/gatesPath":
                return m.GatesPath(Text(0)!);
            case "lib-hier/readJsonl":
                return m.ReadJsonl(Text(0)!);
            case "lib-hier/ensureHierarchyDir":
                return f.Bootstrap.EnsureHierarchyDir(f.Config.HierarchyDir(Text(0)));
            case "lib-hier/localIso":
                return m.LocalIso(Instant(Arg(0)));
            case "lib-hier/fmtAge":
                return m.FmtAge(Arg(0));
            case "lib-hier/ageSecOf":
                return m.AgeSecOf(Arg(0), Num(1));
            case "lib-hier/parseFrontmatter":
                return m.ParseFrontmatter(Arg(0));
            case "lib-hier/indexAnchors":
                return m.IndexAnchors(Text(0)!);
            case "lib-hier/messageHome":
                return m.MessageHome(Text(0)!, Arg(1)) is { } home ? home : JsonValue.Null;
            case "lib-hier/findByIdAndType":
                return m.FindByIdAndType(Text(0)!, Text(1)!, Text(2)!);
            case "lib-hier/createMessage":
                return m.CreateMessage(Text(0)!, Obj(1));
            case "lib-hier/responsePlan":
                return m.ResponsePlan(Arg(0));
            case "lib-hier/fillPlan":
                return m.FillPlan(Obj(0));
            case "lib-hier/responseFrontmatter":
                return m.ResponseFrontmatter(Obj(0), Instant(Arg(1)));
            case "lib-hier/rootOf":
                return m.RootOf(MapOf(Arg(0)), Text(1)!);
            case "lib-hier/listDownstreamDispatches":
                return m.ListDownstreamDispatches(Text(0)!);
            case "lib-hier/openExchanges":
                return m.OpenExchanges(Text(0)!, Arg(1), Arg(2) is { IsArray: true } ex ? ex.AsArray() : null);
            case "lib-hier/pipelineRunLive":
                return m.PipelineRunLive(Text(0));
            case "lib-hier/sweep":
                return m.Sweep(Text(0)!, Num(1), Num(2));
            case "lib-hier/exchangeAgeSec":
                return m.ExchangeAgeSec(Arg(0)!.Value, Num(1));
            case "lib-hier/validateRequestToken":
                return m.ValidateRequestToken(Arg(0), Text(1)!, Arg(2));
            case "lib-hier/validateResponseToken":
                return m.ValidateResponseToken(Arg(0), Text(1)!, Arg(2), Arg(3), Arg(4));
            case "lib-hier/missingIntent":
                return m.MissingIntent(Arg(0)!.Value);
            case "lib-hier/reviewIntentGap":
                return m.ReviewIntentGap(Text(0)!, Arg(1) is { IsMap: true } rv ? rv.AsObject() : null);
            case "lib-hier/reviewIntentReason":
                return m.ReviewIntentReason(Arg(0), Arg(1)!.Value.AsArray(), Arg(2));
            case "lib-hier/reportStatus":
                return m.ReportStatus(Text(0)!, Text(1)!);
            case "lib-hier/outcomeTokens":
                return m.OutcomeTokens(Arg(0));
            case "lib-hier/firstSection":
                return m.FirstSection(Arg(0));
            case "lib-hier/statusFollowUp":
                return m.StatusFollowUp(Arg(0));
            case "lib-hier/followUpRule":
                return m.FollowUpRule(Arg(0), Arg(1));
            case "lib-hier/followUpRules":
                return m.FollowUpRules(Arg(0));
            case "lib-hier/reportFollowUp":
                return m.ReportFollowUp(Arg(0));
            case "lib-hier/hasResponseToken":
                return m.HasResponseToken(Arg(0), Arg(1));
            case "lib-hier/responseLanded":
                return m.ResponseLanded(Text(0)!, Num(1));
            case "lib-hier/readGates":
                return f.Services.Roster.ReadGates(Text(0)!);
            case "lib-hier/sessionRouteRecord":
                return f.Services.Roster.SessionRouteRecord(Text(0)!, Arg(1));
            case "lib-hier/recordRoute":
                f.Services.Roster.RecordRoute(Text(0)!, Arg(1)!.Value, Arg(2)!.Value);
                return null;
            case "lib-hier/effectiveRoute":
                return f.Services.Roster.EffectiveRoute(Text(0)!, Arg(1) is { IsMap: true } rc ? rc.AsObject() : null, Arg(2));
            case "lib-hier/readRoster":
                return f.Services.Roster.ReadRoster(Text(0)!);
            case "lib-hier/latestRoster":
                return f.Services.Roster.LatestRoster(Text(0)!);
            case "lib-hier/attributedRoster":
                return f.Services.Roster.AttributedRoster(Text(0)!);
            case "lib-hier/upRecordFor":
                return f.Services.Roster.UpRecordFor(Text(0)!, Arg(1));
            case "lib-hier/recordLiveness":
                return WithoutNativeLivenessFields(f.Services.Roster.RecordLiveness(Arg(0)!.Value, Num(1)));
            case "lib-hier/attributedLiveness":
                return WithoutNativeLivenessFields(f.Services.Roster.AttributedLiveness(Arg(0)!.Value.AsArray(), Arg(1)));
            case "lib-hier/synthesizedPeerName":
                return f.Services.Roster.SynthesizedPeerName(Arg(0)!.Value);
            case "lib-hier/livePeerSlots":
                return Arg(1) is { IsMap: true } scope && scope.AsObject().Get("$export") is { IsText: true } export && export.AsString() == "NO_TEAM_SCOPE"
                    ? f.Services.Roster.LivePeerSlots(Text(0)!, null, true, Num(2))
                    : f.Services.Roster.LivePeerSlots(Text(0)!, Arg(1), false, Num(2));
            case "lib-hier/hasGate":
            {
                var wanted = Arg(1)!.Value.AsObject().Get("$predicate")!.Value.AsObject();
                var field = wanted["field"].AsString();
                var equals = wanted["equals"];
                return f.Services.Roster.HasGate(Text(0)!, record => record.Member(field) is { } value && value.Equals(equals));
            }

            case "lib-hier/roster":
                return f.Services.Roster.Roster(Text(0)!, Obj(1), Text(2)!, Num(3));
            case "lib-hier/describeInstance":
                return f.Services.Roster.DescribeInstance(Arg(0)!.Value);
            case "lib-hier/rosterLine":
                return f.Services.Roster.RosterLine(Obj(0));
            case "lib-hier/sessionModel":
                return f.Services.Roster.SessionModel(Arg(0), Text(1));
            case "lib-hier/cacheSessionModel":
                f.Services.Roster.CacheSessionModel(Text(0)!, Arg(1), Arg(2));
                return null;
            case "lib-hier/roleTier":
                return f.Services.Roster.RoleTier(Text(0)!, Obj(1), Arg(2));
            case "lib-hier/roleModelLabel":
                return f.Services.Roster.RoleModelLabel(Text(0)!, Obj(1));
            case "lib-hier/tierLine":
                return f.Services.Roster.TierLine(Obj(0), Arg(1));
            case "lib-hier/routeLine":
                return f.Services.Roster.RouteLine(Arg(0));
            case "lib-hier/buildStateBlock":
                return f.Services.Roster.BuildStateBlock(Text(0)!, Obj(1), Text(2)!, Arg(3), Arg(4), Arg(5), Num(6));
            case "lib-hier/msgRoles":
                return m.MsgRoles(Text(0));
            case "lib-hier/roleForPeerName":
                return f.Services.Roster.RoleForPeerName(Arg(0), Obj(1), Text(2)!) is { } role1 ? role1 : JsonValue.Null;
            case "lib-hier/roleForAnyPeerName":
                return f.Services.Roster.RoleForAnyPeerName(Text(0)!, Arg(1), Obj(2), Text(3)!) is { } role2 ? role2 : JsonValue.Null;
            case "lib-hier/realCwd":
                return m.RealCwd(Text(0)!);
            case "lib-hier/pathException":
                return m.PathException(Text(0)!, Text(1)!, Arg(2)!.Value);
            case "lib-hier/list":
                return RunOp(f.Services.StateDomains, "msgs", "list", Arg(0), Arg(2), request, null);
            case "lib-hier/index":
                return RunOp(f.Services.StateDomains, "msgs", "index", Arg(0), Arg(2), request, null);
            case "lib-hier/append":
                return RunOp(f.Services.StateDomains, Arg(2)?.Member("domain") is { IsText: true } ad ? ad.AsString() : "peers", "append", Arg(0), Arg(2), request, writer, f.Environment.AhHome);
            case "lib-hier/read":
                return RunOp(f.Services.StateDomains, Arg(2)?.Member("domain") is { IsText: true } rd ? rd.AsString() : "peers", "read", Arg(0), Arg(2), request, null);
            case "lib-hier/afterWrite":
            case "lib-hier/gatesAfterWrite":
            {
                var op = Text(0)!;
                var domainName = fn == "gatesAfterWrite" ? "gates" : op == "append" ? "peers" : "msgs";
                var call = new OpCall(op, JsonText.ToUtf8(Arg(1) ?? JsonValue.Null, 0), request);
                f.Services.StateDomains.Single(d => d.Name == domainName).AfterWrite(call, new OpResult(true, default));
                return null;
            }

            case "lib-hier/stateDirs":
            {
                var domain = f.Services.StateDomains.Single(d => d.Name == Text(0)!);
                var args = Arg(2);

                // Without a text `dir` Node names no directory; the engine then falls back to the request context's own.
                if (args?.Member("dir") is not { IsText: true })
                {
                    return new JsonArray();
                }

                return new JsonArray { domain.StateDirectory(request, JsonText.ToUtf8(args!.Value, 0)) };
            }

            case "lib-peer/peerPendingPath":
                return f.Services.Peers.PeerPendingPath();
            case "lib-peer/stripRef":
                return f.Services.Peers.StripRef(Text(0)!);
            case "lib-peer/parseSentinel":
                return f.Services.Peers.ParseSentinel(Arg(0));
            case "lib-peer/parseWrapper":
                return f.Services.Peers.ParseWrapper(Arg(0));
            case "lib-peer/unwrapCrossSession":
                return f.Services.Peers.UnwrapCrossSession(Arg(0));
            case "lib-peer/extractPendingRecord":
                return f.Services.Peers.ExtractPendingRecord(Arg(0), Arg(1));
            case "lib-peer/readPeerRecords":
                return f.Services.Core.ReadPeerRecords();
            case "lib-peer/latestByKey":
                return f.Services.Peers.LatestByKey(Arg(0)!.Value.AsArray());
            case "lib-peer/appendPeerRecord":
                f.Services.Peers.AppendPeerRecord(writer, Arg(0)!.Value);
                return null;
            case "lib-peer/cliRootAlreadyEmitted":
                return f.Services.Peers.CliRootAlreadyEmitted(Arg(0), Arg(1));
            case "lib-peer/recordCliRootEmitted":
                f.Services.Peers.RecordCliRootEmitted(Arg(0), Arg(1));
                return null;
            case "lib-peer/recordPeerRow":
                f.Services.Peers.RecordPeerRow(Arg(0)!.Value);
                return null;
            case "lib-peer/appendRelayRecord":
                return f.Services.Peers.AppendRelayRecord(Arg(0), Arg(1)!.Value, Instant(Arg(2)));
            case "lib-peer/pendingFor":
                return f.Services.Peers.PendingFor(Arg(0));
            case "lib-peer/appendTurnMarker":
                f.Services.Peers.AppendTurnMarker(Arg(0), Arg(1));
                return null;
            case "lib-peer/latestTurnMarker":
                return f.Services.Peers.LatestTurnMarker(Arg(0));
            case "lib-peer/appendDispatchRecord":
                f.Services.Peers.AppendDispatchRecord(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4));
                return null;
            case "lib-peer/appendReportRecord":
                f.Services.Peers.AppendReportRecord(Arg(0), Arg(1), Arg(2));
                return null;
            case "lib-peer/reportShown":
                return f.Services.Peers.ReportShown(Arg(0), Arg(1));
            case "lib-peer/dispatchRecordsFor":
                return f.Services.Peers.DispatchRecordsFor(Arg(0));
            case "lib-peer/latestDispatchRows":
                return f.Services.Peers.LatestDispatchRows(Arg(0));
            case "lib-peer/appendSubagentRecord":
                f.Services.Peers.AppendSubagentRecord(Arg(0), Arg(1), Arg(2));
                return null;
            case "lib-peer/latestSubagentRecords":
            {
                var entries = new JsonArray();
                foreach (var row in f.Services.Peers.LatestSubagentRecords(Arg(0)))
                {
                    entries.Add(JsonValue.FromArray(new JsonArray { row.Member("agent_id") is { } agent ? agent : new JsonObject { ["$undefined"] = 1 }, row }));
                }

                return new JsonObject { ["$map"] = JsonValue.FromArray(entries) };
            }

            case "lib-peer/hasInflightSubagent":
                return f.Services.Peers.HasInflightSubagent(Arg(0), Num(1));
            case "lib-peer/dispatchOrigin":
            {
                var entries = new JsonArray();
                foreach (var (key, value) in f.Services.Peers.DispatchOrigin(Arg(0)!.Value.AsArray()))
                {
                    entries.Add(JsonValue.FromArray(new JsonArray { key, value }));
                }

                return new JsonObject { ["$map"] = JsonValue.FromArray(entries) };
            }

            case "lib-peer/targetSatisfiesRecord":
                return f.Services.Peers.TargetSatisfiesRecord(Arg(0), Arg(1)!.Value);
            case "lib-peer/unconsumedWatchEvents":
                return f.Services.Peers.UnconsumedWatchEvents(Arg(0));
            case "lib-peer/stateDirs":
                return new JsonArray { f.Services.StateDomains.Single(d => d.Name == "peer-records").StateDirectory(request) };
            case "lib-peer/append":
                return RunOp(f.Services.StateDomains, "peer-records", "append", Arg(0), Arg(2), request, writer);
            case "lib-peer/read":
                return RunOp(f.Services.StateDomains, "peer-records", "read", Arg(0) ?? new JsonObject(), null, request, null);
            case "lib-peer/MAX_NUDGES":
                return HierarchyModule.MaxNudges;
            case "lib-peer/SUBAGENT_INFLIGHT_TTL_MS":
                return HierarchyModule.SubagentInflightTtlMs;

            case "lib-decisions/DECISION_ITEM_CAP":
                return HierarchyModule.DecisionItemCap;
            case "lib-decisions/DECISION_RUN_CAP":
                return HierarchyModule.DecisionRunCap;
            case "lib-decisions/DECISION_INPUT_MAX":
                return HierarchyModule.DecisionInputMax;
            case "lib-decisions/MERGE_METHODS":
            {
                var methods = new JsonArray();
                foreach (var method in HierarchyModule.MergeMethods)
                {
                    methods.Add(method);
                }

                return methods;
            }

            case "lib-decisions/decisionLogPath":
                return f.Services.Decisions.DecisionLogPath(Text(0)!, Text(1)!);
            case "lib-decisions/openRunAnchor":
                return f.Services.Decisions.OpenRunAnchor(Text(0)!, Arg(1), Arg(2) is { IsArray: true } listed ? listed.AsArray() : null);
            case "lib-decisions/knownRun":
                return f.Services.Decisions.KnownRun(Text(0)!, Arg(1));
            case "lib-decisions/readDecisions":
                return f.Services.Decisions.ReadDecisions(Text(0)!);
            case "lib-decisions/decisionSummary":
                return f.Services.Decisions.DecisionSummary(Arg(0)!.Value.AsArray(), Arg(1));
            case "lib-decisions/questionKey":
                return f.Services.Decisions.QuestionKey(Arg(0), Arg(1));
            case "lib-decisions/appendDecision":
                return f.Services.Decisions.AppendDecision(writer, Text(0)!, Arg(1), Arg(2), Text(3));
            case "lib-decisions/constraintLines":
                return f.Services.Decisions.ConstraintLines(Arg(0));
            case "lib-decisions/liveRun":
                return f.Services.Decisions.LiveRun(Text(0));
            case "lib-decisions/mergeCommand":
                return f.Services.Decisions.MergeCommand(Arg(0), Arg(1), Arg(2), Arg(3));
            case "lib-decisions/parseMergeForm":
                return f.Services.Decisions.ParseMergeForm(Arg(0));
            case "lib-decisions/appendMergeRecord":
                f.Services.Decisions.AppendMergeRecord(writer, Text(0)!, Arg(1)!.Value, Text(2));
                return null;
            case "lib-decisions/readDecisionInput":
                return f.Services.Decisions.ReadDecisionInput(Text(0)!, Text(1)!, Text(2)!);
            case "lib-decisions/runLiveness":
                return f.Services.Decisions.RunLiveness(Text(0));
            case "lib-decisions/append":
                return RunOp(f.Services.StateDomains, "decisions", "append", Arg(0), Arg(2), request, writer);
            case "lib-decisions/add":
                return RunOp(f.Services.StateDomains, "decisions", "add", Arg(0), Arg(2), request, writer);
            case "lib-decisions/read":
                return RunOp(f.Services.StateDomains, "decisions", "read", Arg(0), null, request, null);
            case "lib-decisions/stateDirs":
            {
                var args = Arg(2);
                if (args?.Member("path") is not { IsText: true })
                {
                    return new JsonArray();
                }

                var domain = f.Services.StateDomains.Single(d => d.Name == "decisions");
                return new JsonArray { domain.StateDirectory(request, JsonText.ToUtf8(args!.Value, 0)) };
            }

            case "lib-decisions/afterWrite":
            {
                var call = new OpCall(Text(0)!, JsonText.ToUtf8(Arg(1) ?? JsonValue.Null, 0), request);
                var outcome = new OpResult(true, JsonText.ToUtf8(Arg(2) ?? JsonValue.Null, 0));
                f.Services.StateDomains.Single(d => d.Name == "decisions").AfterWrite(call, outcome);
                return null;
            }

            case "lib-status/STATUS_SCHEMA":
                return f.Services.Status.StatusSchema;
            case "lib-status/STATUS_SIZE_CAP":
                return f.Services.Status.StatusSizeCap;
            case "lib-status/ROUND_CAP":
                return f.Services.Status.RoundCap;
            case "lib-status/clean":
                return f.Services.Status.Clean(Arg(0), Arg(1)) is { } cleaned ? cleaned : JsonValue.Null;
            case "lib-status/readActivity":
                return f.Services.Status.ReadActivity(Text(0)!, Text(1)!);
            case "lib-status/readActivityRecord":
                return f.Services.Status.ReadActivityRecord(Text(0)!, Arg(1));
            case "lib-status/recordActivity":
            {
                // per-dir-files-to-db S2: the record is written through activity.record, its only writer
                var fields = Arg(2)!.Value.AsObject().Clone();
                fields["dir"] = Arg(0) ?? JsonValue.Null;
                fields["subject"] = Arg(1) ?? JsonValue.Null;
                return RunOp(f.Services.StateDomains, "activity", "record", fields, null, request, writer, f.Environment.AhHome).Member("written");
            }

            case "lib-status/clearActivity":
                return RunOp(f.Services.StateDomains, "activity", "clear", new JsonObject { ["dir"] = Arg(0) ?? JsonValue.Null, ["subject"] = Arg(1) ?? JsonValue.Null }, null, request, writer, f.Environment.AhHome).Member("cleared");
            case "lib-status/sweepActivity":
                return RunOp(f.Services.StateDomains, "activity", "sweep", new JsonObject { ["dir"] = Arg(0) ?? JsonValue.Null, ["cutoff_ms"] = CutoffOf(Arg(1)) }, null, request, writer, f.Environment.AhHome).Member("removed");
            case "lib-status/memberFills":
                return f.Services.Status.MemberFills(Text(0)!, Arg(1)!.Value);
            case "lib-status/describeMembers":
                return f.Services.Status.DescribeMembers(Text(0)!, Arg(1)!.Value, Arg(2)!.Value.AsArray());
            case "lib-status/computeStatus":
                return f.Services.Status.ComputeStatus(Text(0), Num(1), Text(2), Arg(3) is { IsBool: true } enabled ? enabled.AsBool() : null);
            case "lib-status/record":
                return RunOp(f.Services.StateDomains, "activity", "record", Arg(0), null, request, writer, f.Environment.AhHome);
            case "lib-status/clear":
                return RunOp(f.Services.StateDomains, "activity", "clear", Arg(0), null, request, writer, f.Environment.AhHome);
            case "lib-status/sweep":
                return RunOp(f.Services.StateDomains, "activity", "sweep", Arg(0), null, request, writer, f.Environment.AhHome);
            case "lib-status/stateDirs":
            {
                var args = Arg(2);
                if (args?.Member("dir") is not { IsText: true })
                {
                    return new JsonArray();
                }

                var wanted = Text(0) is "status" or "session-pids" ? Text(0) : "activity";
                var domain = f.Services.StateDomains.Single(d => d.Name == wanted);
                return new JsonArray { domain.StateDirectory(request, JsonText.ToUtf8(args!.Value, 0)) };
            }

            case "lib-status/activityAfterWrite":
            {
                var call = new OpCall(Text(0)!, JsonText.ToUtf8(Arg(1) ?? JsonValue.Null, 0), request);
                var outcome = new OpResult(true, JsonText.ToUtf8(Arg(2) ?? JsonValue.Null, 0));
                f.Services.StateDomains.Single(d => d.Name == "activity").AfterWrite(call, outcome);
                return null;
            }

            case "lib-status/refreshStatus":
            case "lib-status/refreshStatusSync":
                f.Services.Status.RefreshStatus(Text(0) ?? string.Empty, Arg(1) is { IsMap: true } refreshOptions ? refreshOptions.AsObject() : null);
                return null;
            case "lib-status/recordActivityRouted":
                return f.Services.Status.RecordActivityRouted(
                    Text(0) ?? string.Empty,
                    Arg(1) ?? JsonValue.Null,
                    Arg(2) is { IsMap: true } routedFields ? routedFields.AsObject() : new JsonObject(),
                    Arg(3)?.Member("hook") is { IsBool: true } recordHook && recordHook.AsBool());
            case "lib-status/clearActivityRouted":
                f.Services.Status.ClearActivityRouted(Text(0) ?? string.Empty, Arg(1) ?? JsonValue.Null, Arg(2)?.Member("hook") is { IsBool: true } clearHook && clearHook.AsBool());
                return null;
            case "lib-status/sweepActivityRouted":
                return f.Services.Status.SweepActivityRouted(Text(0) ?? string.Empty, CutoffOf(Arg(1)), Arg(2)?.Member("hook") is { IsBool: true } sweepHook && sweepHook.AsBool());
            case "lib-status/plainStatus":
                return f.Services.Status.PlainStatus(Arg(0)!.Value, Num(1) ?? double.NaN);

            case "lib-hier/CHECKIN_CADENCE":
            {
                var cadence = new JsonArray();
                foreach (var gap in HierarchyModule.CheckinCadence)
                {
                    cadence.Add(gap);
                }

                return cadence;
            }

            case "lib-hier/ETAS":
                return Strings(HierarchyModule.Etas);
            case "lib-hier/ETA_THRESHOLD_SEC":
            {
                var thresholds = new JsonObject();
                foreach (var (eta, seconds) in HierarchyModule.EtaThresholdSeconds)
                {
                    thresholds[eta] = seconds;
                }

                return thresholds;
            }

            case "lib-hier/FILL_BODY_MAX":
                return HierarchyModule.FillBodyMax;
            case "lib-hier/LANDED_QUIET_MS":
                return HierarchyModule.LandedQuietMs;
            case "lib-hier/MSG_TYPES":
                return Strings(HierarchyModule.MsgTypes);
            case "lib-hier/NOTE_RULE":
                return HierarchyModule.NoteRule;
            case "lib-hier/PEERS_FILE":
                return HierarchyModule.PeersFile;
            case "lib-hier/PIPELINE_ANCHOR_SLUG":
                return HierarchyModule.PipelineAnchorSlug;
            case "lib-hier/REASONS":
                return Strings(HierarchyModule.Reasons);
            case "lib-hier/REQUEST_KEYS":
                return Strings(HierarchyModule.RequestKeys);
            case "lib-hier/RESPONSE_KEYS":
                return Strings(HierarchyModule.ResponseKeys);
            case "lib-hier/ROSTER_FRESH_SEC":
                return HierarchyModule.RosterFreshSeconds;
            case "lib-hier/SELF_STATE":
            {
                var state = new JsonObject();
                foreach (var (hook, activity) in HierarchyModule.SelfState)
                {
                    state[hook] = activity;
                }

                return state;
            }

            case "lib-hier/SWEEP_DAYS":
                return HierarchyModule.SweepDays;
            case "lib-hier/TOOL_WRITE_INTERVAL_SEC":
                return HierarchyModule.ToolWriteIntervalSeconds;
            case "lib-hier/TEAM_GUIDE":
                return f.Services.Messages.TeamGuide;
            case "lib-hier/ID_RE":
                return f.Services.Messages.IsMessageId(Text(0)!);
            case "lib-hier/SLUG_RE":
                return f.Services.Messages.IsSlug(Text(0)!);
            case "lib-hier/MSG_TOKEN_RE":
                return f.Services.Core.ExtractMsgToken(Text(0)) is not null;
            case "lib-hier/appendRosterRecord":
                _ = RunOp(f.Services.StateDomains, "peers", "append", new JsonObject { ["dir"] = Text(0)!, ["rec"] = Arg(1) ?? JsonValue.Null }, null, request, writer, f.Environment.AhHome);
                return null;
            case "lib-hier/healFalseDown":
                return f.Services.Roster.HealFalseDown(Text(0)!, Arg(1), Arg(2));

            case "lib-usage/CACHE_FILE":
                return HierarchyModule.CreateUsage(io, env).CacheFile;
            case "lib-usage/PRUNE_AT":
                return HierarchyModule.CreateUsage(io, env).PruneAt;
            case "lib-usage/PRUNE_TO":
                return HierarchyModule.CreateUsage(io, env).PruneTo;
            case "lib-recovery/RESUME_PARAGRAPH":
                return HierarchyModule.CreateRecovery(io, env, clock).ResumeParagraph;
            case "lib-recovery/RETRYABLE":
                return Strings(HierarchyModule.CreateRecovery(io, env, clock).Retryable);
            case "lib-recovery/SAME_FAILURE_MS":
                return HierarchyModule.CreateRecovery(io, env, clock).SameFailureMs;
            case "lib-recovery/STREAK_WINDOW_MS":
                return HierarchyModule.CreateRecovery(io, env, clock).StreakWindowMs;
            case "lib-context/TAIL_BYTES":
                return HierarchyModule.CreateContext(io, clock, roster).TailBytes;

            default:
                throw new NotSupportedException("no native dispatcher for " + lib + "/" + fn);
        }
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    /// <summary>A cutoff as JavaScript compares it against a modification time: a number, a numeric string, else never less than anything.</summary>
    private static double CutoffOf(JsonValue? v) => v switch
    {
        { IsNumber: true } n => n.AsNumber(),
        { IsText: true } t when double.TryParse(t.AsString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        { IsNull: true } => 0,
        _ => double.NaN,
    };

    /// <summary>A table's <c>{"$map": [[k, v], ...]}</c> as a dictionary keyed by text.</summary>
    private static Dictionary<string, JsonValue> MapOf(JsonValue? raw)
    {
        var map = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        if (raw is { IsMap: true } m && m.AsObject().Get("$map") is { IsArray: true } entries)
        {
            foreach (var entry in entries.AsArray())
            {
                map[entry.AsArray()[0].AsString()] = entry.AsArray()[1];
            }
        }

        return map;
    }

    /// <summary>The <c>Map</c> a table writes as <c>{"$map": [[k, v], ...]}</c> (values are stamps), else null.</summary>
    private static Dictionary<string, string>? SeenMap(JsonValue? raw)
    {
        if (raw is not { IsMap: true } m || m.AsObject().Get("$map") is not { IsArray: true } entries)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.AsArray())
        {
            map[entry.AsArray()[0].AsString()] = entry.AsArray()[1].AsString();
        }

        return map;
    }

    /// <summary>Runs an op body as the engine would: args JSON, the request context, the call's op id and lock flag from the case's meta.</summary>
    internal static JsonValue RunOp(IReadOnlyList<IStateDomain> domains, string domain, string op, JsonValue? args, JsonValue? meta, RequestContext request, IStoreWriteContext? writer, string? ahHome = null)
    {
        var d = domains.Single(x => x.Name == domain);
        var operation = d.Ops.Single(o => o.Name == op);
        var opId = meta?.Member("opId") is { IsText: true } id ? id.AsString() : null;
        var locked = meta?.Member("locked") is not { IsBool: true } l || l.AsBool();
        var json = JsonText.ToUtf8(args ?? JsonValue.Null, 0);
        var call = new OpCall(op, json, request, opId, locked);
        OpResult result;
        if (d.DbOnly && operation.Kind == OpKind.Write && operation.DbBody is { } dbBody)
        {
            var home = ahHome ?? throw new InvalidOperationException("a db-only write needs AhHome");
            var outcome = Ah.StateDb.StateDbModule.CreateOpRunner(home).Run(new DbOpRequest(domain, call.OperationId ?? Guid.NewGuid().ToString(), "direct", tx => dbBody(call, tx), DbOnly: true));
            result = outcome.Status == DbOpStatus.Done ? new OpResult(true, outcome.ResultJson) : new OpResult(false, default, outcome.Error);
        }
        else
        {
            result = operation.Body(call, operation.Kind == OpKind.Write ? writer : null);
        }

        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error);
        }

        return JsonText.Parse(result.ResultJson.Span);
    }
}
