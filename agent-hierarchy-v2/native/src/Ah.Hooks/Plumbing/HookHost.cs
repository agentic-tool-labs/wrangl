using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hooks.Gates;

namespace Ah.Hooks.Plumbing;

/// <summary>What one hook body is given: the services, the event as Node's <c>readHookInput()</c> would return it, and its name.</summary>
internal sealed record GateContext(GateServices Services, JsonValue Input, string Name);

internal delegate HookOutcome GateBody(GateContext context);

/// <param name="Name">The hook unit's name.</param>
/// <param name="Body">The unit's decision.</param>
/// <param name="Class">What kind of gate it is.</param>
/// <param name="StaysHard">
/// The test that says a denial is one of the hard ones, kept in every gate mode. For a <see cref="GateClass.Split"/> unit every other
/// denial is advisory, and a split unit without it keeps every denial. For an <see cref="GateClass.Advisory"/> unit every denial is
/// advisory unless this says otherwise; a timeout or crash of the unit never reaches it.
/// </param>
/// <param name="Matcher">
/// The tools the unit runs for, as <c>hooks.json</c> matchers read: <c>*</c> for every tool, a <c>|</c>-joined list of tool names,
/// or a regular expression. Null for a unit that is not a PreToolUse gate.
/// </param>
internal sealed record GateUnit(string Name, GateBody Body, GateClass Class, Func<GateContext, bool>? StaysHard = null, string? Matcher = null);

/// <summary>
/// Runs one hook unit the way every hook must behave: it never exits 70 or 2, a gate that cannot decide denies (fail
/// closed, with exit 0), and an advisory hook that cannot run is silent and leaves one hook-errors row.
/// </summary>
internal static partial class HookHost
{
    /// <summary>The PreToolUse scripts of <c>hooks.json</c>: the units that fail closed.</summary>
    public static readonly IReadOnlyList<string> GateNames =
    [
        "pretooluse-soft-rules",
        "pretooluse-mail-claim",
        "pretooluse-msg-gate",
        "pretooluse-sendmessage-response",
        "pretooluse-ah-cli",
        "pretooluse-compound-allow",
        ChainName,
    ];

    /// <summary>
    /// The gates that allow, and leave a hook-errors row, when their input is unusable or their body throws. Every other gate
    /// denies. A deny the gate's own rules make, an unusable team file, and a unit with no native body are not failures of this
    /// kind and are unchanged. The chain is here because everything that can fail by that name happens before the session is
    /// identified; once it is identified, the chain's own failure and each gate's are decided in <see cref="RunChain"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> FailOpenGates =
    [
        ChainName,
        "pretooluse-soft-rules",
        "pretooluse-msg-gate",
        "pretooluse-sendmessage-response",
        "pretooluse-ah-cli",
        "pretooluse-compound-allow",
    ];

    /// <summary>Whether a failure of this unit (unusable input, an exception in its body) is a deny: a gate that is not in <see cref="FailOpenGates"/>.</summary>
    public static bool FailsClosed(string name, bool gate) => IsGate(name, gate) && !FailOpenGates.Contains(name);

    /// <summary>Whether a hook is a gate: named by its caller as one, or one of <see cref="GateNames"/>.</summary>
    public static bool IsGate(string name, bool gate) => gate || GateNames.Contains(name);

    /// <summary>
    /// The units that run for a session that is not part of ah, each asking the identity predicate itself: the ways in (a typed ah2
    /// command, an ah2 act), the session-start attribution of a hand-launched member, the CLI root of an explicit ah2 subagent, and the
    /// session directive, index and rules, which on /clear must move the flag before they ask. Every other unit does nothing for such a session.
    /// </summary>
    internal static readonly IReadOnlySet<string> SelfGuarded = new HashSet<string>(StringComparer.Ordinal)
    {
        "sessionstart",
        "session-directive",
        "session-index",
        "session-rules",
        "posttooluse-implicit-opt-in",
        "userpromptsubmit-peer-tracking",
        "userpromptsubmit-admin",
        "subagentstart-cli-root",
    };

    public const string SessionMismatchRefusal = "ah: this hook's session does not belong to its process; refused";

