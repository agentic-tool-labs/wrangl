using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>A tap file's text and its modification time.</summary>
internal sealed record TapFile(string Text, double MtimeMs);

/// <summary>One rate-limit window from the statusline input.</summary>
/// <param name="Name"><c>five_hour</c>, <c>seven_day</c> or <c>spend_limit</c>.</param>
/// <param name="Pct">Percent used, 0 to 100.</param>
/// <param name="ResetsAt">The ISO instant the window resets, or null.</param>
internal sealed record TapRateLimit(string Name, double Pct, string? ResetsAt);

/// <summary>The context window as the statusline input reports it.</summary>
/// <param name="Window">Tokens in the window.</param>
/// <param name="Pct">Percent used, 0 to 100.</param>
/// <param name="Used">Input plus cache tokens of the latest call, or null when the input carries no usage.</param>
internal sealed record TapContext(double Window, double Pct, long? Used);

/// <summary>The model the statusline input names.</summary>
internal sealed record TapModel(string Id, string? Display);

/// <summary>
/// What a session's tap file serves. <see cref="OffReason"/> is set when nothing is served (<c>tap off</c> or
/// <c>statusline data stale</c>); otherwise every other member is null when the input held no such field, and
/// <see cref="Stale"/> says the file is past the fresh window.
/// </summary>
internal sealed record TapReading(string? OffReason, string? AsOf, bool Stale, IReadOnlyList<TapRateLimit>? RateLimits, TapContext? Context, TapModel? Model = null, string? Effort = null, string? Cwd = null)
{
    internal const string TapOff = "tap off";
    internal const string DataStale = "statusline data stale";
    internal const string NotInData = "not in statusline data";

    internal static TapReading Off(string reason) => new(reason, null, false, null, null);
}

/// <summary>
/// Reads the file the statusline tap writes for a session (<c>~/.claude/ah2/statusline/&lt;session_id&gt;.json</c>), read only. The
/// field names are those of Claude Code's statusline input documentation.
/// </summary>
internal sealed partial class StatuslineTap(Func<string, TapFile?> read, Func<double> nowMs, Action<string>? log = null)
{
    internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ServedFor = TimeSpan.FromMinutes(60);
    internal static readonly TimeSpan LogEvery = TimeSpan.FromHours(1);

    private static readonly string[] Windows = ["five_hour", "seven_day", "spend_limit"];

    private readonly Dictionary<string, double> _logged = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,128}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SessionName();

    /// <summary>A tap reader over <c>&lt;home&gt;/.claude/ah2/statusline</c>.</summary>
    internal static StatuslineTap OnDisk(string home, Func<double> nowMs, Action<string>? log = null) =>
        new(session => ReadFile(Path.Join(home, ".claude", "ah2", "statusline", session + ".json")), nowMs, log);

    private static TapFile? ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 1 << 20 ? new TapFile(File.ReadAllText(path), new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal TapReading Read(string? sessionId)
    {
        if (sessionId is null || !SessionName().IsMatch(sessionId) || read(sessionId) is not { } file)
        {
            return TapReading.Off(TapReading.TapOff);
        }

        JsonValue doc;
        try
        {
            doc = JsonText.Parse(file.Text);
        }
        catch (JsonParseException)
        {
            doc = JsonValue.Null;
        }

        if (!doc.IsMap)
        {
            LogOnce(sessionId);
            return TapReading.Off(TapReading.TapOff);
        }

        var age = nowMs() - file.MtimeMs;
        if (age > ServedFor.TotalMilliseconds)
        {
            return TapReading.Off(TapReading.DataStale);
        }

        var limits = new List<TapRateLimit>();
        if (doc.Member("rate_limits") is { IsMap: true } rate)
        {
            foreach (var name in Windows)
            {
                if (rate.Member(name) is { IsMap: true } one && Number(one.Member("used_percentage")) is { } pct)
                {
                    limits.Add(new TapRateLimit(name, pct, Number(one.Member("resets_at")) is { } at ? IsoTime.Seconds(at * 1000) : null));
                }
            }
        }

        TapContext? context = null;
        if (doc.Member("context_window") is { IsMap: true } cw && Number(cw.Member("context_window_size")) is > 0 and var size && Number(cw.Member("used_percentage")) is { } used)
        {
            context = new TapContext(size, used, UsedTokens(cw.Member("current_usage")));
        }

        TapModel? model = doc.Member("model") is { IsMap: true } m && Text(m.Member("id")) is { } id ? new TapModel(id, Text(m.Member("display_name"))) : null;
        var cwd = Text(doc.Member("workspace")?.Member("current_dir")) ?? Text(doc.Member("cwd"));
        var effort = doc.Member("effort") is { IsMap: true } e ? Text(e.Member("level")) : null;
        var asOf = IsoTime.Seconds(file.MtimeMs);
        return new TapReading(null, asOf, age >= FreshFor.TotalMilliseconds, limits.Count > 0 ? limits : null, context, model, effort, cwd);
    }

    private static double? Number(JsonValue? v) => v is { IsNumber: true } n && double.IsFinite(n.AsNumber()) ? n.AsNumber() : null;

    private static string? Text(JsonValue? v) => v is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;

    /// <summary>Input, cache-creation and cache-read tokens of the latest call; null when the input holds no usage object.</summary>
    private static long? UsedTokens(JsonValue? usage) =>
        usage is { IsMap: true } u
            ? (long)((Number(u.Member("input_tokens")) ?? 0) + (Number(u.Member("cache_creation_input_tokens")) ?? 0) + (Number(u.Member("cache_read_input_tokens")) ?? 0))
            : null;

    private void LogOnce(string sessionId)
    {
        var now = nowMs();
        lock (_logged)
        {
            if (_logged.TryGetValue(sessionId, out var last) && now - last < LogEvery.TotalMilliseconds)
            {
                return;
            }

            _logged[sessionId] = now;
        }

        log?.Invoke($"statusline tap file for session {sessionId} could not be parsed; serving it as tap off");
    }
}
