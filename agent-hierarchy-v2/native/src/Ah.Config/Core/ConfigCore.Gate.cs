using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>The pure parts of <c>lib-gate</c> and <c>lib-session-role</c>, and the tier scale.</summary>
internal sealed partial class ConfigCore
{
    /// <summary>The session roles document (b397 group 2: a <c>meta</c> row read straight from the database, the daemon up or not).</summary>
    private JsonObject SessionRoles => SessionMapFile.Parse(docs?.Read(SessionMapFile.RolesKey), SessionRolesDomain.Version);

    public int SessionRoleVersion => SessionRolesDomain.Version;

    public int MaxSessions => SessionMapFile.MaxSessions;

    public string NoSessionKey => SessionMapFile.NoSessionKey;

    public JsonValue ReadSessionRole(JsonValue? sessionId) =>
        ReadSessionRoleByKey(SessionMapFile.KeyOf(sessionId)) is { } role ? role : JsonValue.Null;

    public JsonValue ReadOrchestratorFlag(JsonValue? sessionId) =>
        SessionRoles["sessions"].AsObject().Get(SessionMapFile.KeyOf(sessionId)) is { IsMap: true } entry
        && entry.AsObject().Get(SessionMapFile.OrchestratorField) is { IsTruthy: true }
            ? entry
            : JsonValue.Null;

    public bool ReadOrchestratorDeclined(JsonValue? sessionId) =>
        SessionRoles["sessions"].AsObject().Get(SessionMapFile.KeyOf(sessionId)) is { IsMap: true } entry
        && entry.AsObject().Get(SessionMapFile.DeclinedField) is { IsTruthy: true };

    public JsonObject Tier => new() { ["haiku"] = 1, ["sonnet"] = 2, ["opus"] = 3, ["fable"] = 4 };

    public string NormalizeSessionId(JsonValue? sessionId) => SessionKey(sessionId);

    public bool IsGatedPeerTarget(JsonValue? target, JsonValue? expectedName)
    {
        if (target is not { IsText: true } text || JsText.Trim(text.AsString()).Length == 0)
        {
            return false;
        }

        if (expectedName is not { IsText: true } expected || expected.AsString().Length == 0)
        {
            return false;
        }

        return StripPeerRef(JsText.Trim(text.AsString())) == expected.AsString();
    }

    /// <summary>
    /// <c>s.replace(/\s*\[[^\]]*\]\s*$/, "")</c> for a string with no trailing white space: the leftmost <c>[</c> that is
    /// followed by no <c>]</c> until the final character, which must be the closing <c>]</c>, together with the white
    /// space directly before it.
    /// </summary>
    private static string StripPeerRef(string s)
    {
        if (s.Length == 0 || s[^1] != ']')
        {
            return s;
        }

        var close = s.Length - 1;
        var firstClose = s.IndexOf(']');
        if (firstClose != close)
        {
            // A ] earlier than the last one: only a [ after that ] can start a match whose body holds no ].
            var open = s.IndexOf('[', firstClose + 1);
            return open < 0 || s.IndexOf(']', open + 1) != close ? s : s[..TrimBack(s, open)];
        }

        var first = s.IndexOf('[');
        return first < 0 ? s : s[..TrimBack(s, first)];
    }

    private static int TrimBack(string s, int index)
    {
        var i = index;
        while (i > 0 && JsText.IsSpace(s[i - 1]))
        {
            i--;
        }

        return i;
    }
}
