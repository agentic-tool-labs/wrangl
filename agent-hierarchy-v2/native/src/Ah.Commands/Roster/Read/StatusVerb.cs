using System.Globalization;
using Ah.Core.Json;

namespace Ah.Commands;

internal static class StatusVerb
{
    /// <summary>
    /// The hierarchy status document: computed from the database and printed.
    /// <c>--now</c> evaluates it as of that instant; <c>--plain</c> prints the one-line form.
    /// </summary>
    internal static int Run(RosterContext c)
    {
        var nowMs = (double)c.Services.Clock.UtcNow.ToUnixTimeMilliseconds();
        if (c.Opts.Has("now"))
        {
            var parsed = c.Opts.Text("now") is { } text ? ParseInstant(text) : double.NaN;
            if (double.IsNaN(parsed))
            {
                var shown = c.Opts.Text("now") is { } t ? JsonText.Quote(t) : "true";
                throw new RosterFailure($"status: --now takes an ISO-8601 time, got {shown}");
            }

            nowMs = parsed;
        }

        var status = c.Services.Hierarchy.Status;
        var computeStart = Ah.Core.Time.PhaseTrace.Begin();
        var document = status.ReadStatus(c.Cwd, nowMs);
        Ah.Core.Time.PhaseTrace.End("status.compute", computeStart);
        var jobsStart = Ah.Core.Time.PhaseTrace.Begin();
        MarkBlockedOnJob(c, document, nowMs);
        Ah.Core.Time.PhaseTrace.End("status.blocked-on-job", jobsStart);
        var goals = MarkGoals(c, document);
        if (c.Opts.IsTrue("plain"))
        {
            c.Stdout.Write(status.PlainStatus(document, nowMs));
            c.Stdout.Write('\n');
            if (document.Get("backlog") is { IsMap: true } backlog)
            {
                c.Stdout.Write(backlog.Member("due") is { IsTruthy: true } ? "backlog: due\n" : "backlog: ok\n");
            }

            foreach (var (team, text) in goals)
            {
                c.Stdout.Write($"goal ({team}): {Clip(text, PlainGoalCap)}\n");
            }
        }
        else
        {
            c.Out(document);
        }

        return 0;
    }

    /// <summary>
    /// A live member that owns an overdue job is shown as <c>blocked-on-job</c>: its row gains <c>state</c> and <c>blocked_job</c> (<c>id</c>, <c>kind</c>,
    /// <c>label</c>, <c>eta</c>), from the same derivation the member verdict uses. The job facts live above the hierarchy library, so this is composed
    /// onto the printed document; the status file the library writes does not carry it.
    /// </summary>
    private static void MarkBlockedOnJob(RosterContext c, JsonObject document, double nowMs)
    {
        if (document.Get("teams") is not { IsArray: true } teams)
        {
            return;
        }

        foreach (var team in teams.AsArray().Where(static t => t.IsMap))
        {
            var name = team.AsObject().Get("team") is { IsText: true } text ? text.AsString() : null;
            if (team.AsObject().Get("members") is not { IsArray: true } members)
            {
                continue;
            }

            var jobs = Ah.Watch.WatchModule.JobsFromFiles(c.Services.Hierarchy.Status, c.Dir, name);
            foreach (var member in members.AsArray().Where(static m => m.IsMap))
            {
                var row = member.AsObject();
                if (row.Get("live") is { IsBool: true } live && live.AsBool() && row.Get("name") is { IsText: true } who
                    && Ah.Watch.WatchModule.JobOverdueOf(jobs, who.AsString(), eta => c.Services.Hierarchy.Messages.ThresholdFor(JsonValue.FromString(eta)), nowMs, 30000) is { } blocked)
                {
                    row["state"] = "blocked-on-job";
                    row["blocked_job"] = JsonValue.FromObject(new JsonObject { ["id"] = blocked.Id, ["kind"] = blocked.Kind, ["label"] = blocked.Label, ["eta"] = blocked.Eta });
                }
            }
        }
    }

    /// <summary>How many code points of a goal the <c>--plain</c> line keeps; <c>ah rules goal</c> prints it whole.</summary>
    internal const int PlainGoalCap = 100;

    /// <summary>
    /// Each named team whose chain has a goal (ah2-53) gains <c>goal</c> (<c>text</c>, <c>done_when</c>, <c>team</c>), composed onto the printed document as
    /// <see cref="MarkBlockedOnJob"/> is, never into the status compute. A goal that cannot be read adds nothing. Returns each such team's name and goal text.
    /// </summary>
    private static List<(string Team, string Text)> MarkGoals(RosterContext c, JsonObject document)
    {
        var shown = new List<(string, string)>();
        if (document.Get("teams") is not { IsArray: true } teams)
        {
            return shown;
        }

        foreach (var team in teams.AsArray().Where(static t => t.IsMap))
        {
            if (team.AsObject().Get("team") is { IsText: true } name && c.Services.TeamGoal(c.Cwd, name.AsString()) is { } goal && goal.Get("text") is { IsText: true } text)
            {
                team.AsObject()["goal"] = JsonValue.FromObject(goal);
                shown.Add((name.AsString(), text.AsString()));
            }
        }

        return shown;
    }

    /// <summary>One inert line of <paramref name="text"/>, cut to <paramref name="cap"/> code points with a closing ellipsis when longer.</summary>
    private static string Clip(string text, int cap)
    {
        var runes = Ah.Core.Text.SafeText.Line(text).EnumerateRunes().ToList();
        return runes.Count <= cap ? string.Concat(runes) : string.Concat(runes.Take(cap - 1)) + "…";
    }

    /// <summary>The instant of an ISO-8601 time in milliseconds since the epoch, or NaN when it does not parse.</summary>
    private static double ParseInstant(string text)
    {
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        string[] formats =
        [
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
            "yyyy-MM-dd'T'HH:mm:ssK",
            "yyyy-MM-dd'T'HH:mmK",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm",
            "yyyy-MM-dd",
        ];
        if (DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, styles, out var exact))
        {
            return exact.ToUnixTimeMilliseconds();
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out var loose)
            ? loose.ToUnixTimeMilliseconds()
            : double.NaN;
    }
}
