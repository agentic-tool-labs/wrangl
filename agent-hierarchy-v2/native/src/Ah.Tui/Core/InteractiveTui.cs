using System.Text;

namespace Ah.Tui.Core;

/// <summary>The terminal the interactive client draws on and reads from; the real one is the console, a test's is a fake.</summary>
internal interface ITuiTerminal
{
    int Columns { get; }

    int Rows { get; }

    /// <summary>Raised when the terminal's size changed.</summary>
    event Action? Resized;

    /// <summary>Reads input bytes; returns 0 at the end of input.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    void Write(string text);

    /// <summary>Puts input in raw mode until the handle is disposed; null when that is not possible.</summary>
    IDisposable? EnterRaw();
}

/// <summary>
/// The interactive client: an alternate screen with the cursor hidden and the mouse on, a frame drawn from the pure screens whenever the
/// slices, the state, the size or the clock changes it, keys and mouse reports decoded and applied, and the terminal put back on every way out.
/// </summary>
internal sealed class InteractiveTui : IDisposable
{
    internal const string EnterScreen = TuiModes.ScreenOn + TuiModes.MouseOn;
    internal const string LeaveScreen = TuiModes.MouseOff + TuiModes.ScreenOff;

    private readonly object _gate = new();
    private readonly ITuiTerminal _term;
    private readonly ITuiClock _clock;
    private readonly bool _color;
    private readonly bool _mouse;
    private readonly TimeZoneInfo _zone;
    private readonly SliceFeed _feed;
    private readonly CancellationTokenSource _stop = new();
    private TuiScreenState _state = TuiScreenState.Initial;
    private TuiFrame _frame = TuiFrame.Empty;
    private string? _team;
    private string _lastFrame = string.Empty;
    private string? _failure;
    private string? _usage;

    private InteractiveTui(string? team, ITuiTerminal term, IViewSource source, IDaemonDiscovery discovery, ITuiClock clock, bool color, bool mouse, TimeZoneInfo zone)
    {
        _team = team;
        _term = term;
        _clock = clock;
        _color = color;
        _mouse = mouse;
        _zone = zone;
        _feed = new SliceFeed(source, discovery, clock, Draw, OnFailed);
    }

    /// <summary>Runs the client, opening on the team named if one is, until quit, end of input, or cancellation; returns the exit code.</summary>
    internal static async Task<int> RunAsync(
        string? team,
        ITuiTerminal term,
        IViewSource source,
        IDaemonDiscovery discovery,
        ITuiClock clock,
        bool color,
        TextWriter stderr,
        CancellationToken cancellationToken,
        TuiCaps? caps = null,
        TimeZoneInfo? zone = null)
    {
        using var client = new InteractiveTui(team, term, source, discovery, clock, color, (caps?.Mouse ?? MouseLevel.Basic) != MouseLevel.Off, zone ?? TimeZoneInfo.Local);
        return await client.RunCoreAsync(stderr, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _feed.Dispose();
        _stop.Dispose();
    }

    private async Task<int> RunCoreAsync(TextWriter stderr, CancellationToken external)
    {
        using var link = CancellationTokenSource.CreateLinkedTokenSource(external, _stop.Token);
        var token = link.Token;
        IDisposable? raw = null;
        Task? timers = null;
        Task? reads = null;
        try
        {
            _term.Write(TuiModes.Enter(_mouse));
            raw = _term.EnterRaw();
            _term.Resized += OnResized;
            Draw();
            reads = _feed.RunAsync(token);
            timers = TimersAsync(token);
            await ReadLoopAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // a signal or a quit ended the session
        }
        catch (Exception e)
        {
            _failure ??= e.Message;
        }
        finally
        {
            // The timers and the reads draw, so they end before the screen is given back, whichever way the session ended.
            await link.CancelAsync().ConfigureAwait(false);
            foreach (var running in new[] { timers, reads })
            {
                if (running is null)
                {
                    continue;
                }

                try
                {
                    await running.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // it ends with the session
                }
                catch (Exception e)
                {
                    _failure ??= e.Message;
                }
            }

            _term.Resized -= OnResized;
            try
            {
                _term.Write(TuiModes.Leave(_mouse));
            }
            catch (IOException)
            {
                // the terminal is already gone
            }

            raw?.Dispose();
        }

        if (_usage is not null)
        {
            await stderr.WriteAsync((_usage + "\n").AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return 2;
        }

        if (_failure is not null)
        {
            await stderr.WriteAsync($"ahd tui: {TuiText.Plain(_failure)}\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return 1;
        }

        return 0;
    }

    private void OnResized() => Draw();

    private void OnFailed(Exception e)
    {
        _failure ??= e.Message;
        Stop();
    }

    private void Stop()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the session is over
        }
    }

    private async Task TimersAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await _clock.DelayAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            Draw();
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[4096];
        var ended = Task.Delay(Timeout.Infinite, token);
        while (!token.IsCancellationRequested)
        {
            // A read of standard input cannot be interrupted, so the session waits for the read or for its own end, whichever is first.
            var read = _term.ReadAsync(buffer, token).AsTask();
            if (await Task.WhenAny(read, ended).ConfigureAwait(false) != read)
            {
                return;
            }

            var n = await read.ConfigureAwait(false);
            if (n <= 0)
            {
                return;
            }

            var count = decoder.GetChars(buffer, 0, n, chars, 0);
            if (HandleInput(new string(chars, 0, count)))
            {
                return;
            }
        }
    }

