using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>Reads of the hook event that every gate makes, with JavaScript's treatment of a missing or mistyped field.</summary>
internal static class GateInput
{
    /// <summary>A string member, or null when absent or not a string (<c>typeof x === "string"</c>).</summary>
    public static string? Text(this JsonValue value, string name) =>
        value.Member(name) is { IsText: true } t ? t.AsString() : null;

    /// <summary>A non-empty string member, or null (<c>typeof x === "string" &amp;&amp; x</c>).</summary>
    public static string? NonEmpty(this JsonValue value, string name) => value.Member(name)?.NonEmptyText;

    /// <summary>The member when it is an object (arrays and null do not count), else an empty object.</summary>
    public static JsonObject ObjectOrEmpty(this JsonValue value, string name) =>
        value.Member(name) is { IsMap: true } m ? m.AsObject() : new JsonObject();

    /// <summary>A string, or JavaScript <c>null</c> when there is none.</summary>
    public static JsonValue JsonOrNull(string? text) => text is null ? JsonValue.Null : JsonValue.FromString(text);

    public static string? ToolName(this GateContext c) => c.Input.Text("tool_name");

    /// <summary><c>input.tool_input</c> when it is an object, else <c>{}</c>.</summary>
    public static JsonObject ToolInput(this GateContext c) => c.Input.ObjectOrEmpty("tool_input");

    /// <summary><c>input.cwd</c> when a non-empty string, else the process's own.</summary>
    public static string Cwd(this GateContext c) => c.Input.NonEmpty("cwd") ?? c.Services.Process.CurrentDirectory;

    /// <summary>The session id as a gate keys it; <c>"__nosession__"</c> when the event carries none.</summary>
    public static string SessionKey(this GateContext c) => c.Input.NonEmpty("session_id") ?? "__nosession__";

    /// <summary><c>input.tool_input.command</c> when a string, else null.</summary>
    public static string? Command(this GateContext c) => c.ToolInput().Get("command") is { IsText: true } t ? t.AsString() : null;

    /// <summary>Writes one hook-errors row for an error a gate handles itself, as <c>logHookError</c> does.</summary>
    public static void LogError(this GateContext c, Exception error)
    {
        try
        {
            c.Services.ErrorLog.Log(new HookErrorRecord(c.Name, SafeText.Render(error.Message), c.Input.Text("hook_event_name"), c.Input.Text("session_id")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the log is the last link in the chain: there is nowhere left to report to
        }
    }
}
