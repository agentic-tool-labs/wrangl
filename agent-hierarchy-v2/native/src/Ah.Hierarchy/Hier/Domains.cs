using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>Shared shape of the domains below: the lock and executed-op log sit beside the files they guard, in the pool dir an op targets.</summary>
internal abstract class HierDomain(HierLib lib, string name, params string[] stores) : IStateDomain
{
    protected HierLib Lib { get; } = lib;

    public string Name { get; } = name;

    public IReadOnlyList<string> StoreNames { get; } = stores;

    public virtual bool HomeScoped => false;

    public virtual bool DbOnly => false;

    public virtual string StateDirectory(RequestContext context) => NodePath.Join(context.HierarchyDirectory, ".state-" + Name);

    /// <summary>The lock and executed-op log sit in the pool dir the op's own args name (<c>dir</c>).</summary>
    public virtual string StateDirectory(RequestContext context, ReadOnlyMemory<byte> argsJson) =>
        JsonText.TryParse(argsJson.Span, out var args) && args.Member("dir") is { IsText: true } dir
            ? NodePath.Join(dir.AsString(), ".state-" + Name)
            : StateDirectory(context);

    public abstract IReadOnlyList<StateOp> Ops { get; }

    public virtual void AfterWrite(OpCall operation, OpResult result)
    {
    }

    protected static StateOp Write(string name, OpStyle style, bool lockOptional, Func<OpCall, IStoreWriteContext?, OpResult> body, params string[] pathArgs) =>
        new(name, OpKind.Write, style, lockOptional, null, true, body, PathArgs: pathArgs);

    protected static StateOp Read(string name, Func<OpCall, IStoreWriteContext?, OpResult> body, params string[] pathArgs) =>
        new(name, OpKind.Read, null, false, null, true, body, PathArgs: pathArgs);

    /// <summary>A read op the daemon's state route refuses: only the in-process reader of the view reaches it.</summary>
    protected static StateOp InternalRead(string name, Func<OpCall, IStoreWriteContext?, OpResult> body) =>
        new(name, OpKind.Read, null, false, null, false, body);

    /// <summary>A write op the daemon's state route refuses: only in-process callers (the CLI and the hooks) reach it.</summary>
    protected static StateOp InternalWrite(string name, OpStyle style, Func<OpCall, IStoreWriteContext?, OpResult> body, params string[] pathArgs) =>
        new(name, OpKind.Write, style, false, null, false, body, PathArgs: pathArgs);

    protected static string? DirOf(OpCall operation) =>
        JsonText.TryParse(operation.ArgsJson.Span, out var args) && args.Member("dir") is { IsText: true } d ? d.AsString() : null;

    protected void Refresh(string? dir)
    {
        if (dir is not null)
        {
            Lib.RefreshStatus(dir);
        }
    }

    /// <summary>The write's status delta (§3.8 item 3), with today's refresh of <paramref name="dir"/> while no status dir with entries lists it.</summary>
    protected void Delta(OpCall operation, OpResult result, string? dir) => Lib.AfterStatusWrite(Name, operation.Op, operation.ArgsJson, result.ResultJson, dir);
}

[StateDomain("msgs", "msgs", "hier-gitignore")]
internal sealed class MsgsDomain(HierLib lib) : HierDomain(lib, "msgs", "msgs", "hier-gitignore")
{
    internal static readonly TimeSpan CreateBusyAfter = TimeSpan.FromSeconds(1);

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("create", OpStyle.Replace, false, NotCutOver, "path", "dir") with { BusyAfter = CreateBusyAfter, DbBody = StatusDirtyMark.Body(Name, (call, tx) => OpArgs.Run(call, args => Lib.MsgsCreateDb(call, tx, args))) },
        Write("settle", OpStyle.Replace, false, NotCutOver, "path", "dir") with { DbBody = StatusDirtyMark.Body(Name, (call, tx) => OpArgs.Run(call, args => Lib.MsgsSettleDb(call, tx, args))) },
        Write("fill", OpStyle.Replace, false, NotCutOver, "path", "dir") with { DbBody = StatusDirtyMark.Body(Name, (call, tx) => OpArgs.Run(call, args => Lib.MsgsFillDb(call, tx, args))) },
        Write("retire", OpStyle.Replace, false, NotCutOver, "path", "dir") with { DbBody = StatusDirtyMark.Body(Name, (call, tx) => OpArgs.Run(call, args => Lib.MsgsRetireDb(call, tx, args))) },
        Write("archive", OpStyle.Replace, false, NotCutOver, "dir") with { DbBody = StatusDirtyMark.Body(Name, (call, tx) => OpArgs.Run(call, args => Lib.MsgsArchiveDb(call, tx, args))) },
        Write("seal", OpStyle.Replace, false, NotCutOver, "path", "dir") with { DbBody = StatusDirtyMark.Body(Name, (call, tx) => OpArgs.Run(call, args => Lib.MsgsSealDb(call, tx, args))) },
        Read("list", (call, _) => OpArgs.Run(call, args => Lib.MsgsListOp(args)), "dir"),
        Read("index", (call, _) => OpArgs.Run(call, args => Lib.MsgsIndexOp(args)), "dir"),
        InternalRead("open-exchanges", (call, _) => OpArgs.Run(call, args => Lib.MsgsOpenExchangesOp(args))),
        InternalRead("trees", (call, _) => OpArgs.Run(call, args => Lib.MsgsTreesOp(args))),
        InternalRead("version", (call, _) => OpArgs.Run(call, _ => Lib.MsgsVersionOp())),
        InternalRead("changed", (call, _) => OpArgs.Run(call, args => Lib.MsgsChangedOp(args))),

