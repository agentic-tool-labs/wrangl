using Ah.Core.Json;
using Ah.State;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>
/// Turns the state service's completed writes into read-model notices. It exists before the read model does (the service graph is
/// built first), so the model is attached to it afterwards; until then a write is told to no one.
/// </summary>
internal sealed class ReadModelWriteRelay : IStateWriteObserver
{
    private volatile IReadModel? _target;
    private volatile Func<string, string?>? _messageIdOf;
    private volatile Func<bool>? _peerRecordsInDb;

    /// <summary>
    /// Attaches the model; <paramref name="messageIdOf"/> reads the exchange id out of a message file name, with the message library's own
    /// parser. <paramref name="peerRecordsInDb"/> says whether the peer records live in the database, where no file event reports a write.
    /// </summary>
    internal void Attach(IReadModel model, Func<string, string?>? messageIdOf = null, Func<bool>? peerRecordsInDb = null)
    {
        _messageIdOf = messageIdOf;
        _peerRecordsInDb = peerRecordsInDb;
        _target = model;
    }

    /// <summary>How long the peer-records mode read is reused. A flip is made by a verb in another process, so only the age of the read can end it.</summary>
    internal static readonly TimeSpan ModeReuse = TimeSpan.FromSeconds(2);

    /// <summary>Whether the peer-records mode read is attached, so that a database-mode peer write is told to the model.</summary>
    internal bool PeerRecordsModeAttached => _peerRecordsInDb is not null;

    private readonly Func<long> _nowMs;
    private readonly object _modeGate = new();
    private long _modeReadMs = long.MinValue;
    private bool _modeInDb;

