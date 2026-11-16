using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Config;

/// <summary>
/// The shape both per-session stores share: one JSON document <c>{version, sessions: {&lt;session id&gt;: entry}}</c> (a <c>meta</c> row), read as
/// "empty" whenever it cannot be trusted, and pruned to the newest <see cref="MaxSessions"/> entries on write.
/// </summary>
internal static class SessionMapFile
{
    internal const int MaxSessions = 50;

    internal const string NoSessionKey = "__nosession__";

    /// <summary>The entry field set while the session is the Orchestrator by command, beside the <c>pid</c> and <c>pid_start</c> of the process that owns it.</summary>
    internal const string OrchestratorField = "orchestrator_by_command";

    /// <summary>The entry field set once the user turned the session off; only an explicit opt-in clears it.</summary>
    internal const string DeclinedField = "orchestrator_declined";

    /// <summary><c>normalizeSessionId</c>: the trimmed id when it is a non-blank string, else the no-session key.</summary>
    internal static string KeyOf(JsonValue? sessionId) =>
        sessionId is { IsText: true } s && Ah.Core.Text.JsText.Trim(s.AsString()).Length > 0 ? Ah.Core.Text.JsText.Trim(s.AsString()) : NoSessionKey;

    /// <summary>The <c>meta</c> key of the ultra-gate choices (b397 group 2; was <c>agent-hierarchy.gate.json</c>).</summary>
    internal const string GateKey = "home.ultra-gate";

    /// <summary>The <c>meta</c> key of the session roles (b397 group 2; was <c>agent-hierarchy.session-roles.json</c>).</summary>
    internal const string RolesKey = "home.session-roles";

    /// <summary>The document under <paramref name="key"/> as a write's transaction reads it.</summary>
    internal static JsonObject Read(IDbOpTransaction tx, string key, int version) =>
        Parse(tx.Query("SELECT value FROM meta WHERE key = ?1", key) is [[string text]] ? text : null, version);

    /// <summary>Stores <paramref name="state"/> under <paramref name="key"/> inside a write's transaction.</summary>
    internal static void Put(IDbOpTransaction tx, string key, JsonObject state) =>
        tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, JsonText.Stringify(state, 0));

    /// <summary>
    /// The document's <c>{version, sessions}</c>. An absent or malformed document, one that is not an object, and one
    /// written by a newer plugin (an integer <c>version</c> above <paramref name="version"/>) all read as empty.
    /// </summary>
    internal static JsonObject Parse(string? text, int version)
    {
        JsonObject Empty() => new() { ["version"] = version, ["sessions"] = new JsonObject() };
        if (text is null)
        {
            return Empty();
        }

        JsonValue data;
        try
        {
            data = JsonText.Parse(text);
        }
        catch (Exception)
        {
            return Empty();
        }

        if (!data.IsMap)
        {
            return Empty();
        }

        var doc = data.AsObject();
        if (doc.Get("version") is { IsNumber: true } v && double.IsFinite(v.AsNumber()) && v.AsNumber() == Math.Floor(v.AsNumber()) && v.AsNumber() > version)
        {
            return Empty();
        }

        var sessions = doc.Get("sessions") is { IsMap: true } s ? s.AsObject() : new JsonObject();
        return new JsonObject { ["version"] = version, ["sessions"] = sessions };
    }

    /// <summary>The newest <see cref="MaxSessions"/> entries by <c>at</c> (text order; ties keep file order).</summary>
    // ponytail: the oldest go regardless of the Orchestrator flag, so an old flagged session can go bare until its next ah2 command; keep flagged entries first if that bites.
    internal static JsonObject Prune(JsonObject sessions)
    {
        var keys = sessions.Keys.ToList();
        if (keys.Count <= MaxSessions)
        {
            return sessions;
        }

        string At(string key)
        {
            var entry = sessions[key];
            if (entry.IsNull)
            {
                throw new InvalidOperationException("session entry is null");
            }

            return entry.IsMap && entry.AsObject().Get("at") is { IsTruthy: true } at ? Ah.Core.Json.JsConvert.ToJsString(at) : string.Empty;
        }

        var ordered = keys.OrderBy(At, StringComparer.Ordinal).ToList();
        var kept = new JsonObject();
        foreach (var key in ordered.Skip(keys.Count - MaxSessions))
        {
            kept[key] = sessions[key];
        }

        return kept;
    }

    /// <summary>The file's bytes: two-space JSON and a trailing newline.</summary>
    internal static byte[] ToBytes(JsonObject state) => Ah.Core.Text.Utf8Text.Encode(JsonText.Stringify(state, 2) + "\n");

    /// <summary><c>${path}.tmp-${pid}</c>.</summary>
    internal static string TmpName(string path) => $"{path}.tmp-{Environment.ProcessId}";
}
