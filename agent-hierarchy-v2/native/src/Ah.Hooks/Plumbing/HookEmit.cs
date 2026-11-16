using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hooks.Plumbing;

/// <summary>The outputs a PreToolUse hook gives: a silent allow, or one line of JSON on stdout with exit 0.</summary>
internal static class HookEmit
{
    /// <summary>No output, exit 0: the call proceeds under the user's normal permissions.</summary>
    public static HookOutcome Silent { get; } = new(ReadOnlyMemory<byte>.Empty, 0);

    /// <summary>
    /// <c>{systemMessage?, hookSpecificOutput: {hookEventName, permissionDecision, permissionDecisionReason?}}</c>. A null
    /// or empty <paramref name="systemMessage"/> is left out, as is a null <paramref name="reason"/>. The system message
    /// comes first unless <paramref name="messageLast"/>.
    /// </summary>
    public static HookOutcome Decision(string decision, string? reason, string? systemMessage = null, bool messageLast = false)
    {
        var inner = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = decision,
        };
        if (reason is not null)
        {
            inner["permissionDecisionReason"] = reason;
        }

        var outer = new JsonObject();
        var withMessage = !string.IsNullOrEmpty(systemMessage);
        if (withMessage && !messageLast)
        {
            outer["systemMessage"] = systemMessage!;
        }

        outer["hookSpecificOutput"] = inner;
        if (withMessage && messageLast)
        {
            outer["systemMessage"] = systemMessage!;
        }

        return new HookOutcome(JsonText.ToUtf8(outer, 0), 0);
    }

    /// <summary>
    /// <c>{hookSpecificOutput: {hookEventName, updatedInput, additionalContext?}}</c>: the whole replacement input of the tool call and no permission
    /// decision, so the call goes through the user's normal permissions.
    /// </summary>
    public static HookOutcome UpdatedInput(JsonObject input, string? additionalContext = null)
    {
        var inner = new JsonObject { ["hookEventName"] = "PreToolUse", ["updatedInput"] = input };
        if (!string.IsNullOrEmpty(additionalContext))
        {
            inner["additionalContext"] = additionalContext;
        }

        return new HookOutcome(JsonText.ToUtf8(new JsonObject { ["hookSpecificOutput"] = inner }, 0), 0);
    }

    /// <summary>
    /// What several PreToolUse gates give together: one object with the winning decision and its reason, every additional context
    /// and every system message, each left out when empty. Silent when there is nothing to say.
    /// </summary>
    public static HookOutcome Combined(string? decision, string? reason, string? additionalContext, string? systemMessage, JsonValue? updatedInput = null)
    {
        var outer = new JsonObject();
        if (!string.IsNullOrEmpty(systemMessage))
        {
            outer["systemMessage"] = systemMessage;
        }

        if (decision is not null || !string.IsNullOrEmpty(additionalContext) || updatedInput is not null)
        {
            var inner = new JsonObject { ["hookEventName"] = "PreToolUse" };
            if (updatedInput is { } rewritten)
            {
                inner["updatedInput"] = rewritten;
            }

            if (decision is not null)
            {
                inner["permissionDecision"] = decision;
                if (reason is not null)
                {
                    inner["permissionDecisionReason"] = reason;
                }
            }

            if (!string.IsNullOrEmpty(additionalContext))
            {
                inner["additionalContext"] = additionalContext;
            }

            outer["hookSpecificOutput"] = inner;
        }

        return outer.Count == 0 ? Silent : new HookOutcome(JsonText.ToUtf8(outer, 0), 0);
    }

    /// <summary><c>{systemMessage}</c> with exit 0: the call proceeds under the user's normal permissions and the message is shown.</summary>
    public static HookOutcome Warn(string systemMessage) =>
        new(JsonText.ToUtf8(new JsonObject { ["systemMessage"] = systemMessage }, 0), 0);

    /// <summary><c>{decision: "block", reason}</c> with exit 0, no trailing newline: what Stop and SubagentStop hooks write.</summary>
    public static HookOutcome Block(string reason) =>
        new(JsonText.ToUtf8(new JsonObject { ["decision"] = "block", ["reason"] = reason }, 0), 0);

    /// <summary>
    /// <c>{hookSpecificOutput: {hookEventName, additionalContext}}</c> with exit 0, no trailing newline: what SessionStart,
    /// SubagentStart and UserPromptSubmit hooks write.
    /// </summary>
    public static HookOutcome AdditionalContext(string eventName, string text) =>
        new(
            JsonText.ToUtf8(
                new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = eventName, ["additionalContext"] = text } },
                0),
            0);

    /// <summary><c>{terminalSequence}</c> with exit 0, no trailing newline: bytes the terminal is asked to write.</summary>
    public static HookOutcome Terminal(string sequence) =>
        new(JsonText.ToUtf8(new JsonObject { ["terminalSequence"] = sequence }, 0), 0);

    public static HookOutcome Deny(string reason, string? systemMessage = null) => Decision("deny", reason, systemMessage);

    /// <summary>
    /// The prefix of a denial that is a one-time nudge or a shape refusal rather than a hard gate. The ah2 mod strips it and
    /// re-delivers the text without the hook-error label; its mod-side copy must stay the same literal.
    /// </summary>
    public const string NudgeMarker = "[ah-nudge] ";

    /// <summary>A deny whose reason is <see cref="NudgeMarker"/> followed by <paramref name="reason"/>; used by the nudge sites only.</summary>
    public static HookOutcome Nudge(string reason, string? systemMessage = null, bool messageLast = false) =>
        Decision("deny", NudgeMarker + reason, systemMessage, messageLast);

    /// <summary>The same JSON line as <see cref="Decision"/> followed by a newline, as the dispatch preamble writes its own denial.</summary>
    public static HookOutcome DenyLine(string reason)
    {
        var line = Decision("deny", reason);
        var bytes = new byte[line.Stdout.Length + 1];
        line.Stdout.Span.CopyTo(bytes);
        bytes[^1] = (byte)'\n';
        return new HookOutcome(bytes, 0);
    }
}
