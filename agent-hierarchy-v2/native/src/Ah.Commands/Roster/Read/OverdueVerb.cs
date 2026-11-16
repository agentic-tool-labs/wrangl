using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Commands;

/// <summary>
/// <c>roster overdue</c>: the failsafe an Orchestrator runs in the background beside <c>roster wake</c>. It reads files only and never
/// starts or waits for the daemon; when ahd has missed something it asks the health once and, on no answer, starts <c>ahd heal</c>.
/// </summary>
internal static class OverdueVerb
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "cwd", "team" };

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"overdue: unrecognized flag --{key} (use --cwd)");
            }
        }

        var wake = c.Services.Wake ?? throw new RosterFailure("overdue: not available here");
        var session = c.EnvText("CLAUDE_CODE_SESSION_ID");
        if (session is null || wake.OrchestratorBox(session) is not { } box)
        {
            throw new RosterFailure("overdue: only a team's Orchestrator runs the overdue timer");
        }

        var team = box.Address[(box.Address.IndexOf('@', StringComparison.Ordinal) + 1)..];
        var cli = c.Services.Config.CliCommand(Ah.Config.CliTool.Roster);
        using var registration = WakeFiles.Register(wake, c.Services.Clock, $"{session}.overdue.json", team);
        var host = new Host(c, wake, box.Dir, session, team);
        return WatchModule.RunOverdueTimer(host, host.Settings, $"{cli} stall-diagnose", $"{cli} overdue --cwd {c.Cwd}");
    }

    private sealed class Host(RosterContext c, IRosterWake wake, string boxDir, string session, string team) : IOverdueHost
    {
        private string MemoryKey => "overdue." + session;

        private const string HealthKey = "overdue.ahd-health";

        public OverdueSettings Settings { get; } = OverdueSettings.From(c.Services.Environment);

        public DateTimeOffset Now => c.Services.Clock.UtcNow;

        public void Sleep(TimeSpan span) => Thread.Sleep(span);

        public OverdueScan Scan()
        {
            var open = wake.OpenDispatches(c.Dir, team, session);
            return new OverdueScan(open, wake.Events(boxDir, session), QueueStalls(), JobStalls(), LaneStalls());
        }

        private string? _lanesSkipped;

        private IReadOnlyList<OverdueLaneItem> LaneStalls()
        {
            try
            {
                var stalls = wake.LaneStalls(c.Dir, team, session);
                _lanesSkipped = null;
                return stalls;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (_lanesSkipped != e.Message)
                {
                    _lanesSkipped = e.Message;
                    c.Stderr.WriteLine($"overdue: lane check skipped ({e.Message})");
                }

                return [];
            }
        }

        private string? _jobsSkipped;

        private IReadOnlyList<OverdueJobItem> JobStalls()
        {
            try
            {
                var stalls = wake.JobStalls(c.Dir, team, c.Services.Clock.UtcNow.ToUnixTimeMilliseconds());
                _jobsSkipped = null;
                return stalls;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (_jobsSkipped != e.Message)
                {
                    _jobsSkipped = e.Message;
                    c.Stderr.WriteLine($"overdue: job check skipped ({e.Message})");
                }

                return [];
            }
        }

        private string? _skipped;

        private IReadOnlyList<OverdueQueueItem> QueueStalls()
        {
            try
            {
                var stalls = wake.QueueStalls(c.ProgressPool, c.Dir, team, c.Services.Clock.UtcNow.ToUnixTimeMilliseconds());
                _skipped = null;
                return stalls;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (_skipped != e.Message)
                {
                    _skipped = e.Message;
                    c.Stderr.WriteLine($"overdue: queue check skipped ({e.Message})");
                }

                return [];
            }
        }

        public string? FirstLine => WakeVerb.FirstLineOf(c);

        public OverdueMemory LoadMemory()
        {
            var reported = new HashSet<string>(StringComparer.Ordinal);
            var unknown = new HashSet<string>(StringComparer.Ordinal);
            double? unhealthy = null;
            try
            {
                if (wake.ReadState(MemoryKey) is { } text && JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(text)) is { IsMap: true } doc)
                {
                    Fill(doc.AsObject().Get("reported"), reported);
                    Fill(doc.AsObject().Get("unknown"), unknown);
                    unhealthy = doc.AsObject().Get("unhealthy_since") is { IsNumber: true } n ? n.AsNumber() : null;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
            {
                // unreadable memory means a reported step may be reported again
            }

            return new OverdueMemory(reported, unknown, unhealthy);
        }

        public void SaveMemory(OverdueMemory memory)
        {
            var body = new JsonObject { ["reported"] = Array(memory.Reported), ["unknown"] = Array(memory.UnknownLogged) };
            if (memory.UnhealthySinceMs is { } since)
            {
                body["unhealthy_since"] = since;
            }

            wake.WriteState(MemoryKey, JsonText.Stringify(JsonValue.FromObject(body), 0));
        }

        public void WriteHealth(string requestId, string stage, double deadlineMs, double unhealthySinceMs)
        {
            var body = new JsonObject
            {
                ["unhealthy_since"] = unhealthySinceMs,
                ["last_missed"] = new JsonObject { ["request_id"] = requestId, ["step"] = stage, ["deadline"] = deadlineMs },
            };
            wake.WriteState(HealthKey, JsonText.Stringify(JsonValue.FromObject(body), 0));
        }

        public void ClearHealth() => wake.WriteState(HealthKey, null);

        public void StartHeal()
        {
            if (wake.LaunchHeal("failsafe") is { } pid)
            {
                Print($"ahd does not answer: started ahd heal (pid {pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}), which restarts it only if it is proven hung");
            }
        }

        public void Journal(JsonObject row)
        {
            row["team"] = team;
            wake.Journal(row);
        }

        public void Print(string line)
        {
            c.Stdout.WriteLine(line);
            c.Stdout.Flush();
        }

        private static JsonArray Array(IEnumerable<string> items)
        {
            var array = new JsonArray();
            array.AddRange(items.Select(static i => (JsonValue)i));
            return array;
        }

        private static void Fill(JsonValue? value, HashSet<string> into)
        {
            if (value is { IsArray: true } array)
            {
                foreach (var item in array.AsArray().Where(static i => i.IsText))
                {
                    _ = into.Add(item.AsString());
                }
            }
        }
    }
}
