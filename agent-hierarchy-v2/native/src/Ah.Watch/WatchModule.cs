using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>A view answer: the HTTP status and the body.</summary>
/// <param name="Status">200, or 404 with the not-found body.</param>
/// <param name="Body">The view, or <c>{view_schema, error, path, nearest}</c>.</param>
public sealed record ViewAnswer(int Status, JsonObject Body);

public static partial class WatchModule
{
    /// <summary>How long the set of directories to watch is kept before the registrations are read again to choose it.</summary>
    private static readonly TimeSpan ActiveDirsCache = TimeSpan.FromSeconds(2);

    /// <summary>The <c>view_schema</c> every document the view routes serve carries.</summary>
    public const int ViewSchema = ViewBuilder.ViewSchema;

    /// <summary>The reason a view carries when herdr could not be read.</summary>
    public const string HerdrUnreachable = ViewBuilder.HerdrUnreachable;

    /// <summary>How often the processes lane is polled while a view is connected. J4 (b459): was 2 s; a view may show a member's process state up to 5 s old.</summary>
    internal static readonly TimeSpan ProcessesPollPeriod = TimeSpan.FromSeconds(5);

    /// <summary>How often the watched status dirs are recomputed.</summary>
    public static readonly TimeSpan StatusDirsPeriod = TimeSpan.FromSeconds(2);

    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <summary>The JSON form a <c>dispatches.open</c> op result carries a candidate in.</summary>
    public static JsonValue DispatchCandidateToJson(DispatchCandidate candidate) => DispatchCandidateJson.ToJson(candidate);

