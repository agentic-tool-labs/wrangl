namespace Ah.Watch;

/// <summary>An ahd event addressed to an Orchestrator that nothing has acknowledged: its mailbox id and first line.</summary>
public sealed record WakeEvent(string Id, string Line);

/// <summary>What the waiter reads. It makes no daemon request and writes nothing but its own registration and memory.</summary>
public interface IWakeHost
{
    DateTimeOffset Now { get; }

    void Sleep(TimeSpan span);

    /// <summary>The unacknowledged ahd events of this Orchestrator's box, oldest first.</summary>
    IReadOnlyList<WakeEvent> Unacked();

    /// <summary>Whether the daemon the run file names is a running process; read from files and the process table only.</summary>
    bool DaemonUp();

    /// <summary>The event ids a waiter of this session has already printed.</summary>
    IReadOnlySet<string> LoadPrinted();

    void SavePrinted(IReadOnlyCollection<string> ids);

    void Print(string line);

    /// <summary>The line every wake starts with (the checklist to read first), or null for none.</summary>
    string? FirstLine => null;

    /// <summary>Whether the waiter exits after a quiet stretch (the Orchestrator's does; a member's does not).</summary>
    bool Heartbeat => true;

    /// <summary>The line printed when the daemon has been down for over a minute.</summary>
    string DaemonDownLine => "ahd wake: daemon down";

    /// <summary>The line printed, with exit 0, when the host has nothing left to watch; null when it always has.</summary>
    string? NothingLeftLine => null;

    /// <summary>Whether there is nothing left for the waiter to watch (a member's last background job has ended).</summary>
    bool NothingLeft() => false;
}

/// <summary>Something that can wake the Orchestrator: it answers the lines to print, or none.</summary>
public interface IWakeSource
{
    IReadOnlyList<string> Poll(DateTimeOffset now);
}

/// <summary>Wakes on an unacknowledged ahd event it has not printed before.</summary>
internal sealed class EventMailSource(IWakeHost host) : IWakeSource
{
    internal const int MaxLines = 15;

    public IReadOnlyList<string> Poll(DateTimeOffset now)
    {
        var unacked = host.Unacked();
        var printed = new HashSet<string>(host.LoadPrinted(), StringComparer.Ordinal);
        var fresh = unacked.Where(e => !printed.Contains(e.Id)).ToList();
        if (fresh.Count == 0)
        {
            return [];
        }

        var lines = fresh.Take(MaxLines).Select(static e => $"ahd wake: {e.Line}").ToList();
        if (fresh.Count > MaxLines)
        {
            lines.Add($"ahd wake: and {fresh.Count - MaxLines} more");
        }

        lines.Add("next: ah msg inbox (read them), then ah msg ack " + string.Join(' ', fresh.Select(static e => e.Id)));
        var live = unacked.Select(static e => e.Id).ToHashSet(StringComparer.Ordinal);
        host.SavePrinted([.. printed.Where(live.Contains), .. fresh.Select(static e => e.Id)]);
        return lines;
    }
}

/// <summary>
/// The background command an idle Orchestrator runs so that ahd's events can wake it: it blocks, polling its wake sources, and exits so
/// that the harness re-invokes the session. Exit 0 prints what woke it; exit 3 means the daemon has been down for over a minute.
/// </summary>
internal sealed class Waiter(IWakeHost host, StallSettings settings, IReadOnlyList<IWakeSource> sources)
{
    public static readonly TimeSpan DaemonGrace = TimeSpan.FromSeconds(60);

    private void PrintFirst()
    {
        if (host.FirstLine is { } first)
        {
            host.Print(first);
        }
    }

    public int Run()
    {
        var started = host.Now;
        DateTimeOffset? downSince = null;
        while (true)
        {
            foreach (var source in sources)
            {
                var lines = source.Poll(host.Now);
                if (lines.Count > 0)
                {
                    PrintFirst();
                    foreach (var line in lines)
                    {
                        host.Print(line);
                    }

                    return 0;
                }
            }

            if (host.NothingLeftLine is { } nothing && host.NothingLeft())
            {
                host.Print(nothing);
                return 0;
            }

            if (host.DaemonUp())
            {
                downSince = null;
            }
            else
            {
                downSince ??= host.Now;
                if (host.Now - downSince.Value > DaemonGrace)
                {
                    PrintFirst();
                    host.Print(host.DaemonDownLine);
                    return 3;
                }
            }

            if (host.Heartbeat && host.Now - started >= settings.Heartbeat)
            {
                PrintFirst();
                host.Print("ahd wake: heartbeat, nothing pending; restart the waiter if work is in flight");
                return 0;
            }

            host.Sleep(settings.WakePoll);
        }
    }
}