        // internal: the view's read model enumerates a pool and reads one exchange; not served on the route surface
        new("keys", OpKind.Read, null, false, null, false, (call, _) => OpArgs.Run(call, args => Lib.MsgsKeysOp(args)), PathArgs: ["dir"]),
        new("read", OpKind.Read, null, false, null, false, (call, _) => OpArgs.Run(call, args => Lib.MsgsReadOp(args), handOut: true), PathArgs: ["dir"]),
        new("heard", OpKind.Read, null, false, null, false, (call, _) => OpArgs.Run(call, args => Lib.MsgsHeardOp(args)), PathArgs: ["dir"]),
    ];

    /// <summary>The body of a message write in a home still on files: there is no file body, the database body is the write.</summary>
    private OpResult NotCutOver(OpCall call, IStoreWriteContext? writer) =>
        OpArgs.Run(call, _ => throw new StoreUnavailableException(Lib.Core.MsgsNotReady() ?? HierarchyCore.StillOnFilesMessage));

    // archive and seal publish only their delete (ruling B2): with no entries yet they refresh nothing, as before
    public override void AfterWrite(OpCall operation, OpResult result) =>
        Delta(operation, result, operation.Op is "archive" or "seal" ? null : DirOf(operation));
}

[StateDomain("peers", "peers")]
internal sealed class PeersDomain(HierLib lib) : HierDomain(lib, "peers", "peers")
{
    // per-dir-files-to-db S7: the roster lives only in the roster_records table
    public override bool DbOnly => true;

    private static OpResult DbOnlyBody(OpCall call, IStoreWriteContext? writer) => new(false, default, $"peers.{call.Op} is db-only", "op_failed");

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("append", OpStyle.Append, true, DbOnlyBody, "dir") with { DbBody = Lib.PeersAppendDb },
        Read("read", (call, _) => OpArgs.Run(call, args => Lib.PeersReadOp(args)), "dir"),
        Read("latest", (call, _) => OpArgs.Run(call, args => Lib.PeersLatestOp(args)), "dir"),

        // internal: appends one row (JSON text) as given, for seeding a test's roster; the daemon's state route refuses it
        InternalWrite("put", OpStyle.Append, DbOnlyBody, "dir") with { DbBody = HierLib.PeersPutDb },
    ];

    // a seeded row refreshes nothing, as a planted peers.jsonl line did not (and as gates.put does not)
    public override void AfterWrite(OpCall operation, OpResult result)
    {
        if (operation.Op != "put")
        {
            Delta(operation, result, DirOf(operation));
        }
    }
}

[StateDomain("gates", "gates")]
internal sealed class GatesDomain(HierLib lib) : HierDomain(lib, "gates", "gates")
{
    // per-dir-files-to-db S3: the log lives only in the gates table
    public override bool DbOnly => true;

    private static OpResult DbOnlyBody(OpCall call, IStoreWriteContext? writer) => new(false, default, $"gates.{call.Op} is db-only", "op_failed");

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("append", OpStyle.Append, true, DbOnlyBody, "dir") with { DbBody = Lib.GatesAppendDb },
        Read("read", (call, _) => OpArgs.Run(call, args => Lib.GatesReadOp(args)), "dir"),

