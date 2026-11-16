using System.Globalization;
using Ah.Core.Json;

namespace Ah.Tui.Core;

/// <summary>What one request for a view came to.</summary>
internal enum ViewOutcomeKind
{
    /// <summary>A view document.</summary>
    View,

    /// <summary>A long poll that timed out with nothing new; the revision is kept.</summary>
    Unchanged,

    /// <summary>The path is gone; the nearest one is named.</summary>
    Missing,

    /// <summary>The daemon answered with anything else: it is up, so it is not "down".</summary>
    Error,

    /// <summary>The caller cancelled the request.</summary>
    Aborted,

    /// <summary>The daemon could not be reached at all.</summary>
    Down,
}

/// <summary>The outcome of one <c>GET /v1/view</c>.</summary>
internal sealed record ViewOutcome(
    ViewOutcomeKind Kind,
    JsonValue? View = null,
    string? Rev = null,
    string? Nearest = null,
    int Status = 0,
    string Code = "",
    string Message = "")
{
    internal static ViewOutcome Down { get; } = new(ViewOutcomeKind.Down);

    internal static ViewOutcome Aborted { get; } = new(ViewOutcomeKind.Aborted);

    /// <summary>The outcome of a daemon answer: its status and its parsed body (null when the body was not JSON).</summary>
    internal static ViewOutcome FromReply(int status, JsonValue? body)
    {
        if (status == 200 && body is { IsMap: true } view && TuiRender.M(view, "view_schema") is { IsTruthy: true })
        {
            if (TuiRender.M(view, "unchanged") is { IsTruthy: true })
            {
                return new ViewOutcome(ViewOutcomeKind.Unchanged, Rev: TuiText.Text(TuiRender.M(view, "rev")));
            }

            return new ViewOutcome(ViewOutcomeKind.View, View: view);
        }

        if (status == 404 && TuiRender.M(body, "error") is { IsText: true } err && err.AsString() == "not_found")
        {
            var nearest = TuiRender.M(body, "nearest") is { IsText: true } n ? n.AsString() : "/";
            return new ViewOutcome(ViewOutcomeKind.Missing, Nearest: nearest);
        }

        var e = TuiRender.M(body, "error") is { IsMap: true } m ? m : (JsonValue?)null;
        var code = TuiRender.M(e, "code") is { IsText: true } c ? c.AsString() : "unknown";
        var message = TuiRender.M(e, "message") is { IsText: true } t ? t.AsString() : string.Empty;
        return new ViewOutcome(ViewOutcomeKind.Error, Status: status, Code: code, Message: message);
    }
}

/// <summary>A running daemon as discovery found it: its health document and the socket it listens on.</summary>
internal sealed record DaemonProbe(JsonValue Health, string Socket);

/// <summary>Where views come from. The only thing the client asks of a daemon is a view and its health.</summary>
internal interface IViewSource
{
    /// <summary>One <c>GET /v1/view</c>. <paramref name="since"/> and <paramref name="waitSeconds"/> are sent only on a re-poll of the same path.</summary>
    Task<ViewOutcome> GetViewAsync(DaemonProbe daemon, string path, string? since, int waitSeconds, CancellationToken cancellationToken);

    /// <summary>One poll of the slice feed.</summary>
    Task<ViewOutcome> GetSlicesAsync(DaemonProbe daemon, SlicePoll poll, CancellationToken cancellationToken);
}

/// <summary>One poll of the slice feed: the ids subscribed, those whose bodies are wanted whatever their version, the cursor and epoch of the last reply (0 and null for none), and how long to hold.</summary>
internal sealed record SlicePoll(IReadOnlyList<string> Sub, IReadOnlyList<string> Fresh, long Since, string? Epoch, int WaitSeconds);

/// <summary>Finds the daemon. Null: it is down or draining.</summary>
internal interface IDaemonDiscovery
{
    Task<DaemonProbe?> DiscoverAsync(CancellationToken cancellationToken);
}

/// <summary>The client's clock and its way of waiting, so the loop is testable without real time.</summary>
internal interface ITuiClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>The text and timing rules of the client that need no terminal.</summary>
internal static class TuiClientRules
{
    internal const int LongPollSeconds = 10;
    internal static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);

    /// <summary>How long a wanted slice may take to arrive before the screens take it as not served.</summary>
    internal static readonly TimeSpan Settling = TimeSpan.FromSeconds(3);

    /// <summary>The most one plain frame waits for the team list of a daemon still building it.</summary>
    internal static readonly TimeSpan BuildingCeiling = TimeSpan.FromSeconds(15);

    /// <summary>The wait after the <paramref name="failures"/>th failure in a row of a held path: Retry, doubling each time, at most a minute.</summary>
    internal static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(60, Retry.TotalSeconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 6))));

    internal static string FaultText(ViewOutcome e) =>
        $"the daemon answered {e.Status.ToString(CultureInfo.InvariantCulture)} {e.Code}" + (e.Message.Length > 0 ? $": {e.Message}" : string.Empty);
}

