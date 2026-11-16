using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

/// <summary>How many tokens a session's context holds, from the tail of its transcript.</summary>
public interface IHierarchyContext
{
    /// <summary>The most of a transcript's end that is read, in bytes.</summary>
    int TailBytes { get; }

    /// <summary>Whether the session owns a team in <paramref name="dir"/>, by the recorded Orchestrator pid or session id.</summary>
    bool OwnsTeam(string dir, JsonValue? sessionId, JsonValue? pid = null);

    /// <summary>
    /// <c>{used, window_source, at}</c> for the newest assistant entry with usage in the transcript's tail, else null, plus <c>model</c>
    /// when that entry names one. The window is not known from a transcript, so <c>window_source</c> is always <c>"unknown"</c> and there
    /// is no <c>window</c> or <c>pct</c>.
    /// </summary>
    JsonValue ReadContext(JsonValue? path, JsonValue? now = null);
}

internal sealed class ContextLib(IFileReader files, IClock clock, ITeamFiles teams) : IHierarchyContext
{
    public int TailBytes => 256 * 1024;

    public bool OwnsTeam(string dir, JsonValue? sessionId, JsonValue? pid = null)
    {
        try
        {
            var n = JsConvert.AsInteger(pid);
            int? invokerPid = n is > 0 and <= int.MaxValue ? (int)n.Value : null;
            var session = sessionId is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null;
            return teams.OwnedTeams(dir, new TeamInvoker(invokerPid, session)).Count > 0;
        }
        catch (Exception e) when (e is not StoreUnavailableException)
        {
            return false;
        }
    }

    public JsonValue ReadContext(JsonValue? path, JsonValue? now = null)
    {
        if (path is not { IsText: true } p || p.AsString().Length == 0)
        {
            return JsonValue.Null;
        }

        try
        {
            var tail = files.ReadTail(p.AsString(), TailBytes);
            var lines = Utf8Text.Decode(tail.Bytes).Split('\n').ToList();
            if (tail.StartsMidFile)
            {
                lines.RemoveAt(0);
            }

            for (var i = lines.Count - 1; i >= 0; i--)
            {
                JsonValue entry;
                try
                {
                    entry = JsonText.Parse(lines[i]);
                }
                catch (JsonParseException)
                {
                    continue;
                }

                if (UsedOf(entry) is not { } used)
                {
                    continue;
                }

                var reading = new JsonObject { ["used"] = used, ["window_source"] = "unknown", ["at"] = IsoAt(now) };
                if (entry.Member("message")?.Member("model") is { IsText: true } model && model.AsString().Length > 0)
                {
                    reading["model"] = model;
                }

                return reading;
            }

            return JsonValue.Null;
        }
        catch (Exception)
        {
            return JsonValue.Null;
        }
    }

    private static double Num(JsonValue? v) => v is { IsNumber: true } n && double.IsFinite(n.AsNumber()) && n.AsNumber() >= 0 ? n.AsNumber() : 0;

    private static double? UsedOf(JsonValue entry)
    {
        if (!(entry.IsMap || entry.IsArray) || entry.Member("isSidechain") is { IsBool: true } side && side.AsBool())
        {
            return null;
        }

        if (entry.Member("message") is not { } m || !(m.IsMap || m.IsArray))
        {
            return null;
        }

        var assistantRole = m.Member("role") is { IsText: true } role && role.AsString() == "assistant";
        var assistantType = entry.Member("type") is { IsText: true } type && type.AsString() == "assistant";
        if (!assistantRole && !assistantType)
        {
            return null;
        }

        if (m.Member("usage") is not { } u || !(u.IsMap || u.IsArray))
        {
            return null;
        }

        var used = Num(u.Member("input_tokens")) + Num(u.Member("cache_read_input_tokens")) + Num(u.Member("cache_creation_input_tokens"));
        return used > 0 ? used : null;
    }

    private string IsoAt(JsonValue? now)
    {
        if (now is null)
        {
            return Timestamps.ToIsoString(clock.UtcNow);
        }

        var ms = JsConvert.ToNumber(now);
        if (!double.IsFinite(ms) || Math.Abs(ms) > 8.64e15)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Invalid time value");
        }

        return JsOps.IsoString(ms);
    }
}