    /// <summary>
    /// Whether <paramref name="sha"/> (7 to 40 lowercase hex digits) names a commit in <paramref name="tree"/>, asked with the same
    /// read-only argument list the view's runner uses; false for any other text, without starting a process.
    /// </summary>
    public static bool CommitExists(Ah.Abstractions.IProcessRunner process, string tree, string sha)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!GitRunner.IsSha(sha))
        {
            return false;
        }

        var result = process.Run(GitRunner.Request(tree, GitRunner.VerifyArgv(sha), GitRunner.DefaultTimeout));
        return result is { ExitCode: 0, TimedOut: false };
    }

    /// <summary>
    /// The name a dispatch is listed under: the request's <c>to_name</c>, else the member part of the address it was sent to, else
    /// <c>(unnamed)</c>.
    /// </summary>
    public static string DispatchName(string? toName, string? toAddr) => DispatchClock.NameOf(toName, toAddr);

    /// <summary>
    /// One dispatch's liveness clock: <c>Base</c> is the later of <paramref name="start"/> (ms; 0 if unknown) and the latest
    /// <c>heard</c> row from <paramref name="toAddr"/> for this session, and <c>Nudges</c> are the <c>liveness-nudge</c> gate
    /// times (ms, ascending) after <c>Base</c>. Whatever the peer said restarts the clock.
    /// </summary>
    public static (double Base, IReadOnlyList<double> Nudges) LivenessClock(
        JsonArray records,
        JsonArray gates,
        string sessionId,
        string requestId,
        string? toAddr,
        double start)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(gates);
        var (baseMs, nudges) = Liveness.Clock(WatchJson.Objects(records), WatchJson.Objects(gates), sessionId, requestId, toAddr, start);
        return (baseMs, nudges);
    }

    /// <summary>
    /// Whether a dispatch is due for a check-in: it has never been nudged, or the gap <paramref name="cadence"/> gives for its next
    /// check-in has passed since its last nudge after <paramref name="since"/>.
    /// </summary>
    public static bool DueForNudge(
        JsonArray gates,
        string sessionId,
        string itemId,
        double thresholdSeconds,
        IReadOnlyList<double> cadence,
        double now,
        double since = 0)
    {
        ArgumentNullException.ThrowIfNull(gates);
        return Liveness.DueForNudge(WatchJson.Objects(gates), sessionId, itemId, thresholdSeconds, cadence, now, since);
    }

    /// <summary>The daemon's rule for a working member's check-in, for the Stop hook's own clock: true holds the check-in back.</summary>
    public static bool WorkingHoldsCheckIn(double now, double workedAtMs, double baseMs) =>
        DispatchClock.WorkingHoldsCheckIn(now, workedAtMs, baseMs);

    /// <summary>A fresh, empty set of watched status dirs, shared by <see cref="Register"/> and the view routes.</summary>
    public static IStatusDirs CreateStatusDirs() => new StatusDirs();

    /// <summary>The dir among <paramref name="dirs"/> whose slice-key dirId is <paramref name="dirId"/>, or null.</summary>
    public static string? DirOfId(string dirId, IEnumerable<string> dirs) => dirs.FirstOrDefault(d => Model.ActivityLane.DirId(d) == dirId);

    /// <summary>A view source over the services that own each store, for the daemon's view routes.</summary>
    public static IViewSource CreateViewSource(ViewSourceServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new ProductionViewSource(services);
    }

    /// <summary>
    /// The event-driven read model of the view over <paramref name="reader"/>: the activity lane, its file-change hint watchers, and the
    /// single projector thread. <paramref name="hierarchyDirs"/> lists the canonical hierarchy directories; it is read at start and at
    /// each key-set enumeration, never per event. The caller starts it with <see cref="IReadModel"/>'s owner's <c>Start</c> through
    /// <see cref="StartReadModel"/> and disposes it at shutdown.
    /// </summary>
    public static IReadModel CreateReadModel(IViewStateReader reader, Func<IReadOnlyList<string>> hierarchyDirs, ReadModelOptions? options = null) =>
        Assemble(
            reader,
            hierarchyDirs,
            options,
            options?.Timeline is { } timeline ? new Model.SqliteTimelineStore(timeline.Opener, timeline.DbPath, timeline.BusyTimeoutMs) : null,
            options?.Timeline?.RetentionDays);

    internal static IReadModel Assemble(IViewStateReader reader, Func<IReadOnlyList<string>> hierarchyDirs, ReadModelOptions? options, Model.ITimelineStore? timeline, Func<int>? retentionDays, Func<long>? modelNowMs = null)
    {
        var messageIdOf = options?.MessageIdOf;
        var processes = options?.Processes;
        var home = options?.Home;
        var knownDirsFile = options?.KnownDirsFile;
        var newPlanFold = options?.NewPlanFold;
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(hierarchyDirs);
        var dirs = new Model.DirSet(hierarchyDirs);
        Model.ReadModel? model = null;
        var connected = () => model!.Scheduler.ClientConnected;
        Model.GitLane? git = options?.GitProcess is { } gitProcess
            ? new Model.GitLane(reader, () => dirs.Current, gitProcess, static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), key => model!.Emit(Model.GitLane.LaneName, key), options.Home, id => model!.Leases.IsSubscribed(id))
            : null;
        Model.MessagesLane? messagesRef = null;
        Model.QuestionsLane? questionsRef = null;
        Model.QueueLane? queueLane = null;
        Model.PlanItemsLane? planItemsRef = null;
        Model.ItemLogLane? itemLogRef = null;
        Model.SitrepLane? sitrepLane = null;
        Model.JobsLane? jobsLane = null;
        Model.IModelLane activity = new Model.ActivityLane(reader, dirs.Refresh, () => dirs.Current, changed: (dir, subject) => messagesRef?.RereadSession(dir, subject));
        Model.TimelineRecorder? recorder = null;
        if (timeline is not null)
        {
            recorder = new Model.TimelineRecorder(activity, timeline, dir => model!.Emit(Model.TimelineLane.LaneName, dir), git is null ? null : git.Notify, new Model.WorkingShare());
            activity = recorder;
        }

        if (git is not null && recorder is null)
        {
            activity = new Model.TimelineRecorder(activity, null, static _ => { }, git.Notify);
        }

        var retention = retentionDays ?? (static () => 7);
        var now = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bodies = new Model.ExchangeBodyLane(reader, () => dirs.Current, id => model!.Leases.IsSubscribed(id));
        var messages = new Model.MessagesLane(reader, dirs.Refresh, () => dirs.Current, reread: key =>
        {
            if (bodies.Holds(key))
            {
                model!.Emit(Model.ExchangeBodyLane.LaneName, key);
            }

            foreach (var held in queueLane?.HeldOfDir(Model.LaneParts.Split(key).First) ?? [])
            {
                model!.Emit(Model.QueueLane.LaneName, held);
            }

            foreach (var held in planItemsRef?.HeldOfDir(Model.LaneParts.Split(key).First) ?? [])
            {
                model!.Emit(Model.PlanItemsLane.LaneName, held);
            }

            // a timeline names a request by its addressee and its reply
            foreach (var held in itemLogRef?.HeldOfDir(Model.LaneParts.Split(key).First) ?? [])
            {
                model!.Emit(Model.ItemLogLane.LaneName, held);
            }

            foreach (var held in sitrepLane?.HeldOfDir(Model.LaneParts.Split(key).First) ?? [])
            {
                model!.Emit(Model.SitrepLane.LaneName, held);
            }

            // A run anchor opening or closing is an exchange write with no decisions line.
            questionsRef?.EmitHeldOfDir(Model.LaneParts.Split(key).First, model!.Emit);
        }, rereadSelf: key => model!.Emit(Model.MessagesLane.LaneName, key));
        messagesRef = messages;
        var lanes = new List<Model.IModelLane> { activity, messages, bodies, new Model.StatusLane(reader, () => dirs.Current, dir => questionsRef?.EmitHeldOfDir(dir, model!.Emit)) };
        Model.HintWatcher? watcher = null;
        Func<string, string?>? branchOf = options?.TranscriptFiles is { } rootFiles ? root => GitHead.BranchOf(rootFiles, root) : null;
        var treeLanes = home is null ? [] : Model.TreeModel.Lanes(reader, dirs.Refresh, home, () => dirs.Current, branchOf, options?.TeamGoal)
            .Select(l => l.Name == Model.DecisionsLane.LaneName
                ? new Model.DecisionsRereadNotifier(l, key => questionsRef?.EmitHeldOfDir(Model.LaneParts.Split(key).First, model!.Emit))
                : l.Name == Model.TeamLane.LaneName
                    // a team record change can confirm or end a lane, so every held Sitrep of the dir re-reads its scope (ah-ui-ia §3.1)
                    ? new Model.DecisionsRereadNotifier(l, key =>
                    {
                        foreach (var held in sitrepLane?.HeldOfDir(Model.LaneParts.Split(key).First) ?? [])
                        {
                            model!.Emit(Model.SitrepLane.LaneName, held);
                        }
                    })
                    : l.Name == Model.KnownDirsLane.LaneName
                        // the index is rows in ah.db, so no file event reports a note: its re-read sets the watched directories again
                        ? new Model.DecisionsRereadNotifier(l, key =>
                        {
                            _ = dirs.Refresh();
                            watcher?.SyncSoon();
                        })
                        : l)
            .ToList();
        // b517: the questions parked for the user (q<k>); among the Tree's lanes, so a reconcile reads them too. Its re-read re-reads the dir's held questions slices.
        if (home is not null && options?.ParkedProbe is { } parkedProbe)
        {
            treeLanes.Add(new Model.DecisionsRereadNotifier(new Model.ParkedLane(parkedProbe, dirs.Refresh, () => dirs.Current), dir => questionsRef?.EmitHeldOfDir(dir, model!.Emit)));
        }

        lanes.AddRange(treeLanes);
        // ah2-102 F2: the watcher's coverage, in process; a publish that changed it re-reads the lane
        if (options?.Coverage is Model.WatchCoverage coverage)
        {
            lanes.Add(new Model.CoverageLane(coverage));
            coverage.Changed = () => model?.Emit(Model.CoverageLane.LaneName, Model.CoverageLane.Key);
        }
        var lazy = new List<Model.LazyLane>();
        Model.ProgressLane? progress = null;
        Model.PlanItemsLane? planItems = null;
        Model.BacklogLane? backlogLane = null;
        if (newPlanFold is not null)
        {
            progress = new Model.ProgressLane(reader, newPlanFold, dirs.Refresh, () => dirs.Current, reread: key =>
            {
                foreach (var held in planItems?.HeldOf(key) ?? [])
                {
                    model!.Emit(Model.PlanItemsLane.LaneName, held);
                }

                foreach (var held in queueLane?.HeldOf(key) ?? [])
                {
                    model!.Emit(Model.QueueLane.LaneName, held);
                }

                foreach (var held in sitrepLane?.HeldCovering(key) ?? [])
                {
                    model!.Emit(Model.SitrepLane.LaneName, held);
                }

                // A plan row is a timeline entry, and a link or a child can be in another team.
                foreach (var held in itemLogRef?.HeldOfDir(Model.ProgressLane.Split(key).Dir) ?? [])
                {
                    model!.Emit(Model.ItemLogLane.LaneName, held);
                }

                // The questions slice carries the team's waiting items.
                if (questionsRef?.Keys().Contains(key, StringComparer.Ordinal) == true)
                {
                    model!.Emit(Model.QuestionsLane.LaneName, key);
                }

                // A checklist item naming a parent decides whether that backlog row is planned.
                if (Model.ProgressLane.Split(key).Dir is var dir && backlogLane?.Keys().Contains(dir, StringComparer.Ordinal) == true)
                {
                    model!.Emit(Model.BacklogLane.LaneName, dir);
                }
            });
            lazy.Add(new Model.LazyLane(progress, connected));
            planItems = new Model.PlanItemsLane(progress, () => dirs.Current, id => model!.Leases.IsSubscribed(id), (dir, team, nowMs) =>
                Model.SummaryData.LivenessOf(model!.Records, Model.ActivityLane.DirId(dir), Model.ProgressLane.TeamNameOf(team), nowMs, model!.ProcessesObservedAtMs?.Invoke()), options?.WorkCardsProbe);
            planItemsRef = planItems;
            lanes.Add(planItems);
            if (options?.QueueProbe is { } queueProbe)
            {
                queueLane = new Model.QueueLane(() => dirs.Current, id => model!.Leases.IsSubscribed(id), queueProbe);
                lanes.Add(queueLane);
            }

            if (options?.SitrepProbe is { } sitrepProbe)
            {
                sitrepLane = new Model.SitrepLane(() => dirs.Current, id => model!.Leases.IsSubscribed(id), (dir, team, nowMs) => sitrepProbe(dir, team, nowMs, folder =>
                {
                    var dirId = Model.ActivityLane.DirId(dir);
                    var name = Model.ProgressLane.TeamNameOf(folder);
                    return (
                        Model.SummaryData.LivenessOf(model!.Records, dirId, name, (long)nowMs, model!.ProcessesObservedAtMs?.Invoke()),
                        Model.TreeData.FindTeam(model!.Records, dirId, name) is { } entry ? Model.SummaryData.OpenDecisions(model!.Records, entry) : [],
                        Model.SummaryData.WorkItems(model!.Records, dirId, name).ToDictionary(static i => i.Member("key")!.Value.AsString(), static i => i.Member("owner")!.Value.AsString(), StringComparer.Ordinal));
                }));
                lanes.Add(sitrepLane);
            }

            if (options?.JobsProbe is { } jobsProbe)
            {
                jobsLane = new Model.JobsLane(() => dirs.Current, id => model!.Leases.IsSubscribed(id), jobsProbe, options.JobsVersion);
                lanes.Add(jobsLane);
            }
        }

        if (options?.BacklogProbe is { } backlogProbe)
        {
            backlogLane = new Model.BacklogLane(() => dirs.Current, id => model!.Leases.IsSubscribed(id), backlogProbe);
            lanes.Add(backlogLane);
        }

        if (options?.ItemLogProbe is { } itemLogProbe)
        {
            itemLogRef = new Model.ItemLogLane(() => dirs.Current, id => model!.Leases.IsSubscribed(id), itemLogProbe);
            lanes.Add(itemLogRef);
        }

        if (options?.ItemDescProbe is { } itemDescProbe)
        {
            lanes.Add(new Model.ItemLogLane(() => dirs.Current, id => model!.Leases.IsSubscribed(id), itemDescProbe, Model.ItemLogLane.DescName));
        }

        var questionsLane = new Model.QuestionsLane(reader, () => dirs.Current, id => model!.Leases.IsSubscribed(id), (dir, folder) =>
        {
            var dirId = Model.ActivityLane.DirId(dir);
            return Model.TreeData.FindTeam(model!.Records, dirId, Model.ProgressLane.TeamNameOf(folder)) is { } team ? Model.SummaryData.Waiting(model!.Records, dirId, team) : null;
        }, options?.WorkCardsProbe);
        questionsRef = questionsLane;
        lanes.Add(questionsLane);

        var pollers = new List<Model.ClientGatedPoller>();
        Model.ProcessesLane? processLane = null;
        if (processes is not null)
        {
            processLane = new Model.ProcessesLane(reader, () => dirs.Current, processes, memberFacts: options?.MemberFacts, pidsStamp: options?.PidsStamp);
            lazy.Add(new Model.LazyLane(processLane, connected));
            lazy.Add(new Model.LazyLane(new Model.PeersLane(reader, () => dirs.Current), connected));
        }

        if (timeline is not null)
        {
            lazy.Add(new Model.LazyLane(new Model.TimelineLane(timeline, () => dirs.Current, retention, now), connected));
        }

        if (git is not null)
        {
            lazy.Add(new Model.LazyLane(git, connected));
        }

        lanes.AddRange(lazy);
        Model.TranscriptLane? transcript = options?.TranscriptFiles is { } transcriptFiles && options.Usage is { } usage && options.ValidTranscriptPath is { } validTranscriptPath
            ? new Model.TranscriptLane(reader, transcriptFiles, usage, validTranscriptPath, () => dirs.Current, id => model!.Leases.IsSubscribed(id), _ => model!.Counters.TranscriptReset(), () => watcher?.SyncSoon(), options.TranscriptProjects, (dirId, subject) => Model.TranscriptLane.TreeRowOwns(model!.Store.Current.Slices.Values, dirId, subject))
            : null;
        if (transcript is not null)
        {
            lanes.Add(transcript);
        }

        watcher = new Model.HintWatcher(
            dirs,
            path => model!.Hint(path),
            () =>
            {
                model!.ReconcileNow(Model.ActivityLane.LaneName);
                model!.ReconcileNow(Model.MessagesLane.LaneName);
                model!.ReconcileNow(Model.ProgressLane.LaneName);
                ReconcileAll(model!, treeLanes);
                foreach (var held in jobsLane?.Keys() ?? [])
                {
                    model!.Emit(Model.JobsLane.LaneName, held);
                }

                backlogLane?.EmitHeld(model!.Emit);
                sitrepLane?.EmitHeld(model!.Emit);
            },
            home is not null,
            home,
            knownDirsFile,
            () => transcript?.LeasedDirs() ?? [],
            options?.ActiveDirs,
            options?.Log,
            options?.PeerPendingFile,
            ActiveDirsCache);
        var composers = (home is null ? new Model.ComposerRegistry() : Model.TreeModel.Composers(null, options?.LaneFacts, processLane is null ? null : () => processLane.ObservedAtMs, options?.TeamHeld, (dir, team) =>
            {
                // who is working decides each item's work: every held view of the team reads it again
                var progressKey = Model.ProgressLane.KeyOf(dir, Model.ProgressLane.FolderOf(team));
                foreach (var held in planItems?.HeldOf(progressKey) ?? [])
                {
                    model!.Emit(Model.PlanItemsLane.LaneName, held);
                }

                foreach (var held in sitrepLane?.HeldCovering(progressKey) ?? [])
                {
                    model!.Emit(Model.SitrepLane.LaneName, held);
                }
            }, (dir, folder) => progress?.PlanPending(dir, folder) == true))
            .Register(new Model.ExchangeListComposer(Model.ExchangeListComposer.ExchangesPrefix))
            .Register(new Model.ExchangeListComposer(Model.ExchangeListComposer.FeedPrefix))
            .Register(new Model.PlanSummaryComposer())
            .Register(new Model.PlanItemsComposer())
            .Register(new Model.PlanChangesComposer())
            .Register(new Model.FlowHeadComposer())
            .Register(new Model.FlowPageComposer())
            .Register(new Model.ItemMsgsComposer());
        var paths = new Model.PathLaneTable(() => dirs.Current, messageIdOf, home, knownDirsFile, options?.PeerPendingFile);
        if (transcript is not null)
        {
            paths.Register(Model.TranscriptLane.LaneName, transcript.Row());
        }

        model = new Model.ReadModel(lanes, new Model.ModelOptions(), modelNowMs, paths: paths, composers: composers) { Watcher = watcher, ProcessesObservedAtMs = processLane is null ? null : () => processLane.ObservedAtMs };
        // ah2-110: the coverage lane's first record (absent to the empty set) is a lane write too; Publish tells Changed only when the set changes
        if (options?.Coverage is Model.WatchCoverage)
        {
            model.Emit(Model.CoverageLane.LaneName, Model.CoverageLane.Key);
        }
        // Off the projector thread, so the lane reads and the team list behind this handler do not wait on starting watchers (b233).
        dirs.Changed += watcher.SyncSoon;
        dirs.Changed += model.ResolveLiveLeases;
        foreach (var prefix in new[] { Model.TeamExchangeListComposer.ExchangesPrefix, Model.TeamExchangeListComposer.FeedPrefix })
        {
            model.Pagers.Register(new Model.ExchangeListPager(prefix + ":", () => model!.Store.Current, (dirId, id) => ReadExchangeCold(reader, dirs.Current, dirId, id)));
        }

        foreach (var prefix in new[] { Model.ExchangeListComposer.ExchangesPrefix, Model.ExchangeListComposer.FeedPrefix })
        {
            model.Pagers.Register(new Model.ExchangeListPager(prefix + ":", () => model!.Store.Current, (dirId, id) => ReadExchangeCold(reader, dirs.Current, dirId, id), teamScoped: false));
        }

        model.Pagers.Register(new Model.OpenListPager(() => model!.Store.Current, (dirId, id) => ReadExchangeCold(reader, dirs.Current, dirId, id)));
        model.Pagers.Register(new Model.PlanChangesPager(() => model!.Store.Current, suffix => progress?.PlanChangesOf(suffix)));
        model.Pagers.Register(new Model.QuestionsPager(() => model!.Store.Current.Publish, questionsLane.RowsOf));
        model.Pagers.Register(new Model.QaThreadPager(reader, () => dirs.Current));
        if (timeline is Model.ITimelinePages timelinePages)
        {
            model.Pagers.Register(new Model.TimelinePager(timelinePages, retention, now, () => model!.Store.Current.Publish));
            model.Pagers.Register(new Model.TimelineSpansPager(timelinePages, retention, now, () => model!.Store.Current, (dirId, id) => ReadExchangeCold(reader, dirs.Current, dirId, id)));
        }

        if (transcript is not null)
        {
            model.Pagers.Register(new Model.TranscriptPager(transcript, options!.TranscriptFiles!, options.Usage!, () => model!.Store.Current.Publish));
        }

        if (home is not null)
        {
            dirs.Changed += () =>
            {
                foreach (var lane in new[] { Model.TeamLane.LaneName, Model.WorktreeLane.LaneName, Model.DecisionsLane.LaneName, Model.ParkedLane.LaneName })
                {
                    model.EmitKeySet(lane);
                }
            };
        }

        if (lazy.Count == 0)
        {
            return model;
        }

        if (processLane is not null)
        {
            pollers.Add(new Model.ClientGatedPoller(connected, ProcessesPollPeriod, () => processLane.Poll(model.Emit), model.Scheduler.RecordBusy));
        }

        if (backlogLane is not null)
        {
            // the Sitrep's Up next fills from the backlog, so it follows the backlog's own refresh; unchanged bytes publish nothing
            pollers.Add(new Model.ClientGatedPoller(connected, Model.BacklogLane.RefreshPeriod, () =>
            {
                backlogLane.EmitHeld(model.Emit);
                sitrepLane?.EmitHeld(model.Emit);
            }, model.Scheduler.RecordBusy));
        }

        var owned = new List<IDisposable>();
        var signals = new List<Action>();
        var msgsVersion = new Model.MsgsVersionSignal(
            () => dirs.CurrentOrRefresh() is [var dir, ..] ? (long)reader.Read("msgs", "version", dir, new Ah.Core.Json.JsonObject()).Member("version")!.Value.AsNumber() : null,
            () => model.ReconcileNow(Model.MessagesLane.LaneName),
            since =>
            {
                var known = dirs.Current;
                var current = dirs.Refresh();
                if (!known.SequenceEqual(current, StringComparer.Ordinal))
                {
                    // a pool seen for the first time has messages older than the counter
                    model.ReconcileNow(Model.MessagesLane.LaneName);
                    return;
                }

                foreach (var dir in current.Distinct(StringComparer.Ordinal))
                {
                    var ids = reader.Read("msgs", "changed", dir, new Ah.Core.Json.JsonObject { ["dir"] = dir, ["since"] = since }).Member("ids")!.Value.AsArray();
                    foreach (var id in ids)
                    {
                        model.Emit(Model.MessagesLane.LaneName, Model.MessagesLane.KeyOf(dir, id.AsString()));
                    }
                }
            });
        pollers.Add(new Model.ClientGatedPoller(connected, TimeSpan.FromSeconds(2), msgsVersion.Poll, model.Scheduler.RecordBusy));
        signals.Add(msgsVersion.Forget);
        if (options?.TeamsChangeToken is { } teamsToken)
        {
            var teamsVersion = new Model.MsgsVersionSignal(teamsToken, () => model.ReconcileNow(Model.TeamLane.LaneName), _ => model.ReconcileNow(Model.TeamLane.LaneName));
            pollers.Add(new Model.ClientGatedPoller(connected, TimeSpan.FromSeconds(2), teamsVersion.Poll, model.Scheduler.RecordBusy, log: options.Log));
            signals.Add(teamsVersion.Forget);
        }

        if (options?.SessionsChangeToken is { } sessionsToken)
        {
            void SessionsChanged()
            {
                // a new registration can name a pool not seen yet
                _ = dirs.Refresh();
                model.ReconcileNow(Model.RegistrationLane.LaneName);
            }

            var sessionsVersion = new Model.MsgsVersionSignal(sessionsToken, SessionsChanged, _ => SessionsChanged());
            pollers.Add(new Model.ClientGatedPoller(connected, TimeSpan.FromSeconds(2), sessionsVersion.Poll, model.Scheduler.RecordBusy, log: options.Log));
            signals.Add(sessionsVersion.Forget);
        }

        Model.TimelineArchiver? archiver = null;
        if (timeline is not null)
        {
            archiver = new Model.TimelineArchiver(timeline, retention, now);
            var mover = new Model.BackgroundArchiver(
                archiver,
                moved =>
                {
                    if (moved > 0)
                    {
                        model.EmitKeySet(Model.TimelineLane.LaneName);
                    }
                },
                TimeSpan.FromHours(24),
                TimeSpan.FromSeconds(30));
            owned.Add(mover);
            owned.Add(timeline);
            signals.Add(mover.Signal);
        }

        IEnumerable<(string Name, long Value)> Stats()
        {
            yield return ("timeline_append_failures", recorder?.Failures ?? 0);
            yield return ("timeline_archive_failures", archiver?.Failures ?? 0);
        }

        return new Model.LazyAwareModel(model, lazy, pollers, owned, signals, Stats);
    }

    /// <summary>An exchange the store does not hold, read through the messages lane's own op: the body its slice would carry, or null when it is gone.</summary>
    private static Ah.Core.Json.JsonValue? ReadExchangeCold(IViewStateReader reader, IReadOnlyList<string> dirs, string dirId, string id)
    {
        if (dirs.FirstOrDefault(d => Model.ActivityLane.DirId(d) == dirId) is not { } dir)
        {
            return null;
        }

        try
        {
            var exchange = reader.Read("msgs", "read", dir, new Ah.Core.Json.JsonObject { ["dir"] = dir, ["id"] = id });
            return exchange.IsMap ? Ah.Core.Json.JsonValue.FromObject(Model.MessagesLane.BodyOf(dir, id, exchange)) : default(Ah.Core.Json.JsonValue?);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void ReconcileAll(Model.ReadModel model, IEnumerable<Model.IModelLane> lanes)
    {
        foreach (var lane in lanes)
        {
            model.ReconcileNow(lane.Name);
        }
    }

    /// <summary>The parked-questions lane, whose key is a canonical hierarchy directory (b517).</summary>
    public const string ParkedLaneName = Model.ParkedLane.LaneName;

    /// <summary>The known-dirs lane's one key.</summary>
    public const string KnownDirsKey = Model.KnownDirsLane.Key;

    /// <summary>The teams lane's key of the team file at <paramref name="teamFilePath"/> (<c>&lt;dir&gt;/team.json</c> or <c>&lt;dir&gt;/teams/&lt;alias&gt;.json</c>), or null for any other path.</summary>
    public static string? TeamKey(string teamFilePath)
    {
        const string Default = "/team.json";
        const string Teams = "/teams/";
        var at = teamFilePath.LastIndexOf(Teams, StringComparison.Ordinal);
        if (at > 0 && Model.TeamLane.NameOf(teamFilePath[..at], teamFilePath) is not null)
        {
            return Model.TeamLane.KeyOf(teamFilePath[..at], teamFilePath);
        }

        return teamFilePath.EndsWith(Default, StringComparison.Ordinal) && teamFilePath.Length > Default.Length
            ? Model.TeamLane.KeyOf(teamFilePath[..^Default.Length], teamFilePath)
            : null;
    }

    /// <summary>The registrations lane's key of <paramref name="sessionId"/>.</summary>
    public static string RegistrationKey(string sessionId) => sessionId;

    /// <summary>The worktrees lane's key of (<paramref name="canonicalDir"/>, <paramref name="team"/>).</summary>
    public static string WorktreeKey(string canonicalDir, string team) => Model.WorktreeLane.KeyOf(canonicalDir, team);

    /// <summary>The decisions lane's key of the run whose log is at <paramref name="logPath"/> (<c>&lt;dir&gt;/pipeline/&lt;run&gt;/decisions.jsonl</c>), or null for any other path.</summary>
    public static string? DecisionKey(string logPath)
    {
        const string Marker = "/pipeline/";
        var at = logPath.LastIndexOf(Marker, StringComparison.Ordinal);
        if (at <= 0 || !logPath.EndsWith("/decisions.jsonl", StringComparison.Ordinal))
        {
            return null;
        }

        var run = logPath[(at + Marker.Length)..^"/decisions.jsonl".Length];
        return run.Length == 0 || run.Contains('/', StringComparison.Ordinal) ? null : Model.DecisionsLane.KeyOf(logPath[..at], run);
    }

    /// <summary>The activity lane's key of (<paramref name="canonicalDir"/>, <paramref name="subject"/>), for a notice that names one record.</summary>
    public static string ActivityKey(string canonicalDir, string subject) => Model.ActivityLane.KeyOf(canonicalDir, subject);

    /// <summary>The progress lane's key of (<paramref name="canonicalDir"/>, <paramref name="team"/>), for a notice that names one team's plan logs.</summary>
    public static string ProgressKey(string canonicalDir, string team) => Model.ProgressLane.KeyOf(canonicalDir, team);

    /// <summary>The messages lane's key of the exchange <paramref name="id"/> in the pool of <paramref name="canonicalDir"/>, for a notice that names one message file.</summary>
    public static string MessageKey(string canonicalDir, string id) => Model.MessagesLane.KeyOf(canonicalDir, id);

    /// <summary>The messages lane's key of the gate log of <paramref name="canonicalDir"/>, for a notice of a <c>gates</c> write.</summary>
    public static string GatesKey(string canonicalDir) => Model.MessagesLane.KeyOf(canonicalDir, Model.MessagesLane.GatesId);

    /// <summary>The messages lane's key for a reread of every open exchange's heard time, which a new peer record can move.</summary>
    public static string HeardKey => Model.MessagesLane.KeyOf(string.Empty, Model.MessagesLane.HeardId);

    /// <summary>How often the board audit runs with no ship in between (b284).</summary>
    public static TimeSpan BoardAuditDaily => BoardAudit.Daily;

    /// <summary>Mails a team's board audit findings to its Orchestrator once per distinct set; see <see cref="BoardAudit.Mail"/>. Returns whether it sent.</summary>
    public static bool MailBoardAudit(StallTeam team, IReadOnlyList<Ah.Hierarchy.ChecklistAuditFinding> findings, IEnumerable<JsonValue> held, Action<WatchMail> send) =>
        BoardAudit.Mail(team, findings, held, send);

    /// <summary>
    /// The teams the daemon should retire now, and the team files it could not read. Empty for a model not made by <see cref="CreateReadModel"/>, and while
    /// its lazy lanes are not current: waiting items and process readings come from them, and a missing one reads as nothing waiting.
    /// </summary>
    public static (IReadOnlyList<(string Dir, string Team, long LastWorkMs)> Retire, IReadOnlyList<string> Unreadable) TeamsToRetire(IReadModel? model, long nowMs, Func<string, string, bool> recordedDead)
    {
        var pass = model is Model.LazyAwareModel { Settled: false }
            ? Model.RetirePass.Empty
            : Model.LazyAwareModel.Unwrap(model)?.TeamsToRetire(nowMs, recordedDead) ?? Model.RetirePass.Empty;
        return ([.. pass.Retire.Select(static c => (c.Dir, c.Team, c.LastWorkMs))], pass.Unreadable);
    }

    /// <summary>
    /// The model's health counters, read at request time: <c>unclassified_hint</c>, <c>noncanonical_hint</c>, <c>drift</c>, <c>reread_retry</c>
    /// the current <c>coalesce_ms</c> and <c>projector_faults</c>, the completed <c>full_passes</c> and the <c>last_pass_ms</c> of the latest, and
    /// <c>listings</c>, the directory listings this model's projector has passed to the disk. Null for a model not made by <see cref="CreateReadModel"/>. Nothing here is a slice, so reading it publishes nothing.
    /// </summary>
    public static JsonObject? ReadModelStats(IReadModel? model) =>
        Model.LazyAwareModel.Unwrap(model) is not { } m ? null : WithLazyStats(model, new JsonObject
        {
            ["unclassified_hint"] = m.Counters.UnclassifiedHints,
            ["noncanonical_hint"] = m.Counters.NoncanonicalHints,
            ["drift"] = m.Counters.Drift,
            ["reread_retry"] = m.Counters.RereadRetries,
            ["coalesce_ms"] = m.Scheduler.CoalesceMs,
            ["projector_faults"] = m.Counters.ProjectorFaults,
            ["transcript_resets"] = m.Counters.TranscriptResets,
            ["full_passes"] = m.Counters.FullPasses,
            ["last_pass_ms"] = m.Counters.LastPassMs,
            ["listings"] = m.Counters.Listings,
            ["parked"] = JsonValue.FromArray(ParkedJson(m)),
            ["decision_orphans"] = DecisionOrphans(m),
        });

    private static JsonObject WithLazyStats(IReadModel? model, JsonObject stats)
    {
        if (model is Model.LazyAwareModel lazy)
        {
            foreach (var (name, value) in lazy.Stats())
            {
                stats[name] = value;
            }
        }

        return stats;
    }

    private static long DecisionOrphans(Model.ReadModel model) =>
        ((model.Lane(Model.DecisionsLane.LaneName) is Model.DecisionsRereadNotifier notifier ? notifier.Inner : model.Lane(Model.DecisionsLane.LaneName)) as Model.DecisionsLane)?.Orphans ?? 0;

    private static JsonArray ParkedJson(Model.ReadModel model)
    {
        var parked = new JsonArray();
        foreach (var p in model.Parked)
        {
            parked.Add(JsonValue.FromObject(new JsonObject { ["slice_id"] = p.SliceId, ["error_type"] = p.ErrorType, ["since"] = p.SinceMs }));
        }

        return parked;
    }

    /// <summary>Drops the held parsed slice bodies and job rows of a model made by <see cref="CreateReadModel"/>, so the next read reads them again: the on-demand rebuild (b459 step 11).</summary>
    public static void ForgetReadModel(IReadModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (Model.LazyAwareModel.Unwrap(model) is { } read)
        {
            read.ForgetParsed();
            (read.Lane(Model.JobsLane.LaneName) as Model.JobsLane)?.Forget();
            (read.Lane(Model.MessagesLane.LaneName) as Model.MessagesLane)?.ForgetKeys();
            ((read.Lane(Model.ProcessesLane.LaneName) as Model.LazyLane)?.Inner as Model.ProcessesLane)?.ForgetPids();
        }
    }

    /// <summary>Starts the projector thread of a model made by <see cref="CreateReadModel"/>: the start seed, then event application.</summary>
    public static void StartReadModel(IReadModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        (Model.LazyAwareModel.Unwrap(model) ?? throw new ArgumentException("the model was not made by CreateReadModel", nameof(model))).Start();
    }

    /// <summary>
    /// Disposes <paramref name="model"/> and answers how long each part of the close took, for the daemon's stop log: watcher, projector join,
    /// archiver join and timeline store close, in ms. Empty for a model not made by <see cref="CreateReadModel"/> (it is still disposed).
    /// </summary>
    public static string CloseReadModel(IReadModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        switch (model)
        {
            case Model.LazyAwareModel lazy:
                return lazy.Close();
            case Model.ReadModel plain:
                return plain.Close();
            default:
                model.Dispose();
                return string.Empty;
        }
    }

    /// <summary>
    /// Forces a reconcile of one lane of a model made by <see cref="CreateReadModel"/>: the lane's key set is enumerated from truth and every
    /// key read again, as a non-real pass that waits behind any real notice. The pass counts in <c>full_passes</c> and sets <c>last_pass_ms</c>
    /// (see <see cref="ReadModelStats"/>) when it has drained. The model is otherwise reconciled only at its start and, while a client is
    /// connected, periodically.
    /// </summary>
    /// <param name="model">The model; one not made by <see cref="CreateReadModel"/> is refused.</param>
    /// <param name="lane">The lane name, as in <see cref="IReadModel.Emit"/>.</param>
    public static void ReconcileReadModel(IReadModel model, string lane)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(lane);
        (Model.LazyAwareModel.Unwrap(model) ?? throw new ArgumentException("the model was not made by CreateReadModel", nameof(model))).ReconcileNow(lane);
    }

    /// <summary>The poll period the watch keeps: <c>AH_WATCH_POLL_MS</c>, then <c>AHD_WATCH_POLL_MS</c>, else 15 s.</summary>
    public static double PollMs(IAhEnvironment environment) => Liveness.PollMs(environment);

    /// <summary>
    /// <c>/v1/inspect/timers</c>: for each live Orchestrator registration (or only <paramref name="session"/>), its dispatches with
    /// their deadlines and the events the dispatch clock would fire now. A dry evaluation.
    /// </summary>
    public static JsonObject InspectTimers(IWatchHost host, double nowMs, double pollMs, string? session = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Inspect.Timers(host, nowMs, pollMs, session);
    }

    /// <summary><c>/v1/inspect/events</c>: the unacked event entries the daemon mailed, oldest first, optionally of one box address.</summary>
    public static JsonObject InspectEvents(
        IEnumerable<WatchBox> boxes, Func<string, IReadOnlyList<JsonObject>> entriesOf, Func<string, string, bool> isAcked, string? address = null)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(entriesOf);
        ArgumentNullException.ThrowIfNull(isAcked);
        return Inspect.Events(boxes, entriesOf, isAcked, address);
    }

    /// <summary>The environment variable that turns the per-request view timing on when its value is <c>1</c>.</summary>
    public const string ViewTimingSwitch = ViewTiming.Switch;

    /// <summary>Whether <paramref name="switchValue"/>, the value of <see cref="ViewTimingSwitch"/>, turns the view timing on.</summary>
    public static bool ViewTimingIsOn(string? switchValue) => ViewTiming.IsOn(switchValue);

    /// <summary>Opens the timing of a request for the view at <paramref name="path"/>; the reads and runs it makes are counted into it.</summary>
    public static IViewTimingScope OpenViewTiming(string path) => ViewTiming.Open(path);

    /// <summary>The timing of the request this call belongs to, or null when none is open.</summary>
    public static IViewTimingScope? CurrentViewTiming => ViewTiming.Current;

    /// <summary>Times a section of a build until the result is disposed; a call made with no timing open times nothing.</summary>
    public static IDisposable ViewTimingSection(string name) => ViewTiming.Section(name);

    /// <summary>A file reader that counts what it is asked for into the current request's timing and answers as <paramref name="inner"/> does.</summary>
    public static IFileReader TimeFiles(IFileReader inner) => new TimedFileReader(inner);

    /// <summary>A process runner that counts how long each run takes into the current request's timing and answers as <paramref name="inner"/> does.</summary>
    public static IProcessRunner TimeProcesses(IProcessRunner inner) => new TimedProcessRunner(inner);

    /// <summary>
    /// Opens the listing scope of one page build for a caller that derives the page from a model itself, so the build reads inside it from
    /// the first read on. A scope already open is the build's own; the result then does nothing. Dispose it when the build ends.
    /// </summary>
    public static IDisposable OpenBuildScope() => ViewBuilder.OpenBuildScope();

    /// <summary>A reader of watched status documents with revisions that only rise.</summary>
    /// <remarks><paramref name="witnessOf"/> gives the witness of the compute that wrote a document, handed out with that one document; without it every expiry computes again.</remarks>
    public static IStatusRevs CreateStatusRevs(IViewStateReader state, Func<long>? nowMs = null, Func<JsonValue, Ah.Hierarchy.StatusWitness?>? witnessOf = null) => new StatusRevs(state, nowMs, witnessOf);

    /// <summary>A copy of a status document without the fields stamped with the instant it was computed (see <c>StatusRevs.WithoutClockStamps</c>).</summary>
    public static JsonValue StatusWithoutClockStamps(JsonValue doc) => StatusRevs.WithoutClockStamps(doc);

    /// <summary>
    /// The view at <paramref name="path"/> as of <paramref name="nowMs"/>. <paramref name="herdr"/> is called when a herdr-transport
    /// team has a stream; omitted, herdr counts as unreachable.
    /// </summary>
    public static ViewAnswer BuildView(
        IViewSource source, string path, double nowMs, IReadOnlyList<string>? extraDirs = null, Func<HerdrReading>? herdr = null)
    {
        var built = ViewBuilder.InPageScope(() => ViewBuilder.Build(source, path, nowMs, extraDirs, herdr));
        return new ViewAnswer(built.Status, built.Body);
    }

    /// <summary>
    /// A model of the hierarchy as of <paramref name="nowMs"/> that every page is derived from, so that a page after the first
    /// reads nothing the first has read. <paramref name="herdr"/> is called when a herdr-transport team has a stream; omitted,
    /// herdr counts as unreachable.
    /// </summary>
    public static IViewModel CreateViewModel(
        IViewSource source, double nowMs, IReadOnlyList<string>? extraDirs = null, Func<HerdrReading>? herdr = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ViewBuilder.SharedModel(source, nowMs, extraDirs, herdr);
    }

    /// <summary>The facts two builds are compared on to find events, read from <paramref name="source"/> as of <paramref name="nowMs"/>.</summary>
    public static ViewSnapshot EventSnapshot(IViewSource source, double nowMs, IReadOnlyList<string>? extraDirs = null, Func<HerdrReading>? herdr = null) =>
        ViewBuilder.EventSnapshot(source, nowMs, extraDirs, herdr);

    /// <summary>The events between two snapshots, oldest first; none when there is no <paramref name="previous"/> one.</summary>
    public static ViewEventBatch DiffEvents(ViewSnapshot? previous, ViewSnapshot current, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(current);
        return ViewEvents.Diff(previous, current, nowMs);
    }

    /// <summary>
    /// One step of finding events: reads a snapshot and compares it with the one in <paramref name="previous"/>, unless the status
    /// revisions, team files and boxes look as they did then, when nothing is read and the same cursor comes back.
    /// </summary>
    public static EventStep StepEvents(EventCursor? previous, IViewSource source, IStatusRevs revs, IReadOnlyList<string> statusDirs, double nowMs, Func<HerdrReading>? herdr = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(revs);
        ArgumentNullException.ThrowIfNull(statusDirs);
        var stamp = ViewEvents.Stamp(previous?.Snapshot, source, revs, statusDirs);
        // TODO: pending joins have no stamp, so a join that appears is not found until something else moves; stamp the join
        // directory here once the join library is ported.
        if (previous is not null && previous.Stamp == stamp)
        {
            return new EventStep(previous, new ViewEventBatch([], 0));
        }

        var snapshot = EventSnapshot(source, nowMs, statusDirs, herdr);
        return new EventStep(new EventCursor(snapshot, stamp, nowMs), DiffEvents(previous?.Snapshot, snapshot, nowMs));
    }

    /// <summary>An event as a JSON object with the cursor <paramref name="id"/> the daemon gave it.</summary>
    public static JsonObject EventJson(ViewEvent viewEvent, string id)
    {
        ArgumentNullException.ThrowIfNull(viewEvent);
        return ViewEvents.ToJson(viewEvent, id);
    }

    /// <summary>The watcher's coverage (ah2-102 F2): one instance, given to both <see cref="ReadModelOptions.Coverage"/> and <see cref="WatchDependencies"/>.</summary>
    public static IWatchCoverage CreateCoverage() => new Model.WatchCoverage();

    /// <summary>
    /// Registers the watch tick (every poll period) and the status-dir backstop (every 2 s). The composition root calls it once.
    /// </summary>
    public static void Register(IJobRegistry jobs, WatchDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(dependencies);
        var tick = new WatchTick(dependencies);
        jobs.Add(
            "watch-tick",
            TimeSpan.FromMilliseconds(tick.PollMs),
            async cancellation =>
            {
                await tick.RunAsync(cancellation).ConfigureAwait(false);
                dependencies.Stamp?.Invoke("watch-tick");
            });
        jobs.Detail("watch-tick", tick.TakeTickReport);
        if (dependencies.Cpu is { } cpu)
        {
            jobs.Meter("watch-tick", cpu.Take);
        }
        jobs.Add(
            "refresh-status-dirs",
            StatusDirsPeriod,
            _ =>
            {
                tick.RefreshStatusDirs();
                tick.ExpireCoverage(dependencies.Clock.UtcNow.ToUnixTimeMilliseconds());
                return Task.CompletedTask;
            });
    }
}