    /// <summary>The identity kind a unit's class declares; a unit without one is a programming error the unit census test catches.</summary>
    internal static HookIdentityKind KindOf(GateUnit unit) =>
        unit.Body.Method.DeclaringType?.GetCustomAttributes(typeof(HookIdentityAttribute), false) is [HookIdentityAttribute declared, ..]
            ? declared.Kind
            : HookIdentityKind.Grants;

    /// <summary>
    /// Whether the payload session is not one the hook's parent (the harness) provably holds, by the caller-identity predicate. With no
    /// session directory, or no session id, nothing is registered to compare with and nothing is refused.
    /// </summary>
    private static bool SessionDoesNotBelong(GateServices s, string? sessionId) =>
        s.Sessions is { } sessions && !string.IsNullOrEmpty(sessionId)
        && Ah.Mesh.MeshModule.HookSessionMismatch(sessions.Table, s.Process.ParentPid, sessionId, sessions.Sessions());

    /// <summary>
    /// How long a gate may take before its call is denied. Claude Code lets a call through when a hook times out, so this must stay
    /// below the <c>timeout</c> on every PreToolUse entry of <c>hooks.json</c> (60 seconds), leaving room for a cold start under load.
    /// </summary>
    public static readonly TimeSpan GateDeadline = TimeSpan.FromSeconds(15);

    public const string DeadlineReason = "ah gate timed out; command not checked";

    private static readonly AsyncLocal<CancellationTokenSource?> DeadlineSource = new();

    /// <summary>
    /// Whether the gate body running on this thread has outlived its deadline: its call has been denied, so it must not write anything
    /// (a request file, a mailbox entry, a dispatch or peer row) from here on. Always false outside a deadline-bounded gate run.
    /// </summary>
    internal static bool PastDeadline => DeadlineSource.Value is { IsCancellationRequested: true };

    /// <summary>
    /// Runs <see cref="RunUnbounded"/>; a gate that has not decided within <paramref name="deadline"/> (default
    /// <see cref="GateDeadline"/>) is denied. The abandoned body keeps its thread until the process exits right after this returns.
    /// </summary>
    public static HookOutcome Run(
        string name,
        bool gate,
        ReadOnlyMemory<byte> stdin,
        IHookErrorLog errors,
        Func<GateServices> services,
        IReadOnlyList<GateUnit> units,
        TimeSpan? deadline = null,
        bool identified = false)
    {
        if (name == ChainName)
        {
            return RunChain(stdin, errors, services, units, deadline);
        }

        if (!IsGate(name, gate))
        {
            return RunUnbounded(name, gate, stdin, errors, services, units);
        }

        // A stack overflow cannot be caught and the process would exit 134, which lets the call through; a thread's default stack is
        // smaller than the main thread's, so the gate gets a large one.
        HookOutcome? result = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        using var expired = new CancellationTokenSource();
        var worker = new Thread(
            () =>
            {
                DeadlineSource.Value = expired;
                try
                {
                    result = RunUnbounded(name, gate, stdin, errors, services, units, identified);
                }
                catch (Exception ex)
                {
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                }
            },
            maxStackSize: 16 * 1024 * 1024)
        {
            IsBackground = true,
        };
        worker.Start();
        if (worker.Join(deadline ?? GateDeadline))
        {
            failure?.Throw();
            return result!;
        }

        expired.Cancel();
        return TimedOut(name, stdin, errors, services, units);
    }

    /// <summary>What a gate that did not decide in time gives: one hook-errors row, then silence under advisory mode and a deny otherwise.</summary>
    private static HookOutcome TimedOut(string name, ReadOnlyMemory<byte> stdin, IHookErrorLog errors, Func<GateServices> services, IReadOnlyList<GateUnit> units)
    {
        TryLog(errors, new HookErrorRecord(name, DeadlineReason));
        var timedOut = units.FirstOrDefault(u => u.Name == name);
        var advisoryCheck = Task.Run(() => AdvisoryActive(timedOut, stdin, services));
        if (advisoryCheck.Wait(TimeSpan.FromMilliseconds(500)) && advisoryCheck.Result)
        {
            return HookEmit.Silent;
        }

        return HookEmit.Deny($"ah: {DeadlineReason}.", $"ah: {DeadlineReason}.");
    }