        // internal: appends one row (JSON text) as given, for seeding a test's log; the daemon's state route refuses it
        InternalWrite("put", OpStyle.Append, DbOnlyBody, "dir") with { DbBody = Lib.GatesPutDb },
    ];

    public override void AfterWrite(OpCall operation, OpResult result)
    {
        if (JsonText.TryParse(operation.ArgsJson.Span, out var args) && args.Member("rec")?.Member("type") is { IsText: true } t && t.AsString() == "liveness-nudge")
        {
            Delta(operation, result, DirOf(operation));
        }
    }
}

[StateDomain("peer-records", "peer-records")]
internal sealed class PeerRecordsDomain(HierLib lib, string homeStateDirectory) : HierDomain(lib, "peer-records", "peer-records")
{
    public override bool HomeScoped => true;

    public override string StateDirectory(RequestContext context) => homeStateDirectory;

    public override string StateDirectory(RequestContext context, ReadOnlyMemory<byte> argsJson) => homeStateDirectory;

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("append", OpStyle.Append, true, (call, writer) => OpArgs.Run(call, args => Lib.PeerRecordsAppendOp(writer!, args, call.OperationId))) with { DbBody = StatusDirtyMark.Body(Name, PeerRecordsDb.Append) },
        Write("compact", OpStyle.Replace, false, (call, writer) => OpArgs.Run(call, args => Lib.PeerRecordsCompactOp(writer!, args))) with { DbBody = StatusDirtyMark.Body(Name, PeerRecordsDb.Compact) },
        Read("read", (call, _) => OpArgs.Run(call, _ => Lib.CoreRecords())),
    ];

    // a dispatch record carries the dispatch time of a request: its origin entry (§3.8 item 3, peers row)
    public override void AfterWrite(OpCall operation, OpResult result) => Delta(operation, result, null);
}

[StateDomain("qa", "qa")]
internal sealed class QaDomain(HierLib lib, string homeStateDirectory) : HierDomain(lib, "qa", "qa")
{
    public override bool HomeScoped => true;

    public override string StateDirectory(RequestContext context) => homeStateDirectory;

    public override string StateDirectory(RequestContext context, ReadOnlyMemory<byte> argsJson) => homeStateDirectory;

    // b397: the queue lives only in the qa_jobs table
    public override bool DbOnly => true;

    private static OpResult DbOnlyBody(OpCall call, IStoreWriteContext? writer) => new(false, default, $"qa.{call.Op} is db-only", "op_failed");

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("append", OpStyle.Append, false, DbOnlyBody) with { DbBody = HierLib.QaAppendDb },
        Write("compact", OpStyle.Replace, false, DbOnlyBody) with { DbBody = HierLib.QaCompactDb },
        Read("read", (call, _) => OpArgs.Run(call, _ => Lib.QaReadOp())),
    ];
}

[StateDomain("jobs", "jobs")]
internal sealed class JobsDomain(HierLib lib) : HierDomain(lib, "jobs", "jobs")
{
    // per-dir-files-to-db S8: the records live only in the jobs table
    public override bool DbOnly => true;

    private static OpResult DbOnlyBody(OpCall call, IStoreWriteContext? writer) => new(false, default, $"jobs.{call.Op} is db-only", "op_failed");

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("put", OpStyle.Replace, false, DbOnlyBody, "dir") with { DbBody = HierLib.JobsPutDb },
        Write("prune", OpStyle.Replace, false, DbOnlyBody, "dir", "cutoff_ms") with { DbBody = Lib.JobsPruneDb },
    ];
}

[StateDomain("decisions", "decisions")]
internal sealed class DecisionsDomain(HierLib lib) : HierDomain(lib, "decisions", "decisions")
{
    /// <summary>The lock sits in the pool dir of the decision log the op names (<c>path</c>).</summary>
    public override string StateDirectory(RequestContext context, ReadOnlyMemory<byte> argsJson)
    {
        if (!JsonText.TryParse(argsJson.Span, out var args) || args.Member("path") is not { IsText: true } path)
        {
            return StateDirectory(context);
        }

        // the asks log has its own lock and executed-op log: an ask never waits behind a decision write
        return HierLib.IsAsksLog(path.AsString())
            ? NodePath.Join(NodePath.Dirname(path.AsString()), ".state-asks")
            : NodePath.Join(HierLib.PoolOfLog(path.AsString()), ".state-decisions");
    }

