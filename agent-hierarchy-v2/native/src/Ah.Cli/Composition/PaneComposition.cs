using Ah.Abstractions;
using Ah.Commands;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Launch;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Cli.Composition;

/// <summary>
/// The services of the verbs that type into or read a member's pane, built over the launch layer's multiplexer runner and paste
/// path and the graph's mailbox, locks and mesh. Nothing starts a process until a verb asks for a live read.
/// </summary>
internal sealed class PaneComposition : IRosterPanes
{
    private readonly ServiceGraph _graph;
    private readonly IAhEnvironment _environment;
    private readonly IFileReader _files;
    private readonly IClock _clock;
    private IMeshGate? _gate;

    internal PaneComposition(ServiceGraph graph, IAhEnvironment environment, IClock clock)
    {
        _graph = graph;
        _environment = environment;
        _files = graph.Io.Files;
        _clock = clock;
        var launch = graph.Launch;
        Mux = launch.Mux;
        Paste = launch.Paste;
        Screens = RosterModule.CreateScreenRules();
        Reading = new ScreenReading(launch.Herdr, environment, graph.Config, Screens, launch.Harnesses);
        Trust = RosterModule.CreateCodexTrust(_files, environment, cwd => graph.Config.CheckoutRoot(cwd));
    }

    public IMuxRunner Mux { get; }

    public IPaste Paste { get; }

    public IScreenRules Screens { get; }

    public IScreenReading Reading { get; }

    public ICodexTrust Trust { get; }

    /// <summary>The send rule, built the first time a deliver asks.</summary>
    public IMeshGate Mesh => _gate ??= MeshModule.CreateMeshGate(
        _graph.Mailbox,
        MeshModule.CreateMeshRule(_graph.Links, _environment),
        _graph.Links,
        _graph.Hierarchy,
        _clock,
        _graph.Writer,
        _files,
        _graph.RunFacts);

    public ISleeper Sleeper { get; } = new ThreadSleeper();

    public void RecordDeliverAttempt(JsonObject row) => _graph.Mailbox.LogPasteAttempt(null, row);

    public IDisposable? HoldPasteLock(string teamName, string memberName)
    {
        var lockFile = NodePath.Join(_environment.AhHome, "run", "paste", $"{teamName}-{memberName}.lock");
        _graph.Io.Writer.Mkdir(NodePath.Dirname(lockFile));
        var text = _environment.GetVariable("AH_PASTE_LOCK_WAIT_MS");
        var wait = string.IsNullOrEmpty(text) ? 15000 : JsConvert.ToNumber(JsonValue.FromString(text));
        return _graph.Io.Locks.Acquire(
            lockFile,
            TimeSpan.FromMilliseconds(double.IsFinite(wait) && wait > 0 ? wait : 0));
    }

    private sealed class ThreadSleeper : ISleeper
    {
        public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
    }
}