    private static HookOutcome RunUnbounded(
        string name,
        bool gate,
        ReadOnlyMemory<byte> stdin,
        IHookErrorLog errors,
        Func<GateServices> services,
        IReadOnlyList<GateUnit> units,
        bool identified = false)
    {
        var isGate = IsGate(name, gate);
        var closed = FailsClosed(name, gate);
        var unit = units.FirstOrDefault(u => u.Name == name);
        if (unit is null)
        {
            return Unavailable(name, "no native implementation", errors);
        }

        if (isGate && UnusableInput(name, stdin.Span) is { } why)
        {
            return InputUnusable(name, closed, why, errors);
        }

        string? eventName = null;
        string? sessionId = null;
        try
        {
            var s = services();
            var input = s.Config.ParseHookInput(stdin.Span, errors);
            if (input.IsNull)
            {
                // a JSON `null` payload: every property read of it throws, so no gate can decide
                throw new InvalidOperationException("the hook event is JSON null");
            }

            eventName = input.Member("hook_event_name") is { IsText: true } e ? e.AsString() : null;
            sessionId = input.Member("session_id") is { IsText: true } sid ? sid.AsString() : null;
            if (!identified && !SelfGuarded.Contains(name) && !Ah.Hooks.Lifecycle.OrchestratorIdentity.In(new GateContext(s, input, name)))
            {
                return HookEmit.Silent;
            }

            if (eventName != "SessionStart" && KindOf(unit) == HookIdentityKind.Grants && SessionDoesNotBelong(s, sessionId))
            {
                TryLog(errors, new HookErrorRecord(name, $"the payload session {SafeText.Render(sessionId ?? string.Empty)} does not belong to the hook's process; its effects were refused"));
                return isGate ? HookEmit.Deny(SessionMismatchRefusal, SessionMismatchRefusal) : HookEmit.Silent;
            }

            var context = new GateContext(s, input, name);
            var outcome = Advisorise(unit, context, unit.Body(context), errors);
            WithdrawDeniedRelay(context, outcome, errors);
            return outcome;
        }
        catch (Exception ex)
        {
            Log(errors, name, ex, eventName, sessionId);
            if (closed && AdvisoryActive(unit, stdin, services))
            {
                return HookEmit.Silent;
            }

            return closed
                ? HookEmit.Deny(
                    $"ah: the {name} gate hit an internal error and could not check this call, so it was held; see the hook error log.",
                    "ah: a gate could not check this call, so it was held; see the hook error log.")
                : HookEmit.Silent;
        }
    }

    /// <summary>The unit that takes a mail entry's relay claim before a SendMessage runs; its own denials hold no claim.</summary>
    private const string ClaimUnit = "pretooluse-mail-claim";

