using System.Diagnostics;
using Ah.Cli.Verbs;

namespace Ah.Cli.Tests;

/// <summary>No hook that is not a gate may hold a session: past its bound it is skipped, with one line on stderr.</summary>
public sealed class HookBoundTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    private static (int Code, string Err, TimeSpan Took) Run(string[] rest, Func<CliContext> makeContext)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var clock = Stopwatch.StartNew();
        var code = HookVerb.Run(null, rest, stdout, stderr, makeContext: makeContext, fallbackLog: () => null, bound: Short);
        return (code, stderr.ToString(), clock.Elapsed);
    }

    private static CliContext Hangs()
    {
        Thread.Sleep(TimeSpan.FromSeconds(30));
        throw new InvalidOperationException("unreachable");
    }

    [Theory]
    [InlineData("sessionstart")]
    [InlineData("job-track")]
    public void AHookThatHangsIsSkippedAtItsBoundAndExitsZero(string name)
    {
        var (code, err, took) = Run([name], Hangs);

        Assert.Equal(0, code);
        Assert.Contains($"the {name} hook gave up", err, StringComparison.Ordinal);
        Assert.True(took < TimeSpan.FromSeconds(5), $"took {took}");
    }

    [Fact]
    public void AHookThatFinishesInTimeIsNotReportedAsSkipped()
    {
        var (_, err, _) = Run(["job-track"], () => throw new InvalidOperationException("boom"));

        Assert.DoesNotContain("gave up", err, StringComparison.Ordinal);
    }

    [Fact]
    public void AGateIsNeverCutByTheBound()
    {
        var started = new ManualResetEventSlim();
        var worker = new Thread(() => Run(["pretooluse-chain"], () =>
        {
            started.Set();
            Thread.Sleep(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("late");
        })) { IsBackground = true };
        worker.Start();

        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(worker.Join(Short * 3), "the gate returned at the hook bound");
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
    }

    /// <summary>A stream that hands over a few bytes and then never ends, like a parent that keeps the pipe open.</summary>
    private sealed class NeverCloses(byte[] first) : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_sent)
            {
                _sent = true;
                first.CopyTo(buffer, offset);
                return first.Length;
            }

            Thread.Sleep(TimeSpan.FromSeconds(30));
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void StandardInputThatNeverClosesIsReadForItsBoundAndYieldsWhatArrived()
    {
        // The product's own bound: the reader is a thread-pool task, and a pool starved under load can start it after a 300 ms bound.
        var clock = Stopwatch.StartNew();
        var read = HookVerb.ReadBounded(new NeverCloses("{\"a\":1}"u8.ToArray()), HookVerb.StdinBound, out var timedOut);

        Assert.True(timedOut);
        Assert.Equal("{\"a\":1}", System.Text.Encoding.UTF8.GetString(read.Span));
        Assert.True(clock.Elapsed < HookVerb.StdinBound + TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void StandardInputThatClosesIsReadInFull()
    {
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);

        var read = HookVerb.ReadBounded(new MemoryStream(payload), TimeSpan.FromSeconds(5), out var timedOut);

        Assert.False(timedOut);
        Assert.Equal(payload, read.ToArray());
    }
}