    public override IReadOnlyList<StateOp> Ops =>
    [
        // per-dir-files-to-db S9: append and add run in the database while mode.decisions is db; the asks log stays a file
        Write("append", OpStyle.Append, true, (call, writer) => OpArgs.Run(call, args => Lib.DecisionAppendOp(writer!, args, call.OperationId)), "path") with { DbBody = Lib.DecisionAppendDb },
        // per-dir-files-to-db S10: still never served on the daemon route (consent guard G1); its database body runs in-process, under mode.asks
        new StateOp("ask", OpKind.Write, OpStyle.Append, true, null, false, (call, writer) => OpArgs.Run(call, args => Lib.AsksAppendOp(writer!, args, call.OperationId)), PathArgs: ["path"], ModeKey: "asks", DbBodyWithFiles: Lib.AsksAppendDb),
        Write("add", OpStyle.Append, false, (call, writer) => OpArgs.Run(call, args => Lib.DecisionAddOp(writer!, args, call.OperationId)), "path") with { DbBody = Lib.DecisionAddDb },
        Read("read", (call, _) => OpArgs.Run(call, args => Lib.DecisionReadOp(args)), "path"),
        InternalRead("pipeline-run", (call, _) => OpArgs.Run(call, args => Lib.DecisionsPipelineRunOp(args))),
        InternalRead("qa-log", (call, _) => OpArgs.Run(call, args => Lib.QaLogReadOp(args))),
        InternalRead("qa-thread", (call, _) => OpArgs.Run(call, args => Lib.QaThreadReadOp(args))),

        // internal: enumerates the keys of the lane for the view's read model; not served on the route surface
        new("keys", OpKind.Read, null, false, null, false, (call, _) => OpArgs.Run(call, args => Lib.DecisionKeysOp(args)), PathArgs: ["dir"]),
    ];

    public override void AfterWrite(OpCall operation, OpResult result)
    {
        if (!JsonText.TryParse(result.ResultJson.Span, out var r) || r.IsNull || r.Member("refused") is not null)
        {
            return;
        }

        if (JsonText.TryParse(operation.ArgsJson.Span, out var args) && args.Member("path") is { IsText: true } p && !HierLib.IsAsksLog(p.AsString()))
        {
            Delta(operation, result, HierLib.PoolOfLog(p.AsString()));
        }
    }
}

[StateDomain("activity", "activity")]
internal sealed class ActivityDomain(HierLib lib) : HierDomain(lib, "activity", "activity")
{
    // per-dir-files-to-db S2: the records live only in the activity table
    public override bool DbOnly => true;

    private static OpResult DbOnlyBody(OpCall call, IStoreWriteContext? writer) => new(false, default, $"activity.{call.Op} is db-only", "op_failed");

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("record", OpStyle.Replace, false, DbOnlyBody, "dir", "mirror_dir") with { DbBody = Lib.ActivityRecordDb },
        Write("clear", OpStyle.Replace, false, DbOnlyBody, "dir", "mirror_dir") with { DbBody = Lib.ActivityClearDb },
        Write("mark", OpStyle.Replace, false, DbOnlyBody, "dir", "mirror_dir") with { DbBody = Lib.ActivityMarkDb },
        Write("unmark", OpStyle.Replace, false, DbOnlyBody, "dir", "mirror_dir") with { DbBody = Lib.ActivityUnmarkDb },
        Write("sweep", OpStyle.Replace, false, DbOnlyBody, "dir") with { DbBody = Lib.ActivitySweepDb },

        // internal: stores one record as given, for seeding a test's records; the daemon's state route refuses it
        InternalWrite("put", OpStyle.Replace, DbOnlyBody, "dir") with { DbBody = Lib.ActivityPutDb },
        InternalRead("read", (call, _) => OpArgs.Run(call, args => Lib.ActivityReadOp(args))),

        // internal: enumerates the keys of the lane for the view's read model; not served on the route surface
        new("keys", OpKind.Read, null, false, null, false, (call, _) => OpArgs.Run(call, args => Lib.ActivityKeysOp(args)), PathArgs: ["dir"]),
    ];

    public override void AfterWrite(OpCall operation, OpResult result)
    {
        if (!JsonText.TryParse(result.ResultJson.Span, out var r))
        {
            return;
        }

        var changed = operation.Op switch
        {
            "record" => r.Member("written") is { IsTruthy: true },
            "clear" => r.Member("cleared") is { IsTruthy: true },
            "sweep" => r.Member("removed") is { IsTruthy: true },
            _ => false,
        };
        if (changed)
        {
            Delta(operation, result, DirOf(operation));
        }
    }
}

