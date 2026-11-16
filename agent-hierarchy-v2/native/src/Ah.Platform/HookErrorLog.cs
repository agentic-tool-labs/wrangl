using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Platform;

/// <summary>
/// <c>logHookError</c>: appends one JSON line to <c>~/.claude/ah2/hierarchy/hook-errors.jsonl</c> and moves the file to
/// <c>.1</c> first when it is over 1 MiB. A newer-schema refusal the log already holds is not written again: every hook of an old binary
/// hits it. Nothing here ever throws; a logger that fails has nowhere left to report to.
/// </summary>
internal sealed class HookErrorLog(IClock clock, IAhEnvironment environment, IFileReader files, IRawFileWriter writer, IRecordAppender appender) : IHookErrorLog
{
    private const string NewerSchema = "newer than this binary's";

    internal const long CapBytes = RotatedJsonl.CapBytes;

    public void Log(string source, string message) => Log(new HookErrorRecord(source, message));

    public void Log(HookErrorRecord entry)
    {
        try
        {
            var stack = new JsonArray();
            foreach (var frame in entry.Stack ?? [])
            {
                stack.Add(frame);
            }

            var line = new JsonObject
            {
                ["ts"] = Timestamps.ToIsoString(clock.UtcNow),
                ["hook"] = entry.Hook,
                ["event"] = entry.EventName is null ? JsonValue.Null : entry.EventName,
                ["session_id"] = entry.SessionId is null ? JsonValue.Null : entry.SessionId,
                ["message"] = entry.Message,
                ["stack"] = stack,
            };
            var path = Ah.Core.Text.AhPaths.HookErrorLog(environment.HomeDirectory);
            if (entry.Message.Contains(NewerSchema, StringComparison.Ordinal)
                && files.Stat(path) is not null
                && files.ReadText(path).Contains(JsonText.Stringify(line["message"], 0), StringComparison.Ordinal))
            {
                return;
            }

            RotatedJsonl.Append(writer, appender, path, JsonText.ToUtf8(line, 0));
        }
        catch (Exception)
        {
            // the last link in the chain
        }
    }
}
