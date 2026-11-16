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
    /// <summary>The home document (a <c>meta</c> row) that holds the grant: <c>{pid, pid_start, session_id, granted_at}</c>.</summary>
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

    /// <summary>Records the grant for <paramref name="pid"/>; an error text when it is not a live Claude session or the database refuses the write.</summary>
    public static string? Write(IProcessTable table, IHomeDocs? docs, string ahHome, int pid, string? sessionId, DateTimeOffset at)
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
