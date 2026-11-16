using Ah.Abstractions;

namespace Ah.Launch.Tests;

/// <summary>The end of a pane is read only from the one instance the listing has it in.</summary>
public sealed class PaneMuxReadRecentTests
{
    private const uint Uid = 1000;
    private const string TmuxDir = "/t/tmux-1000";

    private sealed class Files(params string[] sockets) : IFileReader
    {
        public string ReadText(string path) => string.Empty;

        public byte[] ReadBytes(string path) => [];

        public FileTail ReadTail(string path, int maxBytes) => new([], 0, false);

        public FileRange ReadRange(string path, long offset, int maxBytes) => new([], 0);

        public NoFollowRead ReadNoFollow(string path, int maxBytes) => new(FileKind.File, 0, []);

        public FileStat? Stat(string path, bool followSymlinks = true) =>
            path == TmuxDir ? new FileStat(FileKind.Directory, 0, 0, 0, 0, Uid)
            : sockets.Any(s => TmuxDir + "/" + s == path || s == path) ? new FileStat(FileKind.Other, 0, 0, 0, 0, Uid)
            : null;

        public bool Exists(string path) => Stat(path) is not null;

        public string RealPath(string path) => path;

        public IReadOnlyList<string> List(string directory) => sockets;

        public bool IsExecutable(string path) => false;
    }

    private sealed class Runner(Func<IReadOnlyList<string>, MuxResult> answer) : IMuxRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public MuxResult Run(string binary, IReadOnlyList<string> args, double timeoutMs, int outputCapBytes = 1 << 20, string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null)
        {
            Calls.Add(args);
            return answer(args);
        }
    }

    private static MuxResult Out(string stdout) => new(true, stdout, string.Empty, 0, false, null);

    private static readonly MuxResult Failed = new(false, string.Empty, "boom", 1, false, null);

    private static Herdr TuiHerdr(Runner runner, Files files) =>
        new(runner, new Ah.TestKit.FakeEnvironment(new Dictionary<string, string>()), files, Ah.Core.Mux.HerdrFlavour.Tui);

    private static PaneMux Mux(Runner runner, params string[] sockets) =>
        new(runner, new Files(sockets), "/t", Uid, null, static _ => new Dictionary<string, string?>(), TuiHerdr(runner, new Files(sockets)));

    private static MuxResult Tmux(IReadOnlyList<string> args, MuxResult capture) =>
        args.Contains("list-panes") ? Out("%1 100\n") : capture;

    [Fact]
    public void ATmuxPaneIsReadJoinedFromItsInstance()
    {
        var runner = new Runner(args => Tmux(args, Out("the end\n")));

        Assert.Equal("the end\n", Mux(runner, "default").ReadRecent("tmux", "%1", 40));
        Assert.Equal(["-S", TmuxDir + "/default", "capture-pane", "-p", "-J", "-S", "-40", "-t", "%1"], runner.Calls[^1]);
    }

    [Fact]
    public void AHerdrPaneIsReadUnwrappedFromItsInstance()
    {
        const string Socket = "/h/herdr.sock";
        var runner = new Runner(args => args[0] switch
        {
            "session" => Out("{\"result\":{\"sessions\":[]}}"),
            "pane" when args[1] == "list" => Out("{\"result\":{\"panes\":[{\"pane_id\":\"w1:p1\"}]}}"),
            _ => Out("the end\n"),
        });
        var mux = new PaneMux(runner, new Files(Socket), "/t", Uid, Socket, static _ => new Dictionary<string, string?>(), TuiHerdr(runner, new Files(Socket)));

        Assert.Equal("the end\n", mux.ReadRecent("herdr", "w1:p1", 40));
        Assert.Equal(["pane", "read", "w1:p1", "--source", "recent-unwrapped", "--lines", "40"], runner.Calls[^1]);
    }

    [Fact]
    public void APaneNoInstanceListsIsNotRead()
    {
        var runner = new Runner(args => Tmux(args, Out("not this")));

        Assert.Null(Mux(runner, "default").ReadRecent("tmux", "%2", 40));
        Assert.DoesNotContain(runner.Calls, c => c.Contains("capture-pane"));
    }

    [Fact]
    public void APaneTwoInstancesListIsNotRead()
    {
        var runner = new Runner(args => Tmux(args, Out("not this")));

        Assert.Null(Mux(runner, "one", "two").ReadRecent("tmux", "%1", 40));
        Assert.DoesNotContain(runner.Calls, c => c.Contains("capture-pane"));
    }

    [Fact]
    public void AFailedReadIsNull() =>
        Assert.Null(Mux(new Runner(args => Tmux(args, Failed)), "default").ReadRecent("tmux", "%1", 40));

    [Fact]
    public void ATransportThatIsNotAMultiplexerIsNull() =>
        Assert.Null(Mux(new Runner(_ => Out("not this")), "default").ReadRecent("headless", "%1", 40));
}
