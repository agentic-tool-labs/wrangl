using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Launch;

/// <summary>
/// How a member stopped at a prompt is seen: one place for the screen read that the roster verbs (deliver, spawn, answer) and the daemon
/// share, so a screen is recognised, hashed and excerpted the same way wherever it is looked at.
/// </summary>
public interface IScreenReading
{
    /// <summary>One <c>visible</c> read of an agent's screen: only what is on screen now, never scrollback. Empty when it cannot be read.</summary>
    string ReadVisibleScreen(string name);

    /// <summary><c>herdr agent list</c> as <c>[{name, pane_id, agent_status}]</c>, or null when herdr cannot answer.</summary>
    JsonArray? HerdrAgentList();

    /// <summary>
    /// What a member's screen shows, given herdr's status for it: <c>{composer, recognized, block, blocked_by, screen, screen_hash,
    /// draft}</c>. <paramref name="readScreen"/> reads the raw screen text; null reads it with <see cref="ReadVisibleScreen"/>.
    /// </summary>
    JsonObject ReadPromptCore(JsonValue member, string? agentStatus, Func<string, string>? readScreen = null);

    /// <summary>The screen line a recognised prompt starts on (the line holding its heading), trimmed, or null.</summary>
    string? PromptNoteLine(string kind, string? recognized, string? screen);

    /// <summary>
    /// The part of a screen worth showing: the note when given, else the last non-empty visible lines. Escape sequences and control
    /// characters are stripped and the text is cut to a fixed length, because it is data from another session's screen.
    /// </summary>
    string ExcerptOf(string? screen, string? note = null);
}

/// <summary>The screen reader over <paramref name="Herdr"/>.</summary>
public sealed partial record ScreenReading(IHerdr Herdr, IAhEnvironment Environment, IConfigCore Config, IScreenRules Screens, IKindHarnesses Harnesses) : IScreenReading
{
    private const int ExcerptLines = 12;
    private const int ExcerptCap = 1200;

    [GeneratedRegex("\u001b\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)|\u001b\\[[0-9;?]*[ -/]*[@-~]|\u001b[@-Z\\\\-_]", RegexOptions.CultureInvariant)]
    private static partial Regex Ansi();

    [GeneratedRegex("[\u0000-\u0008\u000b-\u001f\u007f-\u009f]", RegexOptions.CultureInvariant)]
    private static partial Regex Control();

    public string ReadVisibleScreen(string name)
    {
        var result = RunQuietly(["agent", "read", name, "--source", "visible"]);
        return result is { Ok: true } ? result.Stdout : string.Empty;
    }

    public JsonArray? HerdrAgentList()
    {
        var result = RunQuietly(["agent", "list"]);
        if (result is not { Ok: true } || !JsonText.TryParse(Utf8Text.Encode(result.Stdout), out var doc))
        {
            return null;
        }

        if (doc.Member("result")?.Member("agents") is not { IsArray: true } agents)
        {
            return null;
        }

        var rows = new JsonArray();
        foreach (var a in agents.AsArray())
        {
            if (a.IsNull)
            {
                return null;
            }

            var row = new JsonObject { ["name"] = a.Member("name") is { IsTruthy: true } name ? name : JsonValue.Null };
            if (a.Member("pane_id") is { } pane)
            {
                row["pane_id"] = pane;
            }

            row["agent_status"] = a.Member("agent_status") is { IsTruthy: true } status ? status : JsonValue.Null;
            rows.Add(row);
        }

        return rows;
    }

    public JsonObject ReadPromptCore(JsonValue member, string? agentStatus, Func<string, string>? readScreen = null)
    {
        var kind = JsConvert.ToJsString(Config.ResolveKind(member));
        var screen = string.Empty;
        try
        {
            var name = member.Member("name") is { } n ? JsConvert.ToJsString(n) : "undefined";
            screen = (readScreen ?? ReadVisibleScreen)(name) ?? string.Empty;
        }
        catch (Exception)
        {
            screen = string.Empty;
        }

        var seen = Screens.RecognizeScreen(kind, screen, agentStatus);
        var prompt = seen.Get("prompt") is { IsTruthy: true } p ? p : (JsonValue?)null;
        JsonValue blockedBy = prompt ?? (agentStatus == "blocked" ? "harness-prompt" : JsonValue.Null);
        return new JsonObject
        {
            ["composer"] = seen.Get("composer") ?? JsonValue.False,
            ["recognized"] = seen.Get("prompt") ?? JsonValue.Null,
            ["block"] = seen.Get("block") ?? new JsonArray(),
            ["blocked_by"] = blockedBy,
            ["screen"] = screen,
            ["screen_hash"] = Screens.ScreenHash(screen),
            ["draft"] = seen.Get("draft") is { IsTruthy: true },
        };
    }

    public string? PromptNoteLine(string kind, string? recognized, string? screen)
    {
        if (string.IsNullOrEmpty(recognized) || Harnesses.PromptHeading(kind, recognized) is not { } heading)
        {
            return null;
        }

        foreach (var line in (string.IsNullOrEmpty(screen) ? string.Empty : screen).Split('\n'))
        {
            if (line.Contains(heading, StringComparison.Ordinal))
            {
                var trimmed = JsText.Trim(line);
                return trimmed.Length > 0 ? trimmed : null;
            }
        }

        return null;
    }

    public string ExcerptOf(string? screen, string? note = null)
    {
        var cleanNote = Clean(note ?? string.Empty);
        List<string> lines;
        if (cleanNote.Length > 0)
        {
            lines = [JsText.Trim(cleanNote)];
        }
        else
        {
            lines = [];
            foreach (var line in Clean(screen ?? string.Empty).Split('\n'))
            {
                var end = line.Length;
                while (end > 0 && JsText.IsSpace(line[end - 1]))
                {
                    end--;
                }

                var kept = line[..end];
                if (JsText.Trim(kept).Length > 0)
                {
                    lines.Add(kept);
                }
            }

            if (lines.Count > ExcerptLines)
            {
                lines = lines.GetRange(lines.Count - ExcerptLines, ExcerptLines);
            }
        }

        var text = string.Join('\n', lines);
        return text.Length > ExcerptCap ? text[..ExcerptCap] : text;
    }

    private static string Clean(string s) => Control().Replace(Ansi().Replace(s, string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal), string.Empty);

    private MuxResult? RunQuietly(IReadOnlyList<string> args)
    {
        var text = Environment.GetVariable("AH_HERDR_TIMEOUT_MS");
        var timeout = string.IsNullOrEmpty(text) ? 10000 : JsConvert.ToNumber(JsonValue.FromString(text));
        if (!(double.IsFinite(timeout) && Math.Floor(timeout) == timeout && timeout >= 0))
        {
            return null;
        }

        try
        {
            return Herdr.Run(args, timeout);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
