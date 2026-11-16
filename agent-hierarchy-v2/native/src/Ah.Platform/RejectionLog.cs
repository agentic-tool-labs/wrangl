using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Platform;

/// <summary>
/// Appends one JSON line per refused daemon request to a rotated file. The line carries what identifies the request and the
/// refusal, never the request's body. A refusal that repeats (same method, path, status, code and caller) within a minute of its
/// last line is counted instead of written, and the next line for it carries that count as "repeated". Nothing here throws.
/// </summary>
internal sealed class RejectionLog(IClock clock, string path, IRawFileWriter writer, IRecordAppender appender) : IRejectionLog
{
    internal const int MessageLimit = 500;
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);
    private const int MaxKeys = 256;

    private readonly object gate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Suppressed)> seen = new();

    public void Record(RejectionRecord record)
    {
        try
        {
            var now = clock.UtcNow;
            var key = $"{record.Method} {record.Path} {record.Status} {record.Code} {record.Caller}";
            int repeated;
            lock (gate)
            {
                if (seen.TryGetValue(key, out var last) && now >= last.Start && now - last.Start < RepeatWindow)
                {
                    seen[key] = (last.Start, last.Suppressed + 1);
                    return;
                }

                repeated = seen.TryGetValue(key, out last) ? last.Suppressed : 0;
                if (seen.Count >= MaxKeys) seen.Clear();
                seen[key] = (now, 0);
            }

            var message = record.Message is { Length: > MessageLimit } long_ ? long_[..MessageLimit] : record.Message;
            var line = new JsonObject
            {
                ["ts"] = Timestamps.ToIsoString(now),
                ["method"] = record.Method,
                ["path"] = record.Path,
                ["status"] = record.Status,
                ["code"] = record.Code,
                ["message"] = message is null ? JsonValue.Null : message,
                ["field"] = record.Field is null ? JsonValue.Null : record.Field,
                ["peer_pid"] = record.PeerPid is { } pid ? pid : JsonValue.Null,
                ["client_version"] = record.ClientVersion is null ? JsonValue.Null : record.ClientVersion,
                ["caller"] = record.Caller is null ? JsonValue.Null : record.Caller,
            };
            if (repeated > 0) line["repeated"] = repeated;
            RotatedJsonl.Append(writer, appender, path, JsonText.ToUtf8(line, 0));
        }
        catch (Exception)
        {
            // a log that fails has nowhere to report to, and must not change the reply
        }
    }
}
