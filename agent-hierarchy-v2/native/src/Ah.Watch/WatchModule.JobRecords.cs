using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>What the long-job catalogue says of a command: the kind of job it starts, the eta the clock reads and a label for the alert.</summary>
/// <param name="Kind">One of test, build, slot, smoke, bg.</param>
/// <param name="Eta">The eta word.</param>
/// <param name="Label">The command, cut to a line the alert can carry.</param>
/// <param name="Background">Whether the owner's turn is free while the job runs.</param>
public sealed record JobSpec(string Kind, string Eta, string Label, bool Background);

/// <summary>A job record and its key (per-dir-files-to-db S8: a <c>jobs</c> row, no file).</summary>
/// <param name="Team">The team's jobs folder name (<c>_default</c> for the default team), the key's first half.</param>
/// <param name="Id">The job's id, the key's second half.</param>
/// <param name="Record">The parsed record.</param>
public sealed record JobRecordFile(string Team, string Id, JsonObject Record);

public static partial class WatchModule
{
    private const int LabelLimit = 120;

    [GeneratedRegex("(^|[;&|(])[ \t]*dotnet[ \t]+build([ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetBuild();

    [GeneratedRegex("(^|[;&|(])[ \t]*(bash[ \t]+|sh[ \t]+)?([^ \t]*/)?(dotnet-test\\.sh|tests/[^ \t]+\\.sh)([ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex TestScript();

    [GeneratedRegex("--eta[ \t=]+([A-Za-z0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex EtaFlag();

    /// <summary>
    /// The long-job catalogue: <c>dotnet test</c>, <c>dotnet build</c>, the <c>with-slot.sh</c> wrapper, the dotnet-test script and the
    /// repository's test scripts, or any command run in the background. A test run is what the registry of the session's repository and user says. Null for anything else. The eta is the command's own
    /// <c>--eta</c> when it carries one, else the kind's default (test large, everything else medium).
    /// </summary>
    public static JobSpec? JobMatch(string? command, bool runInBackground, string? home = null, string? cwd = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var label = command.Trim().ReplaceLineEndings(" ");
        if (label.Length > LabelLimit)
        {
            label = label[..LabelLimit];
        }

        string? kind = CommandSegments.Of(command).Any(static s => s.Wrappers.Contains("bucket")) ? "slot"
            : TestRegistry.For(home, cwd).IsTestRun(command) ? "test"
            : DotnetBuild().IsMatch(command) ? "build"
            : TestScript().IsMatch(command) ? "smoke"
            : runInBackground ? "bg"
            : null;
        if (kind is null)
        {
            return null;
        }

        var eta = EtaFlag().Match(command) is { Success: true } m ? EtaWord(m.Groups[1].Value) : null;
        return new JobSpec(kind, eta ?? (kind == "test" ? "large" : "medium"), label, runInBackground || kind == "bg");
    }

    /// <summary>
    /// Whether <paramref name="command"/> is exactly one wake source (<c>ah roster wake</c> or <c>ah roster overdue</c>, run as one of <paramref name="ahBinaries"/>) or a bare
    /// <c>sleep N</c>, none of which is a job. <paramref name="realPath"/> gives the real file the program word names, or null when it names none.
    /// </summary>
    public static bool JobExcluded(string? command, Func<string, string?> realPath, IReadOnlyCollection<string> ahBinaries, Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(realPath);
        ArgumentNullException.ThrowIfNull(ahBinaries);
        return JobExclusion.IsExcluded(command, realPath, ahBinaries, fileExists);
    }

    /// <summary>Whether the record is a background job: its <c>background</c> flag, else whether its kind is <c>bg</c>.</summary>
    public static bool JobRecordBackground(JsonObject record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Get("background") is { IsBool: true } flag ? flag.AsBool() : record.Get("kind") is { IsText: true } kind && kind.AsString() == "bg";
    }

    private static string? EtaWord(string text) =>
        text is "small" or "medium" or "large" ? text
        : double.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds <= 300 ? "small" : seconds <= 600 ? "medium" : "large"
        : null;

    /// <summary>The record of a job that starts now.</summary>
    public static JsonObject JobNewRecord(string id, string team, string owner, string session, JobSpec spec, int? pid, string nowIso, string? toolUseId, string? capIso)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var job = new JsonObject
        {
            ["id"] = id,
            ["team"] = team,
            ["owner"] = owner,
            ["session_id"] = session,
            ["kind"] = spec.Kind,
            ["label"] = spec.Label,
            ["pid"] = pid is { } p ? p : JsonValue.Null,
            ["eta"] = spec.Eta,
            ["sent_at"] = nowIso,
            ["background"] = spec.Background,
        };
        if (capIso is not null)
        {
            job["cap_at"] = capIso;
        }

        if (toolUseId is not null)
        {
            job["tool_use_id"] = toolUseId;
        }

        return job;
    }

    /// <summary>The open record of this owner and session that the same tool call or the same process already made, or null: one record per run.</summary>
    public static JobRecordFile? JobFindOpen(IReadOnlyList<JobRecordFile> records, string owner, string session, string? toolUseId, int? pid)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (toolUseId is null && pid is null)
        {
            return null;
        }

        static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : null;
        return records.FirstOrDefault(r =>
            r.Record.Get("ended_at") is not { IsText: true }
            && Text(r.Record, "owner") == owner
            && Text(r.Record, "session_id") == session
            && ((toolUseId is not null && Text(r.Record, "tool_use_id") == toolUseId)
                || (pid is { } p && r.Record.Get("pid") is { IsNumber: true } n && (int)n.AsNumber() == p)));
    }

    /// <summary>The open record the tool call <paramref name="toolUseId"/> made, or null. The id is unique per call, so the owner and session a later hook resolves do not have to match.</summary>
    public static JobRecordFile? JobFindOpenByToolUse(IReadOnlyList<JobRecordFile> records, string toolUseId)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.FirstOrDefault(r =>
            r.Record.Get("ended_at") is not { IsText: true }
            && r.Record.Get("tool_use_id") is { IsText: true } id && id.AsString() == toolUseId);
    }

    /// <summary>The record with the process and tool call it did not yet know.</summary>
    public static JsonObject JobMerged(JsonObject record, int? pid, string? toolUseId, string? pidStart = null, int? pgid = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var next = record.Clone();
        if (pid is { } p)
        {
            next["pid"] = p;
        }

        if (pidStart is not null)
        {
            next["pid_start"] = pidStart;
        }

        if (pgid is { } group)
        {
            next["pgid"] = group;
        }

        if (toolUseId is not null)
        {
            next["tool_use_id"] = toolUseId;
        }

        return next;
    }

    /// <summary>The record with its test-slot phase set, or cleared when <paramref name="slot"/> is null.</summary>
    public static JsonObject JobSlotted(JsonObject record, JobSlot? slot)
    {
        ArgumentNullException.ThrowIfNull(record);
        var next = record.Clone();
        if (slot is null)
        {
            _ = next.Remove("slot");
            return next;
        }

        next["slot"] = new JsonObject
        {
            ["phase"] = slot.Phase,
            ["since_ms"] = slot.SinceMs,
            ["waited_ms"] = slot.WaitedMs,
            ["pool"] = slot.Pool,
            ["holder"] = slot.Holder,
            ["tokens"] = slot.Tokens,
            ["free"] = slot.Free,
            ["capacity"] = slot.Capacity,
            ["ahead"] = slot.Ahead,
        };
        return next;
    }

    /// <summary>The record, ended now.</summary>
    public static JsonObject JobEnded(JsonObject record, string nowIso, string? exit)
    {
        ArgumentNullException.ThrowIfNull(record);
        var next = record.Clone();
        next["ended_at"] = nowIso;
        if (exit is not null)
        {
            next["exit"] = exit;
        }

        return next;
    }

    /// <summary>
    /// The fields <paramref name="after"/> changes from <paramref name="before"/>: each added or changed one, and a null for each removed, for a put
    /// with <c>ifOpen</c> to merge onto the stored record (b417: the tick never writes back a field a hook changed since its read).
    /// </summary>
    public static JsonObject JobPatch(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var patch = new JsonObject();
        foreach (var key in after.Keys)
        {
            var value = after.Get(key)!.Value;
            if (before.Get(key) is not { } old || JsonText.Stringify(old, 0) != JsonText.Stringify(value, 0))
            {
                patch[key] = value;
            }
        }

        foreach (var key in before.Keys.Where(k => !after.ContainsKey(k)))
        {
            patch[key] = JsonValue.Null;
        }

        return patch;
    }

    /// <summary>The record with a new eta whose clock starts now.</summary>
    public static JsonObject JobExtended(JsonObject record, string eta, string nowIso)
    {
        ArgumentNullException.ThrowIfNull(record);
        var next = record.Clone();
        next["eta"] = eta;
        next["extended_at"] = nowIso;
        return next;
    }

    /// <summary>
    /// The records of the team jobs folder <paramref name="dir"/> (<c>&lt;hierarchy&gt;/jobs/&lt;team&gt;</c>; per-dir-files-to-db S8: the <c>jobs</c>
    /// rows) that parse as objects, in id order; an unreadable one is skipped.
    /// </summary>
    public static IReadOnlyList<JobRecordFile> JobRecordFiles(Ah.Hierarchy.IHierarchyStatus status, string dir, bool mustAnswer = false)
    {
        ArgumentNullException.ThrowIfNull(status);
        var team = Path.GetFileName(dir);
        var found = new List<JobRecordFile>();
        foreach (var row in status.JobRows(Path.GetDirectoryName(Path.GetDirectoryName(dir)) ?? dir, mustAnswer).Where(r => r.Team == team).OrderBy(static r => r.Id, StringComparer.Ordinal))
        {
            try
            {
                if (JsonText.Parse(row.Body) is { IsMap: true } value)
                {
                    found.Add(new JobRecordFile(row.Team, row.Id, value.AsObject()));
                }
            }
            catch (JsonParseException)
            {
                continue;
            }
        }

        return found;
    }

    /// <summary>The cap time <paramref name="seconds"/> after <paramref name="nowIso"/>.</summary>
    public static string JobCapIso(string nowIso, double seconds) =>
        Timestamps.ToIsoString(DateTimeOffset.Parse(nowIso, CultureInfo.InvariantCulture).AddSeconds(seconds));
}