/// <summary>One plain frame for a pipe or <c>--once</c>, and the exit codes that go with it.</summary>
internal static class TuiOnce
{
    internal const int DownExit = 3;

    /// <summary>One plain frame of Teams, or of the team named: exit 0, 3 with no daemon, 2 for a team that is not there, 1 for a daemon's fault.</summary>
    internal static async Task<int> RunScreenAsync(
        string? team,
        IViewSource source,
        IDaemonDiscovery discovery,
        ITuiClock clock,
        int cols,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? failure = null;
        using var feed = new SliceFeed(source, discovery, clock, static () => { }, e =>
        {
            failure = e;
            stop.Cancel();
        });
        var running = feed.RunAsync(stop.Token);
        var exit = 0;

        async Task PrintAsync(TuiScreenState state, IReadOnlyDictionary<string, JsonValue> slices)
        {
            var frame = TuiScreens.Frame(state, new TuiScreenInput(slices, clock.UtcNow, TimeZoneInfo.Local, LastGood: feed.LastGood, Answered: feed.Answered), cols, null);
            await stdout.WriteAsync((string.Join("\n", frame.Lines.Select(static l => l.Text)) + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var state = TuiScreenState.Initial;
            var deadline = clock.UtcNow + TuiClientRules.Settling;
            var ceiling = clock.UtcNow + TuiClientRules.BuildingCeiling;
            var listed = false;
            var named = team is null;
            while (true)
            {
                if (feed.Down)
                {
                    await stdout.WriteAsync((TuiScreens.DownLine + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
                    exit = DownExit;
                    break;
                }

                if (feed.Fault.Length > 0)
                {
                    await stderr.WriteAsync($"ahd: {TuiText.Plain(feed.Fault)}\n".AsMemory(), cancellationToken).ConfigureAwait(false);
                    exit = 1;
                    break;
                }

                var slices = feed.Slices;
                var settled = clock.UtcNow >= deadline;
                // An empty list from a daemon still building it is not an answer: the team lane may not have been read yet, and the frame would say "No teams".
                if (!slices.ContainsKey("list:teams") || (feed.Building && TeamSlices.ListIsEmpty(slices) && clock.UtcNow < ceiling))
                {
                    if (settled && (!feed.Building || clock.UtcNow >= ceiling))
                    {
                        await PrintAsync(state, slices).ConfigureAwait(false);
                        break;
                    }
                }
                else
                {
                    if (!listed)
                    {
                        // What the screen wants is known only now, so its time to arrive starts now.
                        listed = true;
                        deadline = clock.UtcNow + TuiClientRules.Settling;
                        settled = false;
                    }

                    if (!named)
                    {
                        if (TuiScreens.OpenNamed(state, slices, team!) is not { } opened)
                        {
                            await stderr.WriteAsync($"no team named {TuiText.Plain(team)}\n".AsMemory(), cancellationToken).ConfigureAwait(false);
                            exit = 2;
                            break;
                        }

                        state = opened;
                        named = true;
                    }

                    var ids = TuiScreens.Wanted(state, slices, clock.UtcNow);
                    feed.Want(ids);
                    // A count from a plan not yet read is printed as unknown, so a frame waits for the plan as it waits for a building daemon's list.
                    var planWait = TeamSlices.AnyPlanNotRead(slices) && clock.UtcNow < ceiling;
                    if (!planWait && (feed.Answered(ids) || settled))
                    {
                        await PrintAsync(state, slices).ConfigureAwait(false);
                        break;
                    }
                }

                await clock.DelayAsync(TimeSpan.FromMilliseconds(20), stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (failure is not null)
        {
            // the failure is reported below
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the reads end with the frame
            }
        }

        if (failure is not null)
        {
            await stderr.WriteAsync($"ahd tui: {TuiText.Plain(failure.Message)}\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }

        return exit;
    }

    /// <summary>The width plain output is laid out at: <c>COLUMNS</c> when it is above zero, else 100 (the terminal's own width is not used).</summary>
    internal static int Columns(string? columnsVariable) =>
        columnsVariable is { Length: > 0 } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 ? (int)n : 100;
}
