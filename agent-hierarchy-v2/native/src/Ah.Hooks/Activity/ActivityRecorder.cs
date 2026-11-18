using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// Records a Claude role session's own activity for the hierarchy status file, in <c>&lt;hier&gt;/activity/&lt;session&gt;.json</c>:
/// UserPromptSubmit is working, Stop is idle, a permission prompt is blocked, PostToolUse is working again. UserPromptSubmit and
/// Stop also refresh the status file in sessions that record nothing. Never writes stdout, never blocks.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class ActivityRecorder
{
    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (s.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var status = s.Hierarchy.Status;
        var hookEvent = c.Input.Text("hook_event_name");
        if (hookEvent == "PreToolUse")
        {
            AskItemMove.Asked(c);
        }

        // the question's direct reply (spec qa-history-replies §4.2): a transcript read first, database work only on a question's early step
        // (a subagent returned above; a member or sub-orchestrator session never scans the transcript)
        if (hookEvent == "PostToolUse" && !s.Config.IsMemberSession && !Ah.Hooks.Gates.DeclaredSubOrchestrator.Is(c))
        {
            QaLogCapture.Direct(c, advisory);
        }

        var cwd = c.Cwd();
        var dir = s.Config.HierarchyDir(cwd);
        var sessionText = c.Input.Text("session_id") ?? string.Empty;
        var session = JsonValue.FromString(sessionText);
        var ppid = s.Process.ParentPid;
        var recorded = false;
        var userPrompt = hookEvent == "UserPromptSubmit";
        var stop = hookEvent == "Stop";
        var context = stop ? advisory.Context.ReadContext(c.Input.Member("transcript_path")) : JsonValue.Null;
        // A prompt registers the session with its Claude process in the pool and in the main checkout, before the refresh below.
        if (userPrompt)
        {
            EnsureRegistered(c, advisory, cwd, sessionText, ppid);
            status.AppendSessionPid(dir, session, ppid);
            var mainDir = s.Config.MainHierarchyDir(cwd);
            if (!string.IsNullOrEmpty(mainDir) && mainDir != dir)
            {
                status.AppendSessionPid(mainDir, session, ppid);
                status.RefreshStatus(mainDir, new JsonObject { ["hook"] = true });
            }
        }

        var roleTruthy = false;
        var inMemberBranch = sessionText.Length > 0 && InMemberBranch(s, advisory.Context, dir, session, ppid, out roleTruthy);
        var hasContext = context.IsTruthy;
        bool OwnsTeam() => advisory.Context.OwnsTeam(dir, session, ppid);

        if (inMemberBranch)
        {
            // PostToolUse means working again; recordActivity leaves a record already in that state alone.
            var compacted = hookEvent == "SessionStart" && c.Input.Text("source") == "compact";
            // A lead in a checkout that holds no team of its own lands here, and an Orchestrator with a role row does too; a member's question is denied, so it never parks.
            var asking = hookEvent == "PreToolUse" && c.ToolName() == "AskUserQuestion" && (!roleTruthy || s.Config.ReadOrchestratorFlag(session).IsTruthy || s.Config.ReadSessionRole(session) is { IsText: true } role && role.AsString().EndsWith("orchestrator", StringComparison.Ordinal));
            var activity = compacted
                ? (status.ReadActivityRecord(dir, session).Member("activity") is { IsText: true } previous ? previous.AsString() : null)
                : asking ? "blocked" : hookEvent == "PostToolUse" ? "working"
                : hookEvent == "Notification" && !PermissionNotification(c) ? null : SelfState(hookEvent);
            if (roleTruthy && (userPrompt || stop))
            {
                s.Hierarchy.Roster.HealFalseDown(dir, session, ppid);
            }

            var transcript = advisory.Recovery.TranscriptPathOk(c.Input.Member("transcript_path"));
            if (activity is not null)
            {
                var fields = new JsonObject
                {
                    ["activity"] = activity,
                    ["blocked_by"] = asking ? "question" : hookEvent == "Notification" ? "permission" : JsonValue.Null,
                };
                if (asking)
                {
                    fields["note"] = QuestionNote(c);
                }

                if (hookEvent == "PostToolUse")
                {
                    if (c.Input.Member("tool_name") is { } tool)
                    {
                        fields["tool"] = tool;
                    }
                }
                else
                {
                    fields["tool"] = JsonValue.Null;
                }

                var extra = new JsonObject();
                if (transcript is not null)
                {
                    extra["transcript_path"] = transcript;
                }

                if (compacted)
                {
                    extra["compacted_at"] = s.Clock.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                }

                if (userPrompt || stop)
                {
                    var starts = OpenBackgroundStarts(s.Hierarchy.Peers.LatestSubagentRecords(session), s.Clock.UtcNow.ToUnixTimeMilliseconds());
                    extra["bg_open"] = (double)starts.Count;
                    // The oldest open launch: SubagentStop is unreliable, so the watch ages the hold from here (fewer-turns S3.1, E2).
                    extra["bg_since"] = starts.Count > 0 ? Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)starts.Min())) : JsonValue.Null;
                }

                fields["extra"] = extra.Count > 0 ? extra : JsonValue.Null;
                fields["resetStreak"] = stop;
                if (hasContext)
                {
                    fields["context"] = context;
                }

                MirrorTo(fields, MirrorDir(s, cwd, dir));
                recorded = status.RecordActivityRouted(dir, session, fields, hook: true);
            }
        }
        else if (sessionText.Length > 0 && OwnsTeam())
        {
            // The Orchestrator keeps no activity of its own except the context fill and the one state the delivery ladder needs: parked on a question.
            var heldRecord = status.ReadActivityRecord(dir, session);
            var held = heldRecord.Member("activity") is { IsText: true } heldActivity ? heldActivity.AsString() : null;
            var question = hookEvent == "PreToolUse" && c.ToolName() == "AskUserQuestion";
            // Its own permission prompt is not recorded (2.0.12): the user is at that terminal, and every prompt raised a blocked alert.
            var asking = question;
            // A question's answer leaves the turn running; the turn's end or the session's end leaves it idle, so neither state outlives the turn.
            var releases = (held == "blocked" && hookEvent == "PostToolUse") || (held is "blocked" or "working" && hookEvent is "Stop" or "SessionEnd");
            // Whether its turn is open, in fields of their own beside the activity (ah2-62): any prompt opens it, the turn's or the session's end closes it.
            var heldOpen = heldRecord.Member("turn_open") is { IsBool: true } heldTurn && heldTurn.AsBool();
            var opens = userPrompt && !heldOpen;
            var closes = heldOpen && hookEvent is "Stop" or "SessionEnd";
            var fills = asking || releases || (stop && hasContext);
            if (fills || opens || closes)
            {
                // A write that only opens or closes the turn restates the held state, since a write replaces the record.
                var fill = fills
                    ? new JsonObject
                    {
                        ["activity"] = asking ? "blocked" : hookEvent == "PostToolUse" ? "working" : SelfState("Stop")!,
                        ["blocked_by"] = question ? "question" : JsonValue.Null,
                    }
                    : new JsonObject { ["blocked_by"] = heldRecord.Member("blocked_by") ?? JsonValue.Null, ["note"] = heldRecord.Member("note") ?? JsonValue.Null };
                if (!fills && held is not null)
                {
                    fill["activity"] = held;
                }

                if (question)
                {
                    fill["note"] = QuestionNote(c);
                }

                if (hasContext)
                {
                    fill["context"] = context;
                }

                if (opens || heldOpen)
                {
                    fill["extra"] = new JsonObject
                    {
                        ["turn_open"] = !closes,
                        ["turn_open_at"] = opens ? Ah.Core.Time.Timestamps.ToIsoString(s.Clock.UtcNow) : closes ? JsonValue.Null : heldRecord.Member("turn_open_at") ?? JsonValue.Null,
                    };
                }

                MirrorTo(fill, MirrorDir(s, cwd, dir));
                recorded = status.RecordActivityRouted(dir, session, fill, hook: true);
            }
        }

        if (!recorded && (userPrompt || stop))
        {
            status.RefreshStatus(dir, new JsonObject { ["hook"] = true });
        }

        return HookEmit.Silent;
    }

    /// <summary>
    /// A session can be left unregistered, or registered under its old process or an old set of boxes: SessionStart may run before
    /// the team file holds the session's pane, or be skipped at its time bound (b340). The next prompt registers it then.
    /// </summary>
    private static void EnsureRegistered(GateContext c, AdvisoryServices advisory, string cwd, string sessionId, int ppid)
    {
        try
        {
            var s = c.Services;
            var pane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(s.Environment.GetVariable) is { Length: > 0 } herdr ? herdr
                : s.Environment.GetVariable("TMUX_PANE") is { Length: > 0 } tmux ? tmux : null;
            var request = new Ah.Mesh.CallerRequest(cwd, sessionId.Length > 0 ? sessionId : null, ppid, pane, HerdrFlavour: Ah.Core.Mux.HerdrFlavour.SessionFlavour(s.Environment.GetVariable));
            if (advisory.Callers.EnsureRegistered(request).Count == 0)
            {
                _ = Ah.Hooks.Lifecycle.ImplicitTeam.RegisterLate(c, advisory, request);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the activity record does not depend on the registration
        }
    }

    /// <summary>
    /// The hierarchy dir of the main checkout when the session works in a linked work tree, else null. The daemon's readers look for
    /// a session's activity record there, not in the work tree's own dir.
    /// </summary>
    /// <summary>What the Inbox shows of a live AskUserQuestion: its first question, cut to the note's size, or null when the call names none.</summary>
    private static JsonValue QuestionNote(GateContext c)
    {
        const int Cap = 200;
        if (c.ToolInput().Get("questions") is not { IsArray: true } questions
            || questions.AsArray().Count == 0
            || questions.AsArray()[0].Member("question") is not { IsText: true } text)
        {
            return JsonValue.Null;
        }

        return text.AsString().Length > Cap ? text.AsString()[..Cap] : text.AsString();
    }

    internal static string? MirrorDir(GateServices s, string cwd, string dir) =>
        s.Config.MainHierarchyDir(cwd) is { Length: > 0 } main && main != dir ? main : null;

    internal static void MirrorTo(JsonObject fields, string? mirrorDir)
    {
        if (mirrorDir is not null)
        {
            fields["mirror_dir"] = mirrorDir;
        }
    }

    /// <summary>
    /// Whether the session is one the recorder keeps its own activity for: a role session (the Orchestrator's, when it has a role row), or a session with an activity record that
    /// is failed or that owns no team (a team's Orchestrator keeps a record only for its context fill).
    /// </summary>
    internal static bool InMemberBranch(GateServices s, IHierarchyContext? context, string dir, JsonValue session, JsonValue ppid, out bool roleTruthy)
    {
        roleTruthy = s.Config.ReadSessionRole(session).IsTruthy;
        if (roleTruthy)
        {
            return true;
        }

        var held = s.Hierarchy.Status.ReadActivityRecord(dir, session);
        var heldFailed = held.IsTruthy && held.Member("activity") is { IsText: true } activity && activity.AsString() == "failed";
        return held.IsTruthy && (heldFailed || !(context?.OwnsTeam(dir, session, ppid) ?? false));
    }

    /// <summary>How many background Bash and Monitor tasks of the session started within <see cref="HierarchyModule.BackgroundWorkHoldMs"/> and have not stopped: an older one's end was lost.</summary>
    internal static int OpenBackgroundTasks(JsonArray latestSubagentRecords, double nowMs) => OpenBackgroundStarts(latestSubagentRecords, nowMs).Count;

    /// <summary>The start times of the open background work <see cref="OpenBackgroundTasks"/> counts; subagents count too (fewer-turns S3.1).</summary>
    internal static List<double> OpenBackgroundStarts(JsonArray latestSubagentRecords, double nowMs)
    {
        var starts = new List<double>();
        foreach (var r in latestSubagentRecords)
        {
            if (r.Member("kind") is { IsText: true } kind && kind.AsString() is "background" or "subagent"
                && r.Member("status") is { IsText: true } status && status.AsString() == "started"
                && r.Member("ts") is { IsText: true } ts && Ah.Core.Time.Timestamps.TryParseMs(ts.AsString(), out var startedMs)
                && nowMs - startedMs < HierarchyModule.BackgroundWorkHoldMs)
            {
                starts.Add(startedMs);
            }
        }

        return starts;
    }

    // b458: only a permission Notification (notification_type "permission_prompt", message
    // "Claude needs your permission") is a block; any other Notification, such as waiting for input, leaves the record alone.
    private static bool PermissionNotification(GateContext c) =>
        c.Input.Text("notification_type") == "permission_prompt"
        || (c.Input.Text("message") ?? string.Empty).Contains("permission", StringComparison.OrdinalIgnoreCase);

    private static string? SelfState(string? hookEvent)
    {
        foreach (var (name, activity) in HierarchyModule.SelfState)
        {
            if (name == hookEvent)
            {
                return activity;
            }
        }

        return null;
    }
}
