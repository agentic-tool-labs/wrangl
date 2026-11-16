using Ah.Core.Json;
using Ah.Tui.Core;

namespace Ah.Tui.Tests;

/// <summary>A clock that moves only when a test says so; a delay completes when the clock has passed it.</summary>
internal sealed class ManualClock : ITuiClock
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly List<(DateTimeOffset Due, TaskCompletionSource Done)> _waiting = [];
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-11T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private int _registrations;
    private TaskCompletionSource _changed = NewSignal();

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    internal int Pending
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count;
            }
        }
    }

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiting.Add((_now + duration, done));
            _registrations++;
            Signal();
        }

        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _waiting.RemoveAll(w => w.Done == done);
                Signal();
            }

            done.TrySetCanceled(cancellationToken);
        });
        return done.Task;
    }

    internal void Advance(TimeSpan by)
    {
        List<TaskCompletionSource> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _waiting.Where(w => w.Due <= _now).Select(w => w.Done)];
            _waiting.RemoveAll(w => w.Due <= _now);
            Signal();
        }

        foreach (var done in due)
        {
            done.TrySetResult();
        }
    }

    /// <summary>Completes once something is waiting on the clock.</summary>
    internal Task WaitingAsync(CancellationToken cancellationToken) => UntilAsync(() => _waiting.Count > 0, cancellationToken);

    /// <summary>Completes once something is waiting on the clock for exactly <paramref name="duration"/> from now.</summary>
    internal Task WaitingForAsync(TimeSpan duration, CancellationToken cancellationToken) => UntilAsync(() => _waiting.Any(w => w.Due == _now + duration), cancellationToken);

    /// <summary>
    /// Moves the clock once the loop under test is waiting on it and returns when that loop is waiting again, so the tick has been
    /// fully handled and no tick is lost to scheduling.
    /// </summary>
    internal async Task TickAsync(TimeSpan by, CancellationToken cancellationToken)
    {
        await WaitingAsync(cancellationToken);
        int seen;
        lock (_gate)
        {
            seen = _registrations;
        }

        Advance(by);
        await UntilAsync(() => _registrations > seen, cancellationToken);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Signal()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (condition())
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(Ceiling, cancellationToken);
        }
    }
}

internal sealed class FakeDiscovery : IDaemonDiscovery
{
    internal DaemonProbe? Probe { get; set; } = new(JsonText.Parse("{\"version\":\"9.9.9\",\"ladder\":{\"pending\":0,\"sending\":0,\"paused\":0,\"escalated\":1}}"), "/tmp/none.sock");

    internal int Calls { get; private set; }

    public Task<DaemonProbe?> DiscoverAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Probe);
    }
}

/// <summary>What the client makes of a daemon's answer, and the width of plain output.</summary>
public sealed class ClientLoopTests
{
    [Fact]
    public void ADaemonAnswerIsClassifiedAsNodeDoes()
    {
        var view = ViewOutcome.FromReply(200, JsonText.Parse("{\"view_schema\":1,\"path\":\"/\",\"rev\":\"r1\"}"));
        var same = ViewOutcome.FromReply(200, JsonText.Parse("{\"view_schema\":1,\"unchanged\":true,\"rev\":\"r7\"}"));
        var gone = ViewOutcome.FromReply(404, JsonText.Parse("{\"view_schema\":1,\"error\":\"not_found\",\"path\":\"/x\",\"nearest\":\"/o\"}"));
        var goneNoNearest = ViewOutcome.FromReply(404, JsonText.Parse("{\"error\":\"not_found\"}"));
        var boom = ViewOutcome.FromReply(500, JsonText.Parse("{\"error\":{\"code\":\"internal\",\"message\":\"internal error\"}}"));
        var notView = ViewOutcome.FromReply(200, JsonText.Parse("{\"ok\":true}"));
        var unknown = ViewOutcome.FromReply(502, null);
        var notFoundObject = ViewOutcome.FromReply(404, JsonText.Parse("{\"error\":{\"code\":\"not_found\",\"message\":\"no route\"}}"));

        Assert.Equal(ViewOutcomeKind.View, view.Kind);
        Assert.Equal((ViewOutcomeKind.Unchanged, "r7"), (same.Kind, same.Rev));
        Assert.Equal((ViewOutcomeKind.Missing, "/o"), (gone.Kind, gone.Nearest));
        Assert.Equal("/", goneNoNearest.Nearest);
        Assert.Equal((ViewOutcomeKind.Error, 500, "internal", "internal error"), (boom.Kind, boom.Status, boom.Code, boom.Message));
        Assert.Equal(ViewOutcomeKind.Error, notView.Kind);
        Assert.Equal((ViewOutcomeKind.Error, 502, "unknown"), (unknown.Kind, unknown.Status, unknown.Code));
        Assert.Equal((ViewOutcomeKind.Error, "not_found"), (notFoundObject.Kind, notFoundObject.Code));
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData("", 100)]
    [InlineData("0", 100)]
    [InlineData("-5", 100)]
    [InlineData("abc", 100)]
    [InlineData("80", 80)]
    [InlineData("120", 120)]
    public void PlainWidthIsColumnsWhenAboveZeroElseOneHundred(string? columns, int expected)
    {
        Assert.Equal(expected, TuiOnce.Columns(columns));
    }
}
