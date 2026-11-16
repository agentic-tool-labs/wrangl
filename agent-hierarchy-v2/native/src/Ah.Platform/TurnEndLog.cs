using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Platform;

/// <summary>Appends one JSON line per traced turn end to a rotated file. Nothing here throws.</summary>
internal sealed class TurnEndLog(IClock clock, string path, IRawFileWriter writer, IRecordAppender appender) : ITurnEndLog
{
    internal const int ActionLimit = 300;

    public void Record(TurnEndRecord record)
    {
        try
        {
            var action = record.Action.Length > ActionLimit ? record.Action[..ActionLimit] : record.Action;
            var line = new JsonObject
            {
                ["ts"] = Timestamps.ToIsoString(clock.UtcNow),
                ["event"] = record.Event,
                ["session"] = record.Session,
                ["agent_id"] = record.AgentId is null ? JsonValue.Null : record.AgentId,
                ["dir"] = record.Dir is null ? JsonValue.Null : record.Dir,
                ["error"] = record.Error is null ? JsonValue.Null : record.Error,
                ["turn_end_key"] = record.TurnEndKey is null ? JsonValue.Null : record.TurnEndKey,
                ["action"] = action,
            };
            RotatedJsonl.Append(writer, appender, path, JsonText.ToUtf8(line, 0));
        }
        catch (Exception)
        {
            // a trace that fails has nowhere to report to, and must not change what the caller does
        }
    }
}