    internal ReadModelWriteRelay(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>The peer-records mode, read through the attached function at most once per <see cref="ModeReuse"/>, so a burst of appends opens the store once.</summary>
    private bool PeerRecordsInDb()
    {
        if (_peerRecordsInDb is not { } read)
        {
            return false;
        }

        lock (_modeGate)
        {
            var now = _nowMs();
            if (_modeReadMs == long.MinValue || now - _modeReadMs >= (long)ModeReuse.TotalMilliseconds || now < _modeReadMs)
            {
                _modeInDb = read();
                _modeReadMs = now;
            }

            return _modeInDb;
        }
    }

    /// <summary>
    /// Told of every write but an activity record, before the read model is: the view service ends a stretched epoch on it. Activity records come from
    /// hooks on each tool call, so while any agent works they would hold the epoch at its shortest, when its stretch is needed most.
    /// </summary>
    internal Action? Written { get; set; }

    public void Wrote(string domain, string op, ReadOnlyMemory<byte> argsJson, ReadOnlyMemory<byte> resultJson)
    {
        if (domain != "activity")
        {
            Written?.Invoke();
        }

        if (_target is not { } model)
        {
            return;
        }

        if (RefreshesStatus(domain, op, argsJson.Span) && JsonText.TryParse(argsJson.Span, out var written) && written.Member("dir") is { IsText: true } statusDir && statusDir.AsString().Length > 0)
        {
            model.Emit("status", statusDir.AsString());

            // b517: a parked question is written by decisions.ask, which the daemon never serves, so no write names its store. The status
            // refresh that follows one recounts that store's open questions, so it is the write that re-reads them here.
            if (domain == "status")
            {
                model.Emit(Ah.Watch.WatchModule.ParkedLaneName, statusDir.AsString());
            }
        }

        // In files mode the watched peer-pending file reports the write itself; in db mode nothing does, so the heard rows are reread here.
        if (domain == "peer-records" && op is "append" or "compact" && PeerRecordsInDb())
        {
            model.Emit("messages", Ah.Watch.WatchModule.HeardKey);
        }

        if (domain == "session-pids" && op == "append" && JsonText.TryParse(argsJson.Span, out var recorded) && recorded.Member("dir") is { IsText: true } pidDir && pidDir.AsString().Length > 0)
        {
            model.Emit("processes", pidDir.AsString());
        }

        // per-dir-files-to-db S6: the question log is a table, so no file event names it; the status re-read re-reads the dir's questions slices
        if (domain == "qa-log" && op == "append" && JsonText.TryParse(argsJson.Span, out var asked) && Canonical(asked) is { } qaDir)
        {
            model.Emit("status", qaDir);
        }

        if (domain == "progress")
        {
            // per-dir-files-to-db S11c/S11d: no file event tells of a plan or ledger row, or of a checklist row, so the write does;
            // the rollup, journal, .nudged and sessions rows feed no lane, as their files did not
            if (op is "plan-append" or "ledger-append" or "create-if-absent" or "journal-append" or "state-cas" or "checklist-rewrite" or "off-set" && JsonText.TryParse(argsJson.Span, out var plan)
                && plan.Member("dir") is { IsText: true } planDir && planDir.AsString().Length > 0
                && plan.Member("team") is { IsText: true } team && team.AsString().Length > 0)
            {
                model.Emit("progress", Ah.Watch.WatchModule.ProgressKey(planDir.AsString(), team.AsString()));
            }

            if (op == Ah.Hierarchy.HierarchyModule.BacklogWriteOp && JsonText.TryParse(argsJson.Span, out var backlog)
                && backlog.Member("dir") is { IsText: true } backlogDir && backlogDir.AsString().Length > 0)
            {
                model.Emit("backlog", backlogDir.AsString());
                model.Emit("item-log", backlogDir.AsString());
            }

            if (op == Ah.Hierarchy.HierarchyModule.WorkWriteOp && JsonText.TryParse(argsJson.Span, out var work)
                && work.Member("dir") is { IsText: true } workDir && workDir.AsString().Length > 0)
            {
                WroteWork(model, workDir.AsString(), work);
            }

            return;
        }

        if (domain == "msgs")
        {
            WroteMessage(model, op, argsJson);
            return;
        }

        if (domain != "activity")
        {
            WroteTree(model, domain, op, argsJson);
            return;
        }

        switch (op)
        {
            case "sweep":
                model.EmitKeySet("activity");
                return;
            case "record" or "clear":
                if (JsonText.TryParse(argsJson.Span, out var args)
                    && Canonical(args) is { } dir
                    && args.Member("subject") is { IsText: true } subject && subject.AsString().Length > 0)
                {
                    model.Emit("activity", Ah.Watch.WatchModule.ActivityKey(dir, subject.AsString()));
                }

                return;
        }
    }

    /// <summary>
    /// The writes of the Tree's lanes. Each names its key in its arguments (a team file's path, a registration's session, a grants write's dir
    /// and team, a decision log's path) or, for a known-dirs write, the one key; a write whose arguments cannot name the key asks for the
    /// lane's key set. The lanes drop a key whose directory is not a canonical one, so a differently spelled directory makes no record.
    /// </summary>
    private static void WroteTree(IReadModel model, string domain, string op, ReadOnlyMemory<byte> argsJson)
    {
        var parsed = JsonText.TryParse(argsJson.Span, out var args);
        switch (domain)
        {
            case "team" when op is "write" or "clear":
                if (parsed && args.Member("path") is { IsText: true } teamPath && Ah.Watch.WatchModule.TeamKey(teamPath.AsString()) is { } teamKey)
                {
                    model.Emit("teams", teamKey);
                }
                else
                {
                    model.EmitKeySet("teams");
                }

                return;
            case "mailbox" when op is "register" or "unregister":
                if (parsed && args.Member("session_id") is { IsText: true } session && session.AsString().Length > 0)
                {
                    model.Emit("registrations", Ah.Watch.WatchModule.RegistrationKey(session.AsString()));
                }
                else
                {
                    model.EmitKeySet("registrations");
                }

                return;
            case "gates" when op == "append":
                // the gate log is a table since per-dir-files-to-db S3, so no file event names it
                if (parsed && Canonical(args) is { } gatesDir)
                {
                    model.Emit("messages", Ah.Watch.WatchModule.GatesKey(gatesDir));
                }

                return;
            case "peers" when op == "append":
                // per-dir-files-to-db S7: the roster is a table, so no file event names it
                if (parsed && Canonical(args) is { } peersDir)
                {
                    model.Emit("peers", peersDir);
                }

                return;
            case "known-dirs" when op == "note":
                model.Emit("known-dirs", Ah.Watch.WatchModule.KnownDirsKey);
                return;
            case "worktree-grants":
                if (parsed && args.Member("dir") is { IsText: true } gdir && args.Member("team") is { IsText: true } team)
                {
                    model.Emit("worktrees", Ah.Watch.WatchModule.WorktreeKey(gdir.AsString(), team.AsString()));
                }
                else
                {
                    model.EmitKeySet("worktrees");
                }

                return;
            case "decisions" when op is "append" or "add":
                if (parsed && args.Member("path") is { IsText: true } path && Ah.Watch.WatchModule.DecisionKey(path.AsString()) is { } key)
                {
                    model.Emit("decisions", key);
                }
                else
                {
                    model.EmitKeySet("decisions");
                }

                return;
        }
    }

    /// <summary>Whether a completed write is one that rewrites the stored status document: the same writes that call the status refresh.</summary>
    internal static bool RefreshesStatus(string domain, string op, ReadOnlySpan<byte> argsJson) => domain switch
    {
        "msgs" => op is "create" or "settle" or "fill" or "retire",
        "peers" or "activity" => true,
        "status" => op == "refresh",
        "gates" => JsonText.TryParse(argsJson, out var args) && args.Member("rec")?.Member("type") is { IsText: true } type && type.AsString() == "liveness-nudge",
        _ => false,
    };

    /// <summary>
    /// A message write names the files it touched: create, settle and fill one <c>path</c>, archive a list of <c>files</c>. Each file is
    /// one exchange, so each is one notice; a file whose name is no message file is told to no one.
    /// </summary>
    /// <summary>A registry write can change a board card and any item's history: the backlog, the named team and every registered team re-read, and the held item logs of the directory.</summary>
    private static void WroteWork(IReadModel model, string dir, Ah.Core.Json.JsonValue args)
    {
        model.Emit("backlog", dir);
        model.Emit("item-log", dir);
        model.Emit("item-desc", dir);
        var rows = args.Member("register") is { IsArray: true } registered ? registered.AsArray() : [];
        foreach (var team in rows.Prepend(args).Select(static r => r.Member("team")).Where(static t => t is { IsText: true }).Select(static t => t!.Value.AsString()).Where(static t => t.Length > 0).Distinct(StringComparer.Ordinal))
        {
            model.Emit("progress", Ah.Watch.WatchModule.ProgressKey(dir, team));
        }
    }

    private void WroteMessage(IReadModel model, string op, ReadOnlyMemory<byte> argsJson)
    {
        if (_messageIdOf is not { } idOf || !JsonText.TryParse(argsJson.Span, out var args) || Canonical(args) is not { } dir)
        {
            return;
        }

        IEnumerable<string> names = op switch
        {
            "create" or "settle" or "fill" or "retire" => args.Member("path") is { IsText: true } path ? [path.AsString()] : [],
            "archive" => args.Member("files") is { IsArray: true } files ? files.AsArray().Where(static f => f.IsText).Select(static f => f.AsString()) : [],
            _ => [],
        };
        foreach (var name in names)
        {
            if (idOf(Path.GetFileName(name)) is { } id)
            {
                model.Emit("messages", Ah.Watch.WatchModule.MessageKey(dir, id));
            }
        }
    }

    /// <summary>The copy of the record the view reads: the mirror directory when the write names one, else its own.</summary>
    private static string? Canonical(JsonValue args) =>
        args.Member("mirror_dir") is { IsText: true } mirror && mirror.AsString().Length > 0 ? mirror.AsString()
        : args.Member("dir") is { IsText: true } dir && dir.AsString().Length > 0 ? dir.AsString() : null;
}
