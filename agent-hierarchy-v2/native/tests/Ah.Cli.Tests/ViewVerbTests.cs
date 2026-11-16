using System.Text;
using Ah.Abstractions;
using Ah.Cli.Verbs;

namespace Ah.Cli.Tests;

/// <summary><c>ah view open --kind web</c>: the token on stdin, one request to the daemon, and the exit code for each answer.</summary>
public sealed class ViewVerbTests : IDisposable
{
    private static readonly string Token = new('a', 64);

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class FakeDaemon(int status, string body, bool timedOut = false) : IDaemonClient
    {
        public List<DaemonRequest> Asked { get; } = [];

        public Task<DaemonInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<DaemonInfo?>(null);

        public Task<DaemonInfo?> EnsureDaemonAsync(TimeSpan budget, CancellationToken cancellationToken = default) => Task.FromResult<DaemonInfo?>(null);

        public Task<DaemonReply> RequestAsync(DaemonRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            return Task.FromResult(new DaemonReply(status, Encoding.UTF8.GetBytes(body), TimedOut: timedOut));
        }

        public Task DrainAsync(DaemonInfo daemon, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RecordFallback(string verb, string reason)
        {
        }
    }

    private sealed record Outcome(int Code, string Out, string Err, FakeDaemon Daemon);

    private Outcome Run(FakeDaemon daemon, string? stdin, params string[] args)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var context = new CliContext(
            env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501,
            () => stdin is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(stdin));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = ViewVerb.Run(context, daemon, args, stdout, stderr);
        return new Outcome(code, stdout.ToString(), stderr.ToString(), daemon);
    }

    private Outcome Web(FakeDaemon daemon, string? stdin) => Run(daemon, stdin, "open", "--kind", "web");

    private static string TokenLine(string token) => $"{{\"token\":\"{token}\"}}\n";

