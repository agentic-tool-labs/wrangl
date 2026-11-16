using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>One rate-limit window of the newest sample.</summary>
/// <param name="Name"><c>five_hour</c> or <c>seven_day</c>.</param>
/// <param name="Pct">Percent used; 0 once the window has reset.</param>
/// <param name="ResetsAtMs">When the window resets, in epoch milliseconds, or null when the sample names none.</param>
/// <param name="Reset">Whether <paramref name="ResetsAtMs"/> has passed, so the sampled percentage no longer applies.</param>
public sealed record UsageWindow(string Name, double Pct, double? ResetsAtMs, bool Reset);

/// <summary>What the newest statusline tap sample says of the account's rate limits.</summary>
/// <param name="Session">The session whose tap file is the sample, or null with no sample.</param>
/// <param name="AgeSeconds">How old the sample is, or null with no sample.</param>
/// <param name="Stale">Whether the sample is older than <see cref="UsageReader.StaleAfter"/>; its windows are still listed.</param>
/// <param name="Windows">The sample's five-hour and seven-day windows that it carries.</param>
public sealed record UsageReading(string? Session, double? AgeSeconds, bool Stale, IReadOnlyList<UsageWindow> Windows);

/// <summary>
/// Reads the rate limits Claude Code reports in the statusline input, from the files the statusline tap keeps under
/// <c>&lt;home&gt;/.claude/ah2/statusline</c>; the parse is that of <see cref="StatuslineTap"/>.
/// </summary>
internal static class UsageReader
{
    /// <summary>A sample older than this no longer says where the account stands.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private static readonly string[] Counted = ["five_hour", "seven_day"];

    /// <summary>The newest sample that carries rate limits, among the tap files under <paramref name="home"/>, judged at <paramref name="nowMs"/>.</summary>
    public static UsageReading Read(string home, double nowMs)
    {
        var dir = Path.Join(home, ".claude", "ah2", "statusline");
        var tap = StatuslineTap.OnDisk(home, () => nowMs);
        foreach (var (session, mtimeMs) in Sessions(dir))
        {
            var reading = tap.Read(session);
            if (reading.RateLimits is not { Count: > 0 } limits)
            {
                continue;
            }

            var windows = limits.Where(l => Counted.Contains(l.Name, StringComparer.Ordinal)).Select(l => Window(l, nowMs)).ToList();
            if (windows.Count == 0)
            {
                continue;
            }

            var age = Math.Max(0, (nowMs - mtimeMs) / 1000);
            return new UsageReading(session, age, age > StaleAfter.TotalSeconds, windows);
        }

        return new UsageReading(null, null, false, []);
    }

    private static UsageWindow Window(TapRateLimit limit, double nowMs)
    {
        double? resets = limit.ResetsAt is { } iso && Timestamps.TryParseMs(iso, out var ms) ? ms : null;
        var reset = resets is { } r && r <= nowMs;
        return new UsageWindow(limit.Name, reset ? 0 : limit.Pct, resets, reset);
    }

    /// <summary>The tap files' session ids, newest first.</summary>
    private static List<(string Session, double MtimeMs)> Sessions(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*.json")
                .Where(static f => f.LinkTarget is null)
                .Select(static f => (Session: Path.GetFileNameWithoutExtension(f.Name), MtimeMs: (double)new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds()))
                .OrderByDescending(static f => f.MtimeMs)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
