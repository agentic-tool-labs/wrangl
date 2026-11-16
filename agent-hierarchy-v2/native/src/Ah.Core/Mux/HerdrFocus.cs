using System.Text.Json;

namespace Ah.Core.Mux;

/// <summary>The herdr half of the focus jump: pure argv builders and the error-code reader.</summary>
public static class HerdrFocus
{
    /// <summary>The error code herdr answers when the pane does not host a recognised agent.</summary>
    public const string AgentNotFound = "agent_not_found";

    /// <summary>
    /// <c>herdr agent focus &lt;pane id&gt;</c>: moves workspace, tab and pane focus to a pane that hosts a recognised agent.
    /// (<c>pane focus --direction</c> is never built: it moves to a neighbour of the named pane.)
    /// </summary>
    /// <exception cref="ArgumentException">The id is not a canonical herdr pane id.</exception>
    public static IReadOnlyList<string> AgentFocusArgv(string paneId)
    {
        if (!MuxIds.IsHerdrPaneId(paneId))
        {
            throw new ArgumentException("not a herdr pane id", nameof(paneId));
        }

        return ["herdr", "agent", "focus", paneId];
    }

    /// <summary><c>herdr tab focus &lt;tab id&gt;</c>: the fallback for a pane herdr does not recognise as an agent.</summary>
    /// <exception cref="ArgumentException">The id is not a canonical herdr tab id.</exception>
    public static IReadOnlyList<string> TabFocusArgv(string tabId)
    {
        if (!MuxIds.IsHerdrTabId(tabId))
        {
            throw new ArgumentException("not a herdr tab id", nameof(tabId));
        }

        return ["herdr", "tab", "focus", tabId];
    }

    /// <summary>
    /// The <c>error.code</c> of a herdr answer, read from standard error and then standard output (a line of JSON each);
    /// null when none is there. Returned only when it is made of <c>a-z0-9_</c> and at most 40 characters, so it is safe to show.
    /// </summary>
    public static string? ErrorCode(string stdout, string stderr)
    {
        foreach (var text in new[] { stderr, stdout })
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var code = CodeOf(line.Trim());
                if (code is not null)
                {
                    return code;
                }
            }
        }

        return null;
    }

    private static string? CodeOf(string line)
    {
        if (line.Length == 0 || line[0] != '{')
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String)
            {
                var value = code.GetString()!;
                return IsSafeCode(value) ? value : null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool IsSafeCode(string value)
    {
        if (value.Length is < 1 or > 40)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