[StateDomain("status", "status")]
internal sealed class StatusDomain(HierLib lib) : HierDomain(lib, "status", "status")
{
    // files-off-disk F3a: the document is served from status_entries; no status.json, no .state-status/
    public override bool DbOnly => true;

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("refresh", OpStyle.Replace, false, (call, _) => new OpResult(false, default, "status.refresh is db-only", "op_failed"), "dir") with { DbBody = Lib.RefreshDb },
        InternalRead("compute", (call, _) => OpArgs.Run(call, args => Lib.StatusComputeOp(args))),

        // internal: the status document for the view's read model; not served on the route surface
        // its Value is the compute's witness (not the document): the daemon's own reader keeps it beside the document it parses (b459 C1)
        new("read", OpKind.Read, null, false, null, false, (call, _) =>
        {
            StatusWitness? witness = null;
            var result = OpArgs.Run(call, args => Lib.StatusReadOp(args, out witness));
            return result.Ok && witness is not null ? result with { Value = witness } : result;
        }),
        InternalRead("stamp", (call, _) => OpArgs.Run(call, args => Lib.StatusStampOp(args))),
    ];
}

[StateDomain("session-pids", "session-pids")]
internal sealed class SessionPidsDomain(HierLib lib) : HierDomain(lib, "session-pids", "session-pids")
{
    // per-dir-files-to-db S1: the rows live only in session_pids
    public override bool DbOnly => true;

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("append", OpStyle.Append, false, (call, _) => new OpResult(false, default, "session-pids.append is db-only", "op_failed"), "dir") with { DbBody = Lib.AppendSessionPidDb },
        InternalRead("pids", (call, _) => OpArgs.Run(call, args => Lib.SessionPidsReadOp(args))),
        InternalRead("of-pid", (call, _) => OpArgs.Run(call, args => Lib.SessionsOfPidOp(args))),
    ];

    // new (§3.8 item 3): a session-pid row is a status input; the hooks' own refresh after the append still runs
    public override void AfterWrite(OpCall operation, OpResult result)
    {
        if (JsonText.TryParse(result.ResultJson.Span, out var r) && r.Member("written") is { IsTruthy: true })
        {
            Delta(operation, result, null);
        }
    }
}

[StateDomain("known-dirs", "known-dirs")]
internal sealed class KnownDirsDomain(HierLib lib) : HierDomain(lib, "known-dirs", "known-dirs")
{
    public override bool HomeScoped => true;

    public override string StateDirectory(RequestContext context) => Lib.KnownDirsStateDirectory;

    public override string StateDirectory(RequestContext context, ReadOnlyMemory<byte> argsJson) => Lib.KnownDirsStateDirectory;

    // b397: the index lives only in known-dir: rows of the meta table
    public override bool DbOnly => true;

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("note", OpStyle.Replace, false, (call, _) => new OpResult(false, default, "known-dirs.note is db-only", "op_failed")) with { DbBody = Lib.KnownDirsNoteDb },
        InternalRead("read", (call, _) => OpArgs.Run(call, _ => Lib.KnownDirsReadOp())),
    ];
}

// per-dir-files-to-db S6: the user's question log lives only in the qa_log table; decisions.qa-log and qa-thread read it
[StateDomain("qa-log", "qa-log")]
internal sealed class QaLogDomain(HierLib lib) : HierDomain(lib, "qa-log", "qa-log")
{
    public override bool DbOnly => true;

    public override IReadOnlyList<StateOp> Ops =>
    [
        Write("append", OpStyle.Append, true, (call, _) => new OpResult(false, default, "qa-log.append is db-only", "op_failed"), "dir") with { DbBody = Lib.QaLogAppendDb },
    ];
}

/// <summary>Runs the status refresh a write in the hierarchy dir calls for.</summary>
internal sealed class StatusRefresher(HierLib lib) : IStatusRefresher
{
    public void Refresh(string hierarchyDirectory) => lib.RefreshStatus(hierarchyDirectory);

    public void Delta(string domain, OpCall operation, OpResult result, string? refreshDir) =>
        lib.AfterStatusWrite(domain, operation.Op, operation.ArgsJson, result.ResultJson, refreshDir);
}

internal sealed partial class HierLib
{
    internal JsonArray CoreRecords() => _core.ReadPeerRecords();

    private static string ArgText(JsonValue args, string name) =>
        args.Member(name) is { IsText: true } t ? t.AsString() : throw new InvalidOperationException($"The \"{name}\" argument must be of type string");

