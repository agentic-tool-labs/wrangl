using System.Globalization;
using System.Text;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Watch;

namespace Ah.Commands;

/// <summary>
/// <c>roster stall-diagnose &lt;request id&gt;</c>: reads the files and prints why ahd did not wake the Orchestrator about a dispatch, as
/// six facts and one verdict line. It writes one journal line and nothing else, and asks the daemon nothing.
/// </summary>
internal static class StallDiagnoseVerb
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "cwd", "team" };

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"stall-diagnose: unrecognized flag --{key} (use --cwd or --team)");
            }
        }

        var wake = c.Services.Wake ?? throw new RosterFailure("stall-diagnose: not available here");
        var id = c.Opts.Positionals.LastOrDefault(static p => p != "stall-diagnose") ?? throw new RosterFailure("stall-diagnose: needs a request id");
        var session = c.EnvText("CLAUDE_CODE_SESSION_ID");
        (string Address, string Dir)? own = session is null ? null : wake.OrchestratorBox(session);
        var team = own is { } o ? o.Address[(o.Address.IndexOf('@', StringComparison.Ordinal) + 1)..] : c.Opts.Text("team") ?? throw new RosterFailure("stall-diagnose: run it from the Orchestrator's session or pass --team");
        var box = own is { } mine ? (mine.Address, mine.Dir, Session: session!) : wake.OrchestratorBoxOfTeam(c.Dir, team) ?? throw new RosterFailure($"stall-diagnose: team {team} has no registered Orchestrator");

        var dispatch = wake.OpenDispatches(c.Dir, team, box.Session).FirstOrDefault(d => d.RequestId == id)
            ?? throw new RosterFailure($"stall-diagnose: no open dispatch {id} in team {team}");
        var now = c.Services.Clock.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var eta = WatchModule.ComputeEtaState(
            dispatch.RequestId, dispatch.Eta, dispatch.ThresholdSeconds, dispatch.Created, dispatch.ToAddr, dispatch.FloorMs, dispatch.Session,
            dispatch.PeerRecords, dispatch.Gates, false, dispatch.HeldUntilMs, nowMs, 15_000);
        var events = wake.Events(box.Dir, box.Session).Where(e => e.RequestId == id).ToList();
        var ahdPid = AhdPid(wake);
        var daemonUp = wake.DaemonUp();
        var ticks = ReadTicks(wake);
        var waiterLive = WaiterLive(wake, box.Session);
        var member = wake.MemberVerdict(c.Dir, team, dispatch.ToName);
        var facts = new DiagnoseFacts(
            daemonUp, ahdPid, AgeOf(ticks, "watch-tick", nowMs), AgeOf(ticks, "stall-watch", nowMs), PollMs(c), eta, events, waiterLive, nowMs);
        var verdict = WatchModule.Diagnose(facts);

        string Iso(double ms) => Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)ms));
        var lines = new List<string>
        {
            daemonUp ? $"1 ahd: up, pid {(ahdPid?.ToString(CultureInfo.InvariantCulture) ?? "?")}" : "1 ahd: down",
            $"2 jobs: watch-tick {Describe(facts.WatchTickAgeMs)}; stall-watch {Describe(facts.StallWatchAgeMs)}",
            $"3 dispatch: {id} {dispatch.Slug} → {dispatch.ToName} eta {dispatch.Eta} sent {dispatch.Created ?? "?"} due {Iso(eta.DueMs)} next_nudge {(eta.Next is { } n ? $"{n.Step} at {Iso(n.AtMs)}" : "none")} state {eta.State}",
            events.Count == 0
                ? "4 events: none for this id"
                : "4 events: " + string.Join("; ", events.Select(e => $"{e.Step ?? "?"} {e.Id} created {e.Created ?? "?"} {(e.AckedAtMs is null ? "unacknowledged" : "acknowledged")}")),
            $"5 waiter: {(waiterLive ? "live" : "dead")}",
            $"6 member: {member}",
            $"verdict: {verdict.Cause} — {verdict.Evidence}",
        };
        foreach (var line in lines)
        {
            c.Stdout.WriteLine(line);
        }

        var row = new JsonObject
        {
            ["at"] = Timestamps.ToIsoString(now),
            ["team"] = team,
            ["kind"] = "ahd-bug",
            ["subject"] = id,
            ["evidence"] = string.Join(" | ", lines),
        };
        wake.Journal(row);
        return 0;
    }

    private static string Describe(double? ageMs) => ageMs is null ? "never ran" : $"{(ageMs.Value / 1000).ToString("0", CultureInfo.InvariantCulture)}s ago";

    private static double PollMs(RosterContext c)
    {
        foreach (var name in new[] { "AH_WATCH_POLL_MS", "AHD_WATCH_POLL_MS" })
        {
            if (c.Services.Environment.GetVariable(name) is { } raw && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0)
            {
                return n;
            }
        }

        return 15_000;
    }

    private static int? AhdPid(IRosterWake wake)
    {
        try
        {
            return wake.ReadText(Path.Combine(wake.AhHome, "run", "ahd.json")) is { } text
                && JsonText.Parse(Encoding.UTF8.GetBytes(text)) is { IsMap: true } info && info.AsObject().Get("pid") is { IsNumber: true } pid
                    ? (int)pid.AsNumber()
                    : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    private static JsonObject? ReadTicks(IRosterWake wake)
    {
        try
        {
            return wake.ReadState("stall.ticks") is { } text
                && JsonText.Parse(Encoding.UTF8.GetBytes(text)) is { IsMap: true } doc
                    ? doc.AsObject()
                    : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    private static double? AgeOf(JsonObject? ticks, string job, double nowMs) =>
        ticks?.Get(job) is { IsMap: true } row && row.AsObject().Get("last_run_at") is { IsText: true } at && Timestamps.TryParseMs(at.AsString(), out var ms)
            ? Math.Max(0, nowMs - ms)
            : null;

    private static bool WaiterLive(IRosterWake wake, string session)
    {
        try
        {
            return wake.ReadText(WakeFiles.PathOf(wake.AhHome, $"{session}.json")) is { } text
                && JsonText.Parse(Encoding.UTF8.GetBytes(text)) is { IsMap: true } doc
                && doc.AsObject().Get("pid") is { IsNumber: true } pid
                && wake.PidLive((int)pid.AsNumber(), doc.AsObject().Get("pid_start") is { IsText: true } start ? start.AsString() : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return false;
        }
    }
}