    [Fact]
    public void AnOpenedPageIsExitZeroAndTheRequestIsOnePostCarryingOnlyTheToken()
    {
        var result = Web(new FakeDaemon(200, "{\"status\":\"started\",\"opened\":true}"), TokenLine(Token));

        Assert.Equal(0, result.Code);
        Assert.Equal("Web opened\n", result.Out.Replace("\r\n", "\n", StringComparison.Ordinal));
        var request = Assert.Single(result.Daemon.Asked);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/ui/start", request.PathAndQuery);
        Assert.Equal($"{{\"token\":\"{Token}\"}}", Encoding.UTF8.GetString(request.Body.Span));
        Assert.False(request.StartIfDown);
        Assert.DoesNotContain(Token, result.Out + result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void AReusedPageIsOpenedToo()
    {
        Assert.Equal(0, Web(new FakeDaemon(200, "{\"status\":\"reused\",\"opened\":true}"), TokenLine(Token)).Code);
    }

    [Fact]
    public void APageTheBrowserWasNotHandedIsExitThreeWithTheCommandToRun()
    {
        var result = Web(new FakeDaemon(200, "{\"status\":\"started\",\"opened\":false}"), TokenLine(Token));

        Assert.Equal(3, result.Code);
        Assert.Contains("Web did not open (no browser); run `ahd ui start` in a terminal", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedTokenIsExitFourWithTheBandButtonSentenceAndNothingOnStdout()
    {
        var result = Web(new FakeDaemon(403, "{\"error\":{\"code\":\"user_only\",\"message\":\"x\"}}"), TokenLine(Token));

        Assert.Equal(4, result.Code);
        Assert.Contains("web view can only be opened from the band button", result.Err, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Out);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"token\":7}")]
    [InlineData("{\"token\":\"\"}")]
    [InlineData("{\"token\":\"aaaa\"}")]
    [InlineData("{\"nope\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    [InlineData("{\"token\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")]
    [InlineData("{\"token\":\"gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg\"}")]
    [InlineData("{\"token\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}")]
    public void MissingOrGarbledStdinIsExitTwoAndNothingIsSent(string? stdin)
    {
        var result = Web(new FakeDaemon(200, "{\"status\":\"started\",\"opened\":true}"), stdin);

        Assert.Equal(2, result.Code);
        Assert.Empty(result.Daemon.Asked);
    }

    private sealed class OpenEndedStream(byte[] first) : Stream
    {
        private readonly ManualResetEventSlim _closed = new(false);
        private int _served;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served == 0)
            {
                _served = 1;
                first.CopyTo(buffer, offset);
                return first.Length;
            }

            _closed.Wait();
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            _closed.Set();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void AStdinThatNeverClosesEndsTheReadWithNothingInUnderTwoSeconds()
    {
        using var input = new OpenEndedStream(Encoding.UTF8.GetBytes("{\"token\":\"aaaa"));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var bytes = ViewVerb.ReadBounded(input, 4096, TimeSpan.FromMilliseconds(300));

        Assert.True(bytes.IsEmpty);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), clock.Elapsed.ToString());
    }

    [Fact]
    public void ATokenLineEndsTheReadWithoutWaitingForTheStreamToClose()
    {
        using var input = new OpenEndedStream(Encoding.UTF8.GetBytes(TokenLine(Token)));

        var bytes = ViewVerb.ReadBounded(input, 4096, TimeSpan.FromSeconds(30));

        Assert.Equal(TokenLine(Token), Encoding.UTF8.GetString(bytes.Span));
    }

    [Fact]
    public void AReadStopsAtTheCapEvenWhenNoLineEndsArrive()
    {
        using var input = new MemoryStream(new byte[100]);

        Assert.Equal(10, ViewVerb.ReadBounded(input, 10, TimeSpan.FromSeconds(30)).Length);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("open", "--kind")]
    [InlineData("open", "--kind", "tui")]
    [InlineData("open", "--kind", "pane")]
    [InlineData("open", "--kind", "web", "--from-band")]
    [InlineData("close", "--kind", "web")]
    [InlineData("--kind", "web")]
    public void AnythingButOpenKindWebIsAUsageErrorAndNothingIsSent(params string[] args)
    {
        var result = Run(new FakeDaemon(200, "{\"status\":\"started\",\"opened\":true}"), TokenLine(Token), args);

        Assert.Equal(2, result.Code);
        Assert.Empty(result.Daemon.Asked);
    }

    [Fact]
    public void ADaemonThatIsNotRunningIsExitThree()
    {
        var result = Web(new FakeDaemon(0, string.Empty), TokenLine(Token));

        Assert.Equal(3, result.Code);
        Assert.Contains("ahd is not running", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void ADaemonThatDoesNotAnswerInTimeIsExitThree()
    {
        var result = Web(new FakeDaemon(0, string.Empty, timedOut: true), TokenLine(Token));

        Assert.Equal(3, result.Code);
        Assert.Contains("did not answer in time", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorAnswerIsExitThreeWithTheDaemonsMessage()
    {
        var result = Web(new FakeDaemon(503, "{\"error\":{\"code\":\"ui_service\",\"message\":\"the page did not start in time\"}}"), TokenLine(Token));

        Assert.Equal(3, result.Code);
        Assert.Contains("(the page did not start in time)", result.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"status\":\"started\"}")]
    [InlineData("{\"status\":\"started\",\"opened\":\"yes\"}")]
    public void AnAnswerWithoutAnOpenedFlagIsExitThree(string body)
    {
        Assert.Equal(3, Web(new FakeDaemon(200, body), TokenLine(Token)).Code);
    }

    [Fact]
    public void EventsPrintsTheBodyOfOneGetThatNeverStartsTheDaemon()
    {
        var result = Run(new FakeDaemon(200, "{\"cursor\":9}"), null, "events", "--sub", "team:d/t", "--epoch", "e1", "--wait", "25");

        Assert.Equal(0, result.Code);
        Assert.Equal("{\"cursor\":9}\n", result.Out.Replace("\r\n", "\n", StringComparison.Ordinal));
        var request = Assert.Single(result.Daemon.Asked);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/v1/view?path=%2Fevents&since=0&epoch=e1&wait=25&sub=team%3Ad%2Ft", request.PathAndQuery);
        Assert.False(request.StartIfDown);
    }

    [Theory]
    [InlineData("600", 0)]
    [InlineData("601", 2)]
    public void EventsCapsTheWaitAtSixHundredSeconds(string wait, int code)
    {
        var result = Run(new FakeDaemon(200, "{}"), null, "events", "--sub", "a", "--wait", wait);

        Assert.Equal(code, result.Code);
        Assert.Equal(code == 0 ? 1 : 0, result.Daemon.Asked.Count);
    }

    [Fact]
    public void EventsWithNoDaemonIsExitOneWithOneStderrLineAndNoOutput()
    {
        var result = Run(new FakeDaemon(0, string.Empty), null, "events", "--sub", "team:d/t");

        Assert.Equal(1, result.Code);
        Assert.Equal(string.Empty, result.Out);
        Assert.Equal("ah view events: ahd is not running\n", result.Err.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
