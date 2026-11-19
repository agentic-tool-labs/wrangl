using System.Text;
using System.Threading.Channels;
using Ah.Tui.Core;

namespace Ah.Tui.Tests;

/// <summary>A terminal the test types into and reads from; it records raw mode and every write, in order.</summary>
internal sealed class FakeTerminal : ITuiTerminal
{
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource _frameBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _blockedRecorded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _left = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Columns { get; set; } = 100;

    public int Rows { get; set; } = 30;

    public event Action? Resized;

    internal List<string> Writes { get; } = [];

    internal bool RawOn { get; private set; }

    internal int RawEntered { get; private set; }

    internal int RawRestored { get; private set; }

    internal Exception? FailWrites { get; set; }

    /// <summary>When set, the next read that receives input throws this instead of returning it.</summary>
    internal Exception? ReadFault { get; set; }

    /// <summary>When set, the next frame write waits here before it is recorded.</summary>
    internal ManualResetEventSlim? BlockFrame { get; set; }

    /// <summary>Completes when a frame write has started waiting on <see cref="BlockFrame"/>.</summary>
    internal Task WhenFrameBlocked => _frameBlocked.Task;

    /// <summary>Completes when the frame write that waited on <see cref="BlockFrame"/> has been recorded.</summary>
    internal Task WhenBlockedFrameRecorded => _blockedRecorded.Task;

    /// <summary>Completes when the leave-screen sequence has been recorded.</summary>
    internal Task WhenLeft => _left.Task;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            var chunk = await _input.Reader.ReadAsync(cancellationToken);
            if (ReadFault is not null)
            {
                throw ReadFault;
            }

            chunk.CopyTo(buffer);
            return chunk.Length;
        }
        catch (ChannelClosedException)
        {
            return 0;
        }
    }

    public void Write(string text)
    {
        if (FailWrites is not null && text.StartsWith("\u001b[H", StringComparison.Ordinal))
        {
            throw FailWrites;
        }

        var blocked = false;
        if (BlockFrame is { } gate && text.StartsWith("\u001b[H", StringComparison.Ordinal))
        {
            BlockFrame = null;
            _frameBlocked.TrySetResult();
            gate.Wait();
            blocked = true;
        }

        lock (Writes)
        {
            Writes.Add(text);
        }

        if (blocked)
        {
            _blockedRecorded.TrySetResult();
        }

        if (text == TuiModes.Leave(true) || text == TuiModes.Leave(false))
        {
            _left.TrySetResult();
        }
    }

    public IDisposable? EnterRaw()
    {
        RawOn = true;
        RawEntered++;
        return new Handle(this);
    }

    internal void Type(string text) => _input.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    internal void EndInput() => _input.Writer.TryComplete();

    internal void RaiseResized() => Resized?.Invoke();

    internal string All() => string.Concat(Snapshot());

    internal List<string> Snapshot()
    {
        lock (Writes)
        {
            return [.. Writes];
        }
    }

    private sealed class Handle(FakeTerminal owner) : IDisposable
    {
        public void Dispose()
        {
            owner.RawOn = false;
            owner.RawRestored++;
        }
    }
}