    /// <summary>
    /// A gate other than the claim gate denied a SendMessage: the call never runs, so the relay claim it may have taken is handed
    /// back, or, when the claim gate has not run yet, the denial is noted on the entry's claim so that gate takes none for that call.
    /// The retry is a new call and claims cleanly. Only a message that names a mail entry can hold a claim.
    /// </summary>
    private static void WithdrawDeniedRelay(GateContext c, HookOutcome outcome, IHookErrorLog errors)
    {
        if (c.Name == ClaimUnit || c.Services.Advisory is not { } advisory || c.Input.Text("hook_event_name") != "PreToolUse"
            || c.ToolName() != "SendMessage" || c.Input.Text("tool_use_id") is not { Length: > 0 } toolUseId || !IsDeny(outcome))
        {
            return;
        }

        try
        {
            var toolInput = c.ToolInput();
            var message = toolInput.Get("message") is { IsText: true } m ? m.AsString() : null;
            if (message is null || !(message.Contains("[ah-entry ", StringComparison.Ordinal) || c.Services.Hierarchy.Core.ExtractMsgToken(message) is not null))
            {
                return;
            }

            advisory.MailboxHooks.WithdrawRelay(
                c.Input.Text("session_id"),
                toolInput.Get("to") is { IsText: true } to ? to.AsString() : null,
                message,
                toolUseId,
                advisory.Callers.RequestContext(c.Cwd()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryLog(errors, new HookErrorRecord(c.Name, SafeText.Render(ex.Message)));
        }
    }

    private static bool IsDeny(HookOutcome outcome)
    {
        try
        {
            return outcome.Stdout.Length > 0
                && JsonText.Parse(outcome.Stdout.Span).Member("hookSpecificOutput")?.Member("permissionDecision") is { IsText: true } d && d.AsString() == "deny";
        }
        catch (JsonParseException)
        {
            return false;
        }
    }

    public const string UnusableReason =
        "ah: the hook input was unusable (empty, unreadable, not a JSON object, or without a tool_name), so this call was held; see the hook error log.";

    public const string UnusableMessage = "ah: a gate could not read its input, so it was held; see the hook error log.";

    /// <summary>
    /// Why a gate's input cannot be judged, as a short fixed word, or null when it can: stdin that is empty, not JSON, not an
    /// object, or (for a PreToolUse unit) without a string <c>tool_name</c>.
    /// </summary>
    internal static string? UnusableInput(string name, ReadOnlySpan<byte> stdin)
    {
        if (JsText.Trim(Encoding.UTF8.GetString(stdin)).Length == 0)
        {
            return "empty";
        }

        JsonValue value;
        try
        {
            value = JsonText.Parse(stdin);
        }
        catch (Exception)
        {
            return "not-json";
        }

        if (!value.IsMap)
        {
            return "not-object";
        }

        return name.StartsWith("pretooluse-", StringComparison.Ordinal) && value.Member("tool_name") is not { IsText: true }
            ? "no-tool-name"
            : null;
    }

    /// <summary>One hook-errors row naming the unit and the reason word, and the deny a gate gives (silence for any other hook).</summary>
    public static HookOutcome InputUnusable(string name, bool gate, string why, IHookErrorLog? errors)
    {
        TryLog(errors, new HookErrorRecord(name, "unusable hook input: " + why));
        return gate ? HookEmit.Deny(UnusableReason, UnusableMessage) : HookEmit.Silent;
    }

    /// <summary>A run that failed outside the hook's body: a gate denies, any other hook is silent, and a row is left when a log is reachable.</summary>
    public static HookOutcome Failed(string name, bool gate, Exception error, IHookErrorLog? errors)
    {
        TryLog(errors, new HookErrorRecord(name, SafeText.Render(error.Message)));
        return gate
            ? HookEmit.Deny(
                $"ah: the {name} gate hit an internal error and could not check this call, so it was held; see the hook error log.",
                "ah: a gate could not check this call, so it was held; see the hook error log.")
            : HookEmit.Silent;
    }

    private static void TryLog(IHookErrorLog? errors, HookErrorRecord record)
    {
        try
        {
            errors?.Log(record);
        }
        catch (Exception logging) when (logging is IOException or UnauthorizedAccessException)
        {
            // the log is the last link in the chain: there is nowhere left to report to
        }
    }

    /// <summary>A run the host refuses before the unit starts, so before any session is identified: silent, with one row.</summary>
    public static HookOutcome Refused(string name, string reason, IHookErrorLog? errors)
    {
        TryLog(errors, new HookErrorRecord(name, reason));
        return HookEmit.Silent;
    }

    /// <summary>A unit this binary has no native body for, as a plugin newer than its binary names: silent, with one row.</summary>
    public static HookOutcome Unavailable(string name, string reason, IHookErrorLog errors)
    {
        var message = $"ah: hook {name} is not implemented natively ({reason})";
        errors.Log(new HookErrorRecord(name, message));
        return HookEmit.Silent;
    }

    private static void Log(IHookErrorLog errors, string name, Exception ex, string? eventName, string? sessionId)
    {
        try
        {
            errors.Log(new HookErrorRecord(name, SafeText.Render(ex.Message), eventName, sessionId));
        }
        catch (Exception logging) when (logging is IOException or UnauthorizedAccessException)
        {
            // the log is the last link in the chain: there is nowhere left to report to
        }
    }
}
