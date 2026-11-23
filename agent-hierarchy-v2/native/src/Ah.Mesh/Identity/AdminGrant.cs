using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Mesh;

/// <summary>
/// ah2-32: the admin grant. The user designates one Claude session as an Orchestrator with admin rights
/// (<c>ah admin grant &lt;pid&gt;</c>, run by the user in a plain terminal), recorded in the state database as the home document
/// <see cref="Key"/>: the session's process and its start time. A caller on that process's line is treated as the user wherever
/// ah decides who is calling, so nothing refuses it. The grant ends with that process, or with <c>ah admin revoke</c>.
/// Before ah2-46 the record was the file <c>&lt;AH home&gt;/admin.json</c>; one found there is taken into the database once.
/// </summary>
internal static class AdminGrant
{
    /// <summary>The home document (a <c>meta</c> row) that holds the grant: <c>{pid, pid_start, session_id, granted_at, via}</c>.</summary>
    public const string Key = "home.admin-grant";

    /// <summary>The file that held the grant before ah2-46; read until it is taken in, never written.</summary>
    public const string LegacyFileName = "admin.json";

    public static string LegacyPathIn(string ahHome) => Path.Combine(ahHome, LegacyFileName);

    /// <summary>
    /// The nearest Claude harness process on the line from <paramref name="callerPid"/> up; null when there is none or the line
    /// cannot be read. It is a session whether or not it registered: a top-level session with no team registers nothing.
    /// </summary>
    public static int? HarnessOf(IProcessTable table, int? callerPid)
    {
        try
        {
            if (callerPid is not { } pid || CallerIdentity.WholeLine(table, pid) is not { } line)
            {
                return null;
            }

            foreach (var p in line)
            {
                if (table.Command(p) is { Length: > 0 } command && HarnessCommand.IsHarnessProcess(p, _ => command))
                {
                    return p;
                }
            }

            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The desktop app's main process, as argv word 0 (spec admin-remote A3.2 D7, A5 D10). One path on purpose.</summary>
    private const string DesktopHost = "/Applications/Claude.app/Contents/MacOS/Claude";

    /// <summary>The desktop app's helper that starts its harness, as argv word 0 (A5 D10).</summary>
    private const string DesktopHelper = "/Applications/Claude.app/Contents/Helpers/disclaimer";

    private static readonly string[] NonInteractiveFlags = ["--print", "--input-format", "--output-format", "--sdk-url"];

    /// <summary>
    /// ah2-499 (spec admin-remote A3.2 D4-D7, A5 D9-D11): why the harness <paramref name="harness"/> may not take a grant from a typed
    /// prompt, or null when it may. D4/D9: no other harness on its ancestor line, by command or by executable (<paramref name="executableOf"/>),
    /// and every command and executable on it readable; never waived. D7/D10: H's parent the desktop helper and its grandparent the desktop
    /// host waives D5 and D6. D5/D11: no print or stream flag, and no short-flag cluster with <c>p</c>, in its argv. D6: it has a terminal.
    /// Only the prompt path asks this; the terminal <c>ah admin grant</c> does not.
    /// </summary>
    public static string? PromptGrantRefusal(IProcessTable table, int harness, Func<int, string?> executableOf)
    {
        const string Nested = "this session was started by another Claude Code session.";
        if (CallerIdentity.WholeLine(table, harness) is not { } line)
        {
            return Nested;
        }

        foreach (var p in line.Skip(1))
        {
            if (table.Command(p) is not { Length: > 0 } command || executableOf(p) is not { Length: > 0 } executable)
            {
                return Nested;
            }

            if (HarnessCommand.IsHarnessProcess(p, _ => command, _ => executable)
                || Argv0(command, executable).Split('/').Contains("claude-code", StringComparer.Ordinal))
            {
                return Nested;
            }
        }

        if (line is [_, var parent, var grandparent, ..]
            && Words(table.Command(parent) ?? string.Empty) is [DesktopHelper, ..]
            && Words(table.Command(grandparent) ?? string.Empty) is [DesktopHost, ..])
        {
            return null;
        }

        if (table.Command(harness) is not { Length: > 0 } own || Words(own).Skip(1).Any(PrintOrStream))
        {
            return "this session is not interactive (print or stream mode).";
        }

        return table.Tty(harness) is { Length: > 0 } ? null : "this session has no terminal.";
    }

    /// <summary>A print or stream flag as a whole word or <c>--flag=value</c> (D5), or a short-flag cluster holding <c>p</c> such as <c>-p</c> or <c>-cp</c> (D11).</summary>
    private static bool PrintOrStream(string word) =>
        NonInteractiveFlags.Any(f => word == f || word.StartsWith(f + "=", StringComparison.Ordinal))
        || (word.Length > 1 && word[0] == '-' && word[1] != '-' && word.Skip(1).All(char.IsAsciiLetter) && word.Contains('p', StringComparison.Ordinal));

    /// <summary>
    /// Argv 0 of <paramref name="command"/>: the executable path when the command line starts with it, so a path with a space in it is
    /// kept whole (D9), else the first word.
    /// </summary>
    private static string Argv0(string command, string executable) =>
        command.StartsWith(executable, StringComparison.Ordinal) ? executable : Words(command) is [var first, ..] ? first : string.Empty;

    private static string[] Words(string command) => command.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The recorded grant's text, or null when there is none. False when the database cannot answer: the caller is then not the admin.
    /// A grant still in the old file counts until <see cref="Import"/> takes it in. Reads only.
    /// </summary>
    public static bool TryRead(IHomeDocs? docs, string ahHome, out string? body)
    {
        body = null;
        if (docs is not null && !docs.TryRead(Key, out body))
        {
            return false;
        }

        if (body is null && File.Exists(LegacyPathIn(ahHome)))
        {
            body = File.ReadAllText(LegacyPathIn(ahHome));
        }

        return true;
    }

    /// <summary>Whether <paramref name="callerPid"/> is on the line of the process the grant names, still the same process.</summary>
    public static bool Holds(IProcessTable table, int? callerPid, IHomeDocs? docs, string ahHome)
    {
        try
        {
            if (!TryRead(docs, ahHome, out var body) || body is null || JsonText.Parse(body) is not { IsMap: true } grant)
            {
                return false;
            }

            var o = grant.AsObject();
            return o.Get("pid") is { IsNumber: true } p && (int)p.AsNumber() is > 1 and var granted
                && o.Get("pid_start") is { IsText: true } start
                && HarnessOf(table, callerPid) == granted
                && ProcessIdentity.Same(table, granted, start.AsString());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Takes a grant left in the old file into the database and removes the file; the database's own grant wins over the file.
    /// Null when there was nothing to do or it was done; a text when the file or the database could not be used (the file then stays).
    /// </summary>
    public static string? Import(IHomeDocs? docs, string ahHome)
    {
        var path = LegacyPathIn(ahHome);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if (docs is null || !docs.TryRead(Key, out var held))
            {
                return $"{path} holds a grant from before the move to the database, and the database cannot be read yet";
            }

            if (held is null)
            {
                var text = File.ReadAllText(path);
                if (JsonText.Parse(text) is not { IsMap: true } grant || grant.AsObject().Get("pid") is not { IsNumber: true })
                {
                    return $"{path} is not a grant record; it was left as it is";
                }

                docs.Write(Key, JsonText.Stringify(grant, 0));
            }

            File.Delete(path);
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return $"{path} could not be taken into the database ({e.Message}); it was left as it is";
        }
    }

    /// <summary>How a grant written by <c>ah admin grant</c> in a terminal is recorded, and how a row with no <c>via</c> reads (ah2-499).</summary>
    public const string ViaTerminal = "terminal";

    /// <summary>How a grant made by a typed <c>/wrangl:admin</c> prompt is recorded (ah2-499).</summary>
    public const string ViaPrompt = "prompt";

    /// <summary>The grant's <c>via</c>: how it was made. A row without one (written before ah2-499) reads as <see cref="ViaTerminal"/>.</summary>
    public static string ViaOf(string? body)
    {
        try
        {
            return body is not null && JsonText.Parse(body) is { IsMap: true } grant && grant.AsObject().Get("via") is { IsText: true } via && via.AsString().Length > 0
                ? via.AsString()
                : ViaTerminal;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return ViaTerminal;
        }
    }

    /// <summary>The pid the grant names, or null when there is none or it cannot be read.</summary>
    public static int? PidOf(string? body)
    {
        try
        {
            return body is not null && JsonText.Parse(body) is { IsMap: true } grant && grant.AsObject().Get("pid") is { IsNumber: true } p ? (int)p.AsNumber() : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Records the grant for <paramref name="pid"/>; an error text when it is not a live Claude session or the database refuses the write.</summary>
    public static string? Write(IProcessTable table, IHomeDocs? docs, string ahHome, int pid, string? sessionId, DateTimeOffset at, string via = ViaTerminal)
    {
        if (pid <= 1 || table.Command(pid) is not { Length: > 0 } command || !HarnessCommand.IsHarnessProcess(pid, _ => command))
        {
            return $"pid {pid.ToString(CultureInfo.InvariantCulture)} is not a running Claude Code session";
        }

        if (table.StartTime(pid) is not { Length: > 0 } start)
        {
            return $"the start time of pid {pid.ToString(CultureInfo.InvariantCulture)} cannot be read";
        }

        if (docs is null)
        {
            return "there is no state database to record the grant in";
        }

        var grant = new JsonObject
        {
            ["pid"] = pid,
            ["pid_start"] = start,
            ["session_id"] = sessionId is { Length: > 0 } ? sessionId : JsonValue.Null,
            ["granted_at"] = Timestamps.ToIsoString(at),
            ["via"] = via,
        };
        try
        {
            docs.Write(Key, JsonText.Stringify(grant, 0));
            File.Delete(LegacyPathIn(ahHome));
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>Removes the grant, from the database and from the old file; an error text when the database refuses.</summary>
    public static string? Revoke(IHomeDocs? docs, string ahHome)
    {
        try
        {
            docs?.Write(Key, null);
            File.Delete(LegacyPathIn(ahHome));
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