    /// <summary>Applies each event of a chunk; true when the session should end.</summary>
    private bool HandleInput(string chunk)
    {
        foreach (var ev in TuiNav.ParseInput(chunk))
        {
            NavAction? action;
            lock (_gate)
            {
                var slices = _feed.Slices;
                (_state, action) = TuiScreens.Reduce(TuiScreens.Settle(_state, slices), ev, _frame, slices);
            }

            switch (action)
            {
                case QuitAction:
                    return true;
                case RefreshAction:
                    _feed.Refresh();
                    break;
            }

            // The next event of the chunk moves through what this one drew.
            Draw();
        }

        return false;
    }

    private static string Painted(TuiLine line, bool color)
    {
        var text = line.Tone is { } tone && color && ToneCode(tone) is { } code && line.ToneLength > 0
            ? string.Concat(line.Text.AsSpan(0, line.ToneAt), $"\u001b[{code}m", line.Text.AsSpan(line.ToneAt, line.ToneLength)) + "\u001b[39m" + line.Text[(line.ToneAt + line.ToneLength)..]
            : line.Text;
        return line.Kind switch
        {
            TuiLineKind.Title or TuiLineKind.Heading => "\u001b[1m" + text + "\u001b[0m",
            TuiLineKind.Selected => "\u001b[7m" + text + "\u001b[0m",
            TuiLineKind.Dim => "\u001b[2m" + text + "\u001b[0m",
            _ => text,
        };
    }

    private static string? ToneCode(string tone) => tone switch
    {
        "bad" => "31",
        "warn" => "33",
        "work" => "32",
        _ => null,
    };

    private void Draw()
    {
        lock (_gate)
        {
            var slices = _feed.Slices;
            if (_team is { } name && slices.ContainsKey("list:teams"))
            {
                _team = null;
                if (TuiScreens.OpenNamed(_state, slices, name) is { } opened)
                {
                    _state = opened;
                }
                else
                {
                    _usage = "no team named " + TuiText.Plain(name);
                    Stop();
                    return;
                }
            }

            _state = TuiScreens.Settle(_state, slices);
            _feed.Want(TuiScreens.Wanted(_state, slices, _clock.UtcNow));
            var input = new TuiScreenInput(slices, _clock.UtcNow, _zone, _feed.Down, _feed.Fault, _feed.LastGood, _feed.Answered);
            var cols = _term.Columns;
            var rows = _term.Rows;
            _frame = TuiScreens.Frame(_state, input, cols, rows);
            var lines = _frame.Lines.Take(rows).Select(l => Painted(l, _color)).ToList();
            var text = string.Join("\r\n", lines);
            if (text == _lastFrame)
            {
                return;
            }

            _lastFrame = text;
            _term.Write("\u001b[H" + string.Join("\r\n", lines.Select(l => l + "\u001b[K")) + "\u001b[J");
        }
    }
}