    private void NotInMsgs(string path, string dir)
    {
        if (NodePath.Dirname(path) != MsgsDir(dir))
        {
            throw new InvalidOperationException($"{path} is not in {MsgsDir(dir)}");
        }
    }

    /// <summary><paramref name="text"/> with the closed marks added to its front matter, or null when it is already closed.</summary>
    private static string? RetiredText(string path, string text, JsonValue args)
    {
        var end = text.StartsWith("---\n", StringComparison.Ordinal) ? text.IndexOf("\n---\n", 3, StringComparison.Ordinal) : -1;
        if (end < 0)
        {
            throw new InvalidOperationException($"{path} has no front matter");
        }

        var front = text[..end];
        if (front.Contains("\nclosed: true", StringComparison.Ordinal))
        {
            return null;
        }

        var marks = "\nclosed: true\nclosed_by: " + ArgText(args, "closed_by") + "\nclosed_reason: " + ArgText(args, "closed_reason");
        return front + marks + text[end..];
    }

    /// <summary>The arguments of a fill, once <paramref name="path"/> is shown to be the response of the request the op names.</summary>
    private JsonObject FillArgsOf(JsonValue args, string path)
    {
        var req = args.Member("req");
        var amend = args.Member("amend") is { IsTruthy: true };
        var target = amend ? req is { IsText: true } r ? r.AsString() : null : ResponsePlan(req) is { IsNull: false } plan ? plan.Prop("path").AsString() : null;
        if (target is null || MsgRows.PathKey(target) != MsgRows.PathKey(path)
            || MsgRows.PoolKey(NodePath.Dirname(NodePath.Dirname(path))) != MsgRows.PoolKey(ArgText(args, "dir")))
        {
            throw new InvalidOperationException($"fill: {path} is not the {(amend ? "request" : "response")} of {JsConvert.ToJsString(req)}");
        }

        return new JsonObject
        {
            ["amend"] = amend,
            ["id"] = args.Member("id") ?? JsonValue.Null,
            ["from"] = args.Member("from") ?? JsonValue.Null,
            ["reqPath"] = req ?? JsonValue.Null,
            ["body"] = args.Member("body") ?? JsonValue.Null,
            ["part"] = !amend && args.Member("part") is { IsTruthy: true },
        };
    }

    /// <summary>The files an archive names, each shown to sit in the pool's <c>msgs</c> directory.</summary>
    private List<string> CheckedArchiveFiles(string dir, JsonValue args)
    {
        var files = args.Member("files") is { IsArray: true } f ? f.AsArray() : throw new InvalidOperationException("args.files is not iterable");
        var paths = new List<string>();
        foreach (var file in files)
        {
            var p = file.IsText ? file.AsString() : JsConvert.ToJsString(file);
            if (MsgRows.PoolKey(NodePath.Dirname(p)) != MsgRows.PoolKey(MsgsDir(dir)))
            {
                throw new InvalidOperationException($"{p} is not in {MsgsDir(dir)}");
            }

            paths.Add(p);
        }

        return paths;
    }

    internal JsonValue MsgsListOp(JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var names = DbMsgNames(dir).Where(n => MessageText.ParseFilename(n) is not null).ToList();
        names.Sort(StringComparer.Ordinal);
        return ToArray(names);
    }

    /// <summary>
    /// Every exchange of every team's pools as <c>{slug, open}</c>, newest first. The slugs come from the exchange rows and
    /// only the live scope is judged open; an exchange outside it is closed by <see cref="MsgRows.Live(MsgExchangePair)"/>.
    /// </summary>
    internal JsonValue MsgsIndexOp(JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var rows = new JsonArray();
        using var db = _core.DbMsgs();
        var slugs = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var pool in PoolDirs(dir, _roster.ListTeamNames(dir), null))
        {
            foreach (var (id, slug) in db.ExchangeSlugs(pool))
            {
                slugs.TryAdd(id, slug);
            }
        }

