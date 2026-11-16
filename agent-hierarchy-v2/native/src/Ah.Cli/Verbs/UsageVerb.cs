using System.Globalization;
using System.Text;
using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah usage [--json] [--cwd &lt;dir&gt;]</c>: the account's rate-limit windows, from the newest statusline tap sample.
/// Exit 0, or 2 for a bad argument. Reads only.
/// </summary>
internal static class UsageVerb
{
    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr) =>
        Run(context, args, stdout, stderr, TimeProvider.System.GetUtcNow(), TimeZoneInfo.Local);

    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr, DateTimeOffset now, TimeZoneInfo zone)
    {
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--cwd" when i + 1 < args.Length:
                    i++;
                    break;
                default:
                    stderr.WriteLine($"ah usage: unexpected argument {args[i]}; usage: ah usage [--json] [--cwd <dir>]");
                    return 2;
            }
        }

        var reading = WatchModule.ReadUsage(context.Env.HomeDirectory, now.ToUnixTimeMilliseconds());
        stdout.Write(json ? Json(reading) : Text(reading, zone));
        return 0;
    }

    private static string Json(UsageReading r)
    {
        var windows = new JsonArray();
        foreach (var w in r.Windows)
        {
            windows.Add(new JsonObject
            {
                ["name"] = w.Name,
                ["used_percentage"] = JsonValue.FromNumber(w.Pct),
                ["resets_at"] = w.ResetsAtMs is { } ms ? JsonValue.FromString(DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)) : JsonValue.Null,
                ["reset"] = w.Reset,
            });
        }

        var o = new JsonObject
        {
            ["stale"] = r.Stale,
            ["age_seconds"] = r.AgeSeconds is { } age ? JsonValue.FromNumber(Math.Round(age)) : JsonValue.Null,
            ["session"] = r.Session is { } s ? JsonValue.FromString(s) : JsonValue.Null,
            ["windows"] = windows,
        };
        return JsonText.Stringify(o, 0) + "\n";
    }

    private static string Text(UsageReading r, TimeZoneInfo zone)
    {
        var sb = new StringBuilder("usage:\n");
        if (r.Session is null)
        {
            sb.Append("  no sample: the statusline tap has no rate limits yet (an API-key account, or no response since the tap was turned on)\n");
            return sb.ToString();
        }

        foreach (var w in r.Windows)
        {
            var label = w.Name == "five_hour" ? "5h" : "7d";
            var when = w.ResetsAtMs is { } ms ? TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)ms), zone).ToString("ddd HH:mm", CultureInfo.InvariantCulture) : "?";
            sb.Append(w.Reset ? $"  {label}: reset (0%)\n" : string.Create(CultureInfo.InvariantCulture, $"  {label}: {w.Pct:0.#}% (resets {when})\n"));
        }

        var age = TimeSpan.FromSeconds(r.AgeSeconds ?? 0);
        sb.Append(CultureInfo.InvariantCulture, $"  sample: {(r.Stale ? "stale, " : string.Empty)}{(int)age.TotalMinutes} min old, session {r.Session[..Math.Min(8, r.Session.Length)]}\n");
        return sb.ToString();
    }
}
