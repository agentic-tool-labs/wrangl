using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hooks.Gates;

namespace Ah.Hooks.Plumbing;

/// <summary>Whether the advisory classes keep their denials (<see cref="Enforce"/>) or turn them into a row and a line (<see cref="Advisory"/>).</summary>
internal enum GateMode
{
    Advisory,
    Enforce,
}

internal static partial class HookHost
{
    /// <summary>The top-level key of the user config file that holds the <see cref="GateMode"/>.</summary>
    internal const string GateModeKey = "gate_mode";

    /// <summary>
    /// The mode in the user config file. A missing file or key is <see cref="GateMode.Advisory"/>. A file that cannot be read or parsed,
    /// or a value other than <c>advisory</c> or <c>enforce</c>, is <see cref="GateMode.Enforce"/> with <paramref name="error"/> set.
    /// </summary>
    internal static GateMode ReadGateMode(IFileReader files, string path, out string? error)
    {
        error = null;
        try
        {
            if (!files.Exists(path))
            {
                return GateMode.Advisory;
            }

            if (JsonText.Parse(files.ReadText(path)) is not { IsMap: true } config)
            {
                error = $"{path} is not a JSON object";
                return GateMode.Enforce;
            }

            if (config.Member(GateModeKey) is not { } mode)
            {
                return GateMode.Advisory;
            }

            switch (mode.IsText ? mode.AsString() : null)
            {
                case "advisory":
                    return GateMode.Advisory;
                case "enforce":
                    return GateMode.Enforce;
                default:
                    error = $"{GateModeKey} in {path} is neither \"advisory\" nor \"enforce\"";
                    return GateMode.Enforce;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            error = $"{path} could not be read: {SafeText.Render(e.Message)}";
            return GateMode.Enforce;
        }
    }

    /// <summary>Whether the session is under the auto-mode classifier or its user answers each prompt; any other or missing mode is not.</summary>
    internal static bool UnderClassifier(string? permissionMode) => permissionMode is "auto" or "default";

    /// <summary>The reason a PreToolUse deny or a Stop block gives, or null when <paramref name="outcome"/> blocks nothing.</summary>
    internal static string? BlockReason(HookOutcome outcome)
    {
        if (outcome.Stdout.Length == 0)
        {
            return null;
        }

        try
        {
            var root = JsonText.Parse(outcome.Stdout.Span);
            if (root.Member("hookSpecificOutput") is { IsMap: true } specific && specific.Text("permissionDecision") == "deny")
            {
                return specific.Text("permissionDecisionReason") ?? "denied";
            }

            return root.Text("decision") == "block" ? root.Text("reason") ?? "blocked" : null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    private const int RowReasonLimit = 200;

    /// <summary>
    /// Whether an advisory-class unit that failed or timed out may fail open: the payload names a classifier mode and the gate mode is
    /// advisory. Anything that cannot be read answers false, so the unit fails closed as before.
    /// </summary>
    internal static bool AdvisoryActive(GateUnit? unit, ReadOnlyMemory<byte> stdin, Func<GateServices> services)
    {
        if (unit?.Class != GateClass.Advisory)
        {
            return false;
        }

        try
        {
            if (!UnderClassifier(JsonText.Parse(stdin.Span).Text("permission_mode")))
            {
                return false;
            }

            var s = services();
            return ReadGateMode(s.Files, s.Config.UserConfigPath(), out _) == GateMode.Advisory;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// The outcome of an advisory-class unit whose call was denied, in a session under the classifier with <see cref="GateMode.Advisory"/>:
    /// no decision (never an allow, which would skip the classifier), so the normal permission flow decides, with one peer row and the
    /// reason as additional context. Every other outcome is returned as it is.
    /// </summary>
    internal static HookOutcome Advisorise(GateUnit unit, GateContext c, HookOutcome outcome, IHookErrorLog errors)
    {
        var convertible = unit.Class switch
        {
            GateClass.Advisory => unit.StaysHard is not { } kept || !kept(c),
            GateClass.Split => unit.StaysHard is { } hard && !hard(c),
            _ => false,
        };
        if (!convertible || BlockReason(outcome) is not { } reason)
        {
            return outcome;
        }

        var s = c.Services;
        var mode = ReadGateMode(s.Files, s.Config.UserConfigPath(), out var error);
        if (error is not null)
        {
            TryLog(errors, new HookErrorRecord(c.Name, SafeText.Render(error)));
        }

        if (mode == GateMode.Enforce || !UnderClassifier(c.Input.Text("permission_mode")))
        {
            return outcome;
        }

        var eventName = c.Input.Text("hook_event_name") ?? "PreToolUse";
        try
        {
            s.Hierarchy.Peers.RecordPeerRow(new JsonObject
            {
                ["type"] = "advisory",
                ["gate"] = c.Name,
                ["session_id"] = c.Input.Text("session_id") ?? string.Empty,
                ["tool"] = c.Input.Text("tool_name") ?? string.Empty,
                ["tool_use_id"] = c.Input.Text("tool_use_id") ?? string.Empty,
                ["reason"] = reason.Length > RowReasonLimit ? reason[..RowReasonLimit] : reason,
                ["ts"] = Timestamps.ToIsoString(s.Clock.UtcNow),
            });
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            // the row is the log; without it the call still goes on and the agent still gets the line
            TryLog(errors, new HookErrorRecord(c.Name, SafeText.Render(e.Message)));
        }

        return HookEmit.AdditionalContext(eventName, eventName == "PreToolUse" ? AdvisoryText(c.Name, reason) : $"ah advisory ({c.Name}): {reason}");
    }

    /// <summary>
    /// The line an advisory-converted denial gives the agent. The gate wrote its reason for a call it would have stopped; this gate does
    /// not stop it, but the permission flow still decides, so the line says only that and quotes the reason unchanged.
    /// </summary>
    internal static string AdvisoryText(string gate, string reason) =>
        $"ah advisory ({gate}): not stopped by this gate (advisory); the permission flow decides. Gate said: {reason}";
}