/// <summary>The interactive client: what it writes on the way in and out, and how it answers keys, clicks, resizes and failures.</summary>
public sealed class InteractiveTuiTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private const string Row = "ah2-tm4";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<bool> Until(Func<bool> condition, int ms = 30000)
    {
        for (var waited = 0; waited < ms && !condition(); waited += 5)
        {
            await Task.Delay(5, Token);
        }

        return condition();
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal FakeTerminal Terminal { get; } = new();

        internal ManualClock Clock { get; } = new();

        internal SliceSource Source { get; } = new(ScreenFixtures.Slices);

        internal FakeDiscovery Discovery { get; } = new();

        internal StringWriter Stderr { get; } = new();

        internal CancellationTokenSource Cancel { get; } = new();

        internal Task<int>? Running { get; private set; }

        internal void Start(string? team = null, ColorDepth depth = ColorDepth.None, TuiCaps? caps = null) =>
            Running = InteractiveTui.RunAsync(team, Terminal, Source, Discovery, Clock, depth, Stderr, Cancel.Token, caps, TimeZoneInfo.Utc);

        internal Task<bool> Shows(string text) => Until(() => Terminal.Snapshot().Count > 0 && Terminal.Snapshot()[^1].Contains(text, StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {
            await Cancel.CancelAsync();
            Terminal.EndInput();
            if (Running is not null)
            {
                try
                {
                    await Running.WaitAsync(Ceiling);
                }
                catch (Exception)
                {
                    // already reported by the assertions
                }
            }

            Cancel.Dispose();
        }
    }

    [Fact]
    public async Task EnteringSetsTheScreenUpAndQuittingPutsItBack()
    {
        await using var rig = new Rig();

        rig.Start();
        Assert.True(await rig.Shows(Row));
        Assert.True(rig.Terminal.RawOn);
        Assert.Equal(InteractiveTui.EnterScreen, rig.Terminal.Snapshot()[0]);

        rig.Terminal.Type("q");
        var code = await rig.Running!.WaitAsync(Ceiling, Token);

        Assert.Equal(0, code);
        Assert.Equal(InteractiveTui.LeaveScreen, rig.Terminal.Snapshot()[^1]);
        Assert.False(rig.Terminal.RawOn);
        Assert.Equal((1, 1), (rig.Terminal.RawEntered, rig.Terminal.RawRestored));
        Assert.Empty(rig.Stderr.ToString());
    }

    [Fact]
    public void TheScreenModesAreTheAlternateScreenAndTheCursorAndOnlyWithTheMouseThePressAndWheelReports()
    {
        Assert.Equal("\u001b[?1049h\u001b[?25l\u001b[?1000h\u001b[?1006h", TuiModes.Enter(true));
        Assert.Equal("\u001b[?1006l\u001b[?1000l\u001b[?25h\u001b[?1049l", TuiModes.Leave(true));
        Assert.Equal("\u001b[?1049h\u001b[?25l", TuiModes.Enter(false));
        Assert.Equal("\u001b[?25h\u001b[?1049l", TuiModes.Leave(false));
        Assert.Equal(InteractiveTui.EnterScreen, TuiModes.Enter(true));
        Assert.Equal(InteractiveTui.LeaveScreen, TuiModes.Leave(true));
    }

    [Fact]
    public async Task WithTheMouseOffNoMouseModeIsAskedForOrRestored()
    {
        await using var rig = new Rig();

        rig.Start(caps: new TuiCaps(MouseLevel.Off));
        Assert.True(await rig.Shows(Row));
        rig.Terminal.Type("q");
        await rig.Running!.WaitAsync(Ceiling, Token);

        Assert.Equal(TuiModes.Enter(false), rig.Terminal.Snapshot()[0]);
        Assert.Equal(TuiModes.Leave(false), rig.Terminal.Snapshot()[^1]);
        Assert.DoesNotContain("?100", rig.Terminal.All(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeThisClientAsksForPressesAndTheWheelOnly()
    {
        await using var rig = new Rig();

        rig.Start(caps: new TuiCaps(MouseLevel.Basic));
        Assert.True(await rig.Shows(Row));
        rig.Terminal.Type("q");
        await rig.Running!.WaitAsync(Ceiling, Token);

        Assert.Equal(TuiModes.Enter(true), rig.Terminal.Snapshot()[0]);
        Assert.DoesNotContain("?1002", rig.Terminal.All(), StringComparison.Ordinal);
        Assert.DoesNotContain("?1003", rig.Terminal.All(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlCAlsoQuitsAndRestores()
    {
        await using var rig = new Rig();
        rig.Start();
        await Until(() => rig.Terminal.RawOn);

        rig.Terminal.Type("\u0003");

        Assert.Equal(0, await rig.Running!.WaitAsync(Ceiling, Token));
        Assert.Equal(InteractiveTui.LeaveScreen, rig.Terminal.Snapshot()[^1]);
        Assert.False(rig.Terminal.RawOn);
    }

    [Fact]
    public async Task ASignalEndsTheSessionWithTheTerminalRestoredAndExitZero()
    {
        await using var rig = new Rig();
        rig.Start();
        await Until(() => rig.Terminal.RawOn);

        await rig.Cancel.CancelAsync();

        Assert.Equal(0, await rig.Running!.WaitAsync(Ceiling, Token));
        Assert.Equal(InteractiveTui.LeaveScreen, rig.Terminal.Snapshot()[^1]);
        Assert.False(rig.Terminal.RawOn);
    }

    [Fact]
    public async Task AnUnboundKeyIsIgnoredAndNothingIsForwarded()
    {
        await using var rig = new Rig();
        rig.Start();
        Assert.True(await rig.Shows(Row));
        await Until(() => rig.Source.Polls.Count > 0);
        var before = rig.Terminal.Snapshot().Count;

        rig.Terminal.Type("z");
        rig.Terminal.Type("j");
        Assert.True(await Until(() => rig.Terminal.Snapshot().Count > before));

        Assert.False(rig.Running!.IsCompleted);
    }

    [Fact]
    public async Task AFailureRestoresTheTerminalFirstAndThenPrintsTheMessageWithExitOne()
    {
        await using var rig = new Rig();
        rig.Terminal.FailWrites = new IOException("boom");
        rig.Start();

        var code = await rig.Running!.WaitAsync(Ceiling, Token);

        Assert.Equal(1, code);
        Assert.Equal("ahd tui: boom\n", rig.Stderr.ToString());
        Assert.False(rig.Terminal.RawOn);
        Assert.Equal(InteractiveTui.LeaveScreen, rig.Terminal.Snapshot()[^1]);
    }

    [Fact]
    public async Task AFailureWhileRunningStopsTheReadsBeforeTheTerminalIsRestored()
    {
        await using var rig = new Rig();
        rig.Start();
        Assert.True(await rig.Shows(Row));
        rig.Terminal.FailWrites = new IOException("boom");
        rig.Terminal.Type("j");

        var code = await rig.Running!.WaitAsync(Ceiling, Token);

        Assert.Equal(1, code);
        Assert.Equal(0, rig.Clock.Pending);
        Assert.Equal(InteractiveTui.LeaveScreen, rig.Terminal.Snapshot()[^1]);
    }

    [Fact]
    public async Task ADrawInFlightWhenTheSessionFailsFinishesBeforeTheScreenIsRestored()
    {
        await using var rig = new Rig();
        rig.Start();
        Assert.True(await rig.Shows(Row));
        // Inside the building ceiling the summary rereads every 200 ms, so a 1 s step would also land a read that resets "updated 0s ago" and
        // can leave the timer's frame equal to the last one, with nothing to block. Past the ceiling it reads every 5 s and the step below fires only
        // the timer, whose frame then always changes.
        await rig.Clock.WaitingForAsync(TimeSpan.FromMilliseconds(200), Token);
        await rig.Clock.WaitingForAsync(TimeSpan.FromSeconds(1), Token);
        rig.Clock.Advance(TuiClientRules.BuildingCeiling);
        await rig.Clock.WaitingForAsync(TimeSpan.FromSeconds(5), Token);
        await rig.Clock.WaitingForAsync(TimeSpan.FromSeconds(1), Token);
        rig.Terminal.BlockFrame = new ManualResetEventSlim(false);
        var gate = rig.Terminal.BlockFrame;
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.Terminal.WhenFrameBlocked.WaitAsync(Ceiling, Token);
        rig.Terminal.ReadFault = new IOException("boom");
        rig.Terminal.Type("x");

        // A session that leaves the screen first is released by that write; one that waits for the draw is released after a pause.
        var release = Task.Run(
            async () =>
            {
                await Task.WhenAny(rig.Terminal.WhenLeft, Task.Delay(TimeSpan.FromMilliseconds(250), Token));
                gate.Set();
            },
            Token);

        var code = await rig.Running!.WaitAsync(Ceiling, Token);
        await release;
        await rig.Terminal.WhenBlockedFrameRecorded.WaitAsync(Ceiling, Token);

        Assert.Equal(1, code);
        Assert.Equal(InteractiveTui.LeaveScreen, rig.Terminal.Snapshot()[^1]);
        gate.Dispose();
    }

    [Fact]
    public async Task AFailureMessageIsPrintedAsPlainText()
    {
        await using var rig = new Rig();
        rig.Terminal.FailWrites = new IOException("bad\u001b[31m thing\u0007");
        rig.Start();

        var code = await rig.Running!.WaitAsync(Ceiling, Token);

        Assert.Equal(1, code);
        Assert.Equal("ahd tui: bad thing\n", rig.Stderr.ToString());
    }

    [Fact]
    public async Task AResizeRedrawsAtTheNewSize()
    {
        await using var rig = new Rig();
        rig.Start();
        Assert.True(await rig.Shows(Row));
        var before = rig.Terminal.Snapshot().Count;

        rig.Terminal.Columns = 60;
        rig.Terminal.Rows = 20;
        rig.Terminal.RaiseResized();
        await Until(() => rig.Terminal.Snapshot().Count > before);

        var frame = rig.Terminal.Snapshot()[^1];
        var lines = frame.Replace("\u001b[H", string.Empty, StringComparison.Ordinal).Replace("\u001b[J", string.Empty, StringComparison.Ordinal).Split("\r\n");
        Assert.Equal(20, lines.Length);
        Assert.All(lines, l => Assert.True(TuiText.Width(System.Text.RegularExpressions.Regex.Replace(l, @"\u001b\[[0-9;?]*[A-Za-z]", string.Empty)) <= 60, l));
    }

    [Fact]
    public async Task AnUnchangedTickWritesNoFrame()
    {
        await using var rig = new Rig();
        rig.Discovery.Probe = null;
        rig.Start();
        Assert.True(await rig.Shows(TuiScreens.DownLine));
        await rig.Clock.WaitingAsync(Token);
        var before = rig.Terminal.Snapshot().Count;

        await rig.Clock.TickAsync(TimeSpan.FromSeconds(1), Token);

        Assert.Equal(before, rig.Terminal.Snapshot().Count);
    }

    [Fact]
    public async Task ColourIsOnlyWrittenWhenAskedAndTheLayoutIsTheSame()
    {
        await using var plain = new Rig();
        plain.Start(depth: ColorDepth.None);
        Assert.True(await plain.Shows(Row));
        await using var coloured = new Rig();
        coloured.Start(depth: ColorDepth.TrueColor);
        Assert.True(await coloured.Shows("\u001b[38;2;143;193;255m● working"));

        Assert.DoesNotMatch(@"\u001b\[(?:3[0-7]|9[0-7])m", plain.Terminal.All());
        Assert.Contains("\u001b[1m", plain.Terminal.All(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xterm-256color", null, "1", "None")]
    [InlineData("dumb", "truecolor", null, "None")]
    [InlineData("xterm-256color", null, "", "Palette256")]
    [InlineData("xterm", "truecolor", null, "TrueColor")]
    [InlineData("xterm", "24bit", null, "TrueColor")]
    [InlineData("xterm-256color", "yes", null, "Palette256")]
    [InlineData("xterm-color", null, null, "Sixteen")]
    [InlineData(null, null, null, "Sixteen")]
    public void TheDepthIsReadOnceFromTheEnvironmentAndOneFunctionMapsAColourToIt(string? term, string? colorTerm, string? noColor, string expected)
    {
        var env = new Dictionary<string, string?> { ["TERM"] = term, ["COLORTERM"] = colorTerm, ["NO_COLOR"] = noColor };
        Assert.Equal(Enum.Parse<ColorDepth>(expected), TuiTheme.Depth(k => env.GetValueOrDefault(k)));

        var blue = TuiRgb.Hex("#8fc1ff");
        Assert.Equal("38;2;143;193;255", TuiTheme.Code(blue, ColorDepth.TrueColor));
        Assert.Equal("48;5;111", TuiTheme.Code(blue, ColorDepth.Palette256, background: true));
        Assert.Equal("38;5;244", TuiTheme.Code(new TuiRgb(128, 128, 128), ColorDepth.Palette256));
        Assert.Equal("91", TuiTheme.Code(new TuiRgb(250, 10, 10), ColorDepth.Sixteen));
        Assert.Equal("31", TuiTheme.Code(new TuiRgb(200, 10, 10), ColorDepth.Sixteen));
        Assert.Equal(string.Empty, TuiTheme.Code(blue, ColorDepth.None));
    }

    [Fact]
    public async Task AMemberRowCarriesItsRoleColourAndTheOrchestratorAndASelectedRowNone()
    {
        await using var rig = new Rig();
        rig.Start("ah2-tm4", ColorDepth.TrueColor);

        Assert.True(await rig.Shows("\u001b[38;2;86;200;232mimpl-41\u001b[39m"));
        Assert.True(await rig.Shows("\u001b[38;2;228;123;209mreviewer-9\u001b[39m"));
        rig.Terminal.Type("j");
        Assert.True(await rig.Shows("\u001b[7m▸ impl-41"));
        Assert.Contains("\r\n  orchestrator  ", rig.Terminal.Snapshot()[^1], StringComparison.Ordinal);
        var selected = rig.Terminal.Snapshot()[^1].Split("\r\n").Single(l => l.Contains("▸ ", StringComparison.Ordinal));
        Assert.DoesNotContain("\u001b[38;", selected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeWithNoColorTheSameRowHasNoColourCodes()
    {
        await using var rig = new Rig();
        rig.Start("ah2-tm4", TuiTheme.Depth(k => k switch { "NO_COLOR" => "1", "COLORTERM" => "truecolor", _ => null }));

        Assert.True(await rig.Shows("impl-41"));
        Assert.DoesNotMatch(@"\u001b\[(?:38;|3[0-7]m|9[0-7]m)", rig.Terminal.All());
        Assert.Contains("\u001b[1m", rig.Terminal.All(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMascotIsInTheFirstFrameAndGoneAfterAKey()
    {
        var art = InteractiveTui.Art(ColorDepth.None);
        await using var rig = new Rig();
        rig.Start();

        Assert.True(await rig.Shows(Row));
        Assert.Contains(art[0], rig.Terminal.Snapshot()[1], StringComparison.Ordinal);
        Assert.Contains(art[0], rig.Terminal.Snapshot()[^1], StringComparison.Ordinal);
        rig.Terminal.Type("j");

        Assert.True(await Until(() => !rig.Terminal.Snapshot()[^1].Contains(art[0], StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(23, false)]
    [InlineData(24, true)]
    public async Task TheMascotIsSkippedUnderTwentyFourRows(int rows, bool shown)
    {
        var art = InteractiveTui.Art(ColorDepth.None);
        await using var rig = new Rig();
        rig.Terminal.Rows = rows;
        rig.Start();

        Assert.True(await rig.Shows(Row));
        Assert.Equal(shown, rig.Terminal.Snapshot()[^1].Contains(art[0], StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePollIsSubscribedToTheScreenAndAClickChangesIt()
    {
        await using var rig = new Rig();
        rig.Start();
        Assert.True(await rig.Shows(Row));
        Assert.True(await Until(() => rig.Source.Polls.Count > 0));

        // The first frames have the mascot above the screen, so the row is clicked where it is drawn, under the art.
        rig.Terminal.Type(ScreenFixtures.Click(4, 2 + InteractiveTui.Art(ColorDepth.None).Count));

        Assert.True(await rig.Shows("AGENTS"));
        Assert.True(await Until(() =>
        {
            lock (rig.Source.Polls)
            {
                return rig.Source.Polls[^1].Sub[0] == ScreenFixtures.Team && rig.Source.Polls[^1].Sub.Contains("sitrep:d1/ah2-tm4");
            }
        }));
        Assert.True(await rig.Shows("Pick the DB migration order"));
    }

    [Fact]
    public async Task ATeamFlagOpensOnThatTeam()
    {
        await using var rig = new Rig();
        rig.Start("ah2-tm4");
        Assert.True(await rig.Shows("AGENTS"));
    }

    [Fact]
    public async Task NegativeAnUnknownTeamLeavesTheScreenThenSaysSoWithExitTwo()
    {
        await using var rig = new Rig();
        rig.Start("nosuch");

        Assert.Equal(2, await rig.Running!.WaitAsync(Ceiling, Token));
        Assert.Equal("no team named nosuch\n", rig.Stderr.ToString());
        Assert.Equal(InteractiveTui.LeaveScreen, rig.Terminal.Snapshot()[^1]);
    }

    [Fact]
    public async Task ADaemonThatGoesDownShowsOneLineAndComesBack()
    {
        await using var rig = new Rig();
        rig.Discovery.Probe = null;
        rig.Start();
        Assert.True(await rig.Shows(TuiScreens.DownLine));

        rig.Discovery.Probe = new FakeDiscovery().Probe;
        rig.Clock.Advance(TimeSpan.FromSeconds(3));

        Assert.True(await rig.Shows(Row));
    }

    [Fact]
    public async Task EndOfInputEndsTheSessionRestored()
    {
        await using var rig = new Rig();
        rig.Start();
        await Until(() => rig.Terminal.RawOn);

        rig.Terminal.EndInput();

        Assert.Equal(0, await rig.Running!.WaitAsync(Ceiling, Token));
        Assert.False(rig.Terminal.RawOn);
        Assert.Equal(InteractiveTui.LeaveScreen, rig.Terminal.Snapshot()[^1]);
    }
}
