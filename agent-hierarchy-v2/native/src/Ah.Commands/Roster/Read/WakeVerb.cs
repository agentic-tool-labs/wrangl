using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Commands;

/// <summary>
/// <c>roster wake</c>: the background command an Orchestrator, or a member that owns a background job, runs so that ahd's events can wake
/// it. It makes no daemon request.
/// </summary>
internal static class WakeVerb
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "cwd", "team" };

    internal static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"wake: unrecognized flag --{key} (use --cwd)");
            }
        }

        var wake = c.Services.Wake ?? throw new RosterFailure("wake: not available here");
        var session = c.EnvText("CLAUDE_CODE_SESSION_ID");
        var orchestrator = session is null ? null : wake.OrchestratorBox(session);
        var member = session is null || orchestrator is not null ? null : wake.MemberBox(session);
        if (session is null || (orchestrator is null && member is null))
        {
            throw new RosterFailure("wake: only a registered member or a team's Orchestrator runs the waiter");
        }

        var box = orchestrator ?? member!.Value;
        var at = box.Address.IndexOf('@', StringComparison.Ordinal);
        var team = box.Address[(at + 1)..];
        var settings = StallSettings.From(c.Services.Environment);
        using var registration = WakeFiles.Register(wake, c.Services.Clock, $"{session}.json", team);
        var watch = member is null ? null : new MemberWatch(box.Address[..at], team, c.MainDir ?? c.Dir);
        var host = new Host(c, wake, box.Dir, session, watch);
        return WatchModule.RunWaiter(host, settings);
    }

    /// <summary>A member's waiter: whose background jobs it watches, and where their records are.</summary>
    private sealed record MemberWatch(string Name, string Team, string HierarchyDir);

    private sealed class Host(RosterContext c, IRosterWake wake, string boxDir, string session, MemberWatch? member) : IWakeHost
    {
        private string PrintedKey => "wake.printed." + session;

        public DateTimeOffset Now => c.Services.Clock.UtcNow;

        public void Sleep(TimeSpan span) => Thread.Sleep(span);

        public IReadOnlyList<WakeEvent> Unacked() => wake.Unacked(boxDir, session);

        public bool DaemonUp() => wake.DaemonUp();

        public IReadOnlySet<string> LoadPrinted()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (wake.ReadState(PrintedKey) is { } text && JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(text)) is { IsArray: true } array)
                {
                    foreach (var id in array.AsArray())
                    {
                        if (id.IsText)
                        {
                            _ = ids.Add(id.AsString());
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
            {
                // an unreadable memory only means an event is printed again
            }

            return ids;
        }

        public void SavePrinted(IReadOnlyCollection<string> ids)
        {
            var array = new JsonArray();
            array.AddRange(ids.Select(static id => (JsonValue)id));
            wake.WriteState(PrintedKey, JsonText.Stringify(JsonValue.FromArray(array), 0));
        }

        public void Print(string line)
        {
            c.Stdout.WriteLine(line);
            c.Stdout.Flush();
        }

        public string? FirstLine => member is null ? FirstLineOf(c) : null;

        public string? HeartbeatLine => member is null ? null : "ahd wake: heartbeat; a background job is still open; restart the waiter to keep watching it";

        public string DaemonDownLine => member is null
            ? "ahd wake: daemon down"
            : "ahd wake: daemon down; the Orchestrator's failsafe covers your jobs; continue or report";

        public string? NothingLeftLine => member is null ? null : "ahd wake: no open background job; nothing left to watch";

        public bool NothingLeft() =>
            member is not null
            && !WatchModule.MemberHasOpenBackgroundJob(wake.Jobs(member.HierarchyDir, member.Team), member.Name, session, wake.JobOpen, _kin ??= wake.Kin());

        private IReadOnlySet<int>? _kin;
    }

    /// <summary>The line every wake starts with: read the checklist before anything else.</summary>
    internal static string FirstLineOf(RosterContext c) => $"First: {c.Services.Config.CliCommand(Ah.Config.CliTool.Roster)} checklist show --cwd \"{c.Cwd}\"";
}

/// <summary>
/// A wake file: <c>{pid, pid_start, team, started}</c> under <c>&lt;AhHome&gt;/wake/</c>. On dispose a waiter leaves a file another waiter has since
/// taken, gives the file back to the waiter it took it from if that one still runs, and otherwise removes it.
/// </summary>
internal sealed class WakeFiles : IDisposable
{
    private readonly IRosterWake _wake;
    private readonly string _path;
    private readonly string? _previous;

    private WakeFiles(IRosterWake wake, string path, string? previous)
    {
        _wake = wake;
        _path = path;
        _previous = previous;
    }

    internal static string PathOf(string ahHome, string name) => Path.Combine(ahHome, "wake", name);

    internal static WakeFiles Register(IRosterWake wake, IClock clock, string name, string team)
    {
        var path = PathOf(wake.AhHome, name);
        var previous = ReadQuietly(wake, path);
        var body = new JsonObject
        {
            ["pid"] = wake.Pid,
            ["pid_start"] = wake.StartOf(wake.Pid) is { } start ? start : JsonValue.Null,
            ["team"] = team,
            ["started"] = Ah.Core.Time.Timestamps.ToIsoString(clock.UtcNow),
        };
        wake.WriteText(path, JsonText.Stringify(JsonValue.FromObject(body), 0));
        return new WakeFiles(wake, path, previous);
    }

    public void Dispose()
    {
        if (Holder(ReadQuietly(_wake, _path)) is not { } holder || holder.Pid != _wake.Pid)
        {
            return;
        }

        if (Holder(_previous) is { } previous && previous.Start is not null && previous.Pid != _wake.Pid && _wake.StartOf(previous.Pid) == previous.Start)
        {
            _wake.WriteText(_path, _previous!);
        }
        else
        {
            _wake.Remove(_path);
        }
    }

    private static string? ReadQuietly(IRosterWake wake, string path)
    {
        try
        {
            return wake.ReadText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (int Pid, string? Start)? Holder(string? text)
    {
        try
        {
            return text is not null
                && JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(text)) is { IsMap: true } body
                && body.Member("pid") is { IsNumber: true } pid
                ? ((int)pid.AsNumber(), body.Member("pid_start") is { IsText: true } start ? start.AsString() : null)
                : null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }
}