        var open = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var e in ListLiveExchangesAllTeams(dir))
        {
            var id = Member(e, "id").AsString();
            slugs[id] = Member(e, "slug");
            open[id] = Member(e, "open");
        }

        foreach (var id in slugs.Keys.OrderDescending(StringComparer.Ordinal))
        {
            rows.Add(new JsonObject { ["slug"] = slugs[id], ["open"] = open.TryGetValue(id, out var o) ? o : false });
        }

        return rows;
    }

    /// <summary><c>peers.append</c> in the database: <c>{type: "peer", ...rec, ts}</c> (and the write's <c>op_id</c>) as the next row of <c>dir</c>'s roster.</summary>
    internal OpResult PeersAppendDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var rec = args.Member("rec");
        if (call.OperationId is { Length: > 0 } operationId)
        {
            var copy = JsOps.Spread(rec);
            copy["op_id"] = operationId;
            rec = copy;
        }

        AppendRosterRow(tx, ArgText(args, "dir"), JsonText.Stringify(PeerRow(rec), 0));
        return new JsonObject { ["appended"] = true };
    });

    /// <summary><c>peers.put</c> (in-process only): appends <c>body</c> (JSON text) to <c>dir</c>'s roster as given; <c>{appended: true}</c>.</summary>
    internal static OpResult PeersPutDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var body = args.Member("body") is { IsText: true } text ? text.AsString() : throw new InvalidOperationException("peers.put needs a text body");
        AppendRosterRow(tx, ArgText(args, "dir"), body);
        return new JsonObject { ["appended"] = true };
    });

    // keyed by the dir's real path (the teams key's rule), so every spelling of one dir names one roster, as it named one file;
    // a row whose op_id the dir already holds (a replayed write) is not stored again
    private static void AppendRosterRow(IDbOpTransaction tx, string dir, string body) => tx.Execute(
        "INSERT OR IGNORE INTO roster_records(pool_dir, session_id, at_ms, raw, op_id) VALUES (?1, CASE WHEN json_valid(?2) THEN json_extract(?2, '$.session_id') END, NULL, ?2, CASE WHEN json_valid(?2) THEN json_extract(?2, '$.op_id') END)",
        Ah.Core.Text.PathGuard.RealPath(dir, strict: false) ?? dir, body);

    internal JsonValue PeersReadOp(JsonValue args) => ReadRoster(ArgText(args, "dir"));

    /// <summary>The newest row of every member the directory's peers.jsonl names, the reading the roster verdicts judge.</summary>
    internal JsonValue PeersLatestOp(JsonValue args) => LivenessRoster(ArgText(args, "dir"));

    /// <summary><c>gates.append</c> in the database: <c>rec</c> stamped with <c>ts</c> (and the write's <c>op_id</c>) as the next row of <c>dir</c>'s log.</summary>
    internal OpResult GatesAppendDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var dir = ArgText(args, "dir");
        var row = JsOps.Spread(args.Member("rec"));
        var ts = IsoNowString();
        row["ts"] = ts;
        if (call.OperationId is { Length: > 0 } operationId)
        {
            row["op_id"] = operationId;
        }

        AppendGateRow(tx, dir, JsonText.Stringify(row, 0), ts);
        return new JsonObject { ["appended"] = true };
    });

    /// <summary><c>gates.put</c> (in-process only): appends <c>body</c> (JSON text) to <c>dir</c>'s log as given; <c>{appended: true}</c>.</summary>
    internal OpResult GatesPutDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var body = args.Member("body") is { IsText: true } text ? text.AsString() : throw new InvalidOperationException("gates.put needs a text body");
        AppendGateRow(tx, ArgText(args, "dir"), body, IsoNowString());
        return new JsonObject { ["appended"] = true };
    });

    private static void AppendGateRow(IDbOpTransaction tx, string dir, string body, string at) => tx.Execute(
        "INSERT INTO gates(hier_dir, seq, body, at) VALUES (?1, (SELECT COALESCE(MAX(seq), 0) + 1 FROM gates WHERE hier_dir = ?1), ?2, ?3)", MsgRows.PoolKey(dir), body, at);

    internal JsonValue GatesReadOp(JsonValue args) => ReadGates(ArgText(args, "dir"));

    /// <summary>
    /// Stores one tracked-job record as the <c>jobs</c> row (<c>dir</c>, <c>team</c>, <c>id</c>), the whole record replaced: <c>{written: true}</c>.
    /// With <c>if_open</c>, <c>record</c> holds only the caller's own fields (a null removes one), merged onto the row as stored inside this
    /// write, so a field a hook changed since the caller read it survives (b417); a row that has ended or is gone is left as it is
    /// (<c>{written: false}</c>), so a write built from the earlier copy never undoes that end. <c>merge</c> merges the same way, ended or not
    /// (b423); a row that is gone is still left alone. Refuses a team or id that is empty or names a path, or a record that is not an object.
    /// </summary>
    internal static OpResult JobsPutDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var dir = ArgText(args, "dir");
        var team = ArgText(args, "team");
        var id = ArgText(args, "id");
        if (!IsJobKey(team) || !IsJobKey(id))
        {
            throw new InvalidOperationException($"{team}/{id} is not a job record of {dir}");
        }

        if (args.Member("record") is not { IsMap: true } record)
        {
            throw new InvalidOperationException("the job record must be an object");
        }

        // open means the stored record has no ended_at text: an end that does not parse still counts as ended
        var ifOpen = args.Member("if_open") is { IsTruthy: true };
        if (ifOpen || args.Member("merge") is { IsTruthy: true })
        {
            if (!(tx.Query("SELECT body FROM jobs WHERE team_home = ?1 AND team = ?2 AND id = ?3", MsgRows.PoolKey(dir), team, id) is [[string stored]]
                && JsonText.TryParse(Utf8Text.Encode(stored), out var current) && current.IsMap && !(ifOpen && current.Member("ended_at") is { IsText: true })))
            {
                return new JsonObject { ["written"] = false };
            }

            var merged = current.AsObject().Clone();
            foreach (var key in record.AsObject().Keys)
            {
                var value = record.AsObject().Get(key)!.Value;
                if (value.IsNull)
                {
                    _ = merged.Remove(key);
                }
                else
                {
                    merged[key] = value;
                }
            }

            record = JsonValue.FromObject(merged);
        }

        long? endedMs = record.Member("ended_at") is { IsText: true } ended && Ah.Core.Time.Timestamps.TryParseMs(ended.AsString(), out var ms) ? (long)ms : null;
        tx.Execute(
            "INSERT INTO jobs(team_home, team, id, body, ended_at) VALUES (?1, ?2, ?3, ?4, ?5) ON CONFLICT(team_home, team, id) DO UPDATE SET body = excluded.body, ended_at = excluded.ended_at",
            MsgRows.PoolKey(dir), team, id, JsonText.Stringify(record, 0), endedMs);
        return new JsonObject { ["written"] = true };
    });

    private static bool IsJobKey(string key) => key.Length > 0 && key != "." && key != ".." && !key.Contains('/', StringComparison.Ordinal);

    /// <summary>Deletes the job rows of <c>dir</c> that ended before <c>cutoff_ms</c>, which is clamped to at most 24 hours before now, in one DELETE. A record that is open or has no readable end time is left alone.</summary>
    internal OpResult JobsPruneDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var cutoff = Math.Min(JsConvert.ToNumber(args.Member("cutoff_ms")), NowMs - Ah.Hierarchy.StateCalls.IHierarchyStateCalls.JobRetention.TotalMilliseconds);
        tx.Execute("DELETE FROM jobs WHERE team_home = ?1 AND ended_at IS NOT NULL AND ended_at < ?2", MsgRows.PoolKey(ArgText(args, "dir")), (long)cutoff);
        return new JsonObject { ["removed"] = tx.Query("SELECT changes()") is [[long n]] ? n : 0 };
    });

    /// <summary>The job rows of <paramref name="dir"/> (per-dir-files-to-db S8: the <c>jobs</c> table), every team; none without a state database.</summary>
    public IReadOnlyList<JobEntry> JobRows(string dir, bool mustAnswer = false) => _jobs?.Rows(dir, mustAnswer) ?? [];

    /// <summary>
    /// Whether a response created or filled beside its request may lie in another pool than the request context names: the request is
    /// an existing request file in the same msgs/ dir as the target, with the response's id.
    /// </summary>
    public bool PathException(string domain, string op, JsonValue args)
    {
        if (domain != "msgs" || (op != "create" && op != "fill")
            || !(args.Member("req") is { IsText: true } req && args.Member("path") is { IsText: true } path && args.Member("dir") is { IsText: true } dir))
        {
            return false;
        }

        try
        {
            var reqPath = req.AsString();
            var target = path.AsString();
            if (!NodePath.IsAbsolute(reqPath) || !_core.MsgExists(reqPath) || NodePath.Dirname(reqPath) != NodePath.Dirname(target)
                || dir.AsString() != NodePath.Dirname(NodePath.Dirname(target)))
            {
                return false;
            }

            var want = MessageText.ParseFilename(target);
            var have = MessageText.ParseFilename(reqPath);
            var fm = _core.ReadMsgFile(reqPath).Member("fm");
            return want is { Type: "response" } && have is { Type: "request" } && fm is { IsMap: true } f && f.Member("id") is { IsText: true } id && id.AsString() == want.Id;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
