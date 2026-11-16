using System.Diagnostics.CodeAnalysis;
using Ah.Abstractions;

namespace Ah.Cli.Tests;

/// <summary>A throwaway directory tree for one test. Disposal removes only a directory this class created.</summary>
internal sealed class Sandbox : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateDirectory(Path.Combine(
        OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp",
        "ahprobe." + Guid.NewGuid().ToString("N")[..8]));

    internal string Root => _root.FullName;

    internal string Home => Path.Combine(Root, "home");

    internal string AhHome => Path.Combine(Home, ".claude", "ah2");

    internal string PluginRoot => Path.Combine(Root, "plugin");

    internal string Bin => Path.Combine(Root, "bin");

    internal Sandbox()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Bin);
        DatabaseTemplate.CopyTo(AhHome);
    }

    /// <summary>The real state writer over this sandbox's home, its own graph so a plant touches none of the fakes the test under way counts.</summary>
    private Ah.Cli.Composition.ServiceGraph PlantGraph()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return Ah.Cli.Composition.ServiceGraph.Build(
            Ah.Platform.PlatformModule.CreateIo(null),
            new FakeEnv(Home, AhHome, new Dictionary<string, string?> { ["HOME"] = Home, ["AH_HOME"] = AhHome, ["AH_DAEMON"] = "off" }),
            new FakeRunner(),
            new Ah.TestKit.FakeDaemonClient());
    }

    /// <summary>Removes the team stored for <paramref name="path"/> through the real <c>team clear</c> op.</summary>
    internal void ClearTeam(string path)
    {
        var graph = PlantGraph();
        Ah.TestKit.TestPlant.ClearTeam(graph.Roster, graph.Writer, path);
    }

    /// <summary>The team stored for the team file <paramref name="path"/>, read as the product reads it (the store, not the file); null when there is none.</summary>
    internal Ah.Core.Json.JsonValue ReadTeam(string path) => PlantGraph().Roster.ReadTeamFileAt(path);

    /// <summary>The names of the teams stored for the hierarchy directory <paramref name="dir"/>.</summary>
    internal IReadOnlyList<string> StoredTeamNames(string dir) => PlantGraph().Roster.TeamNamesIn(dir) ?? [];

    /// <summary>Whether a team is stored for the team file <paramref name="path"/>.</summary>
    internal bool TeamExists(string path) => !ReadTeam(path).IsNull;

    /// <summary>The stored team as one compact line, for a test that edits the document as text; a team that is not stored throws.</summary>
    internal string TeamText(string path) =>
        ReadTeam(path) is { IsNull: false } team ? Ah.Core.Json.JsonText.Stringify(team, 0) : throw new FileNotFoundException("no team is stored for " + path, path);

    /// <summary>Writes a team document through the real <c>team write</c> op.</summary>
    internal void PlantTeam(string path, string json)
    {
        var graph = PlantGraph();
        Ah.TestKit.TestPlant.Team(graph.Roster, graph.Writer, path, json);
    }

    /// <summary>
    /// Stores <paramref name="body"/> as the activity record of <paramref name="subject"/> under the hierarchy dir <paramref name="dir"/> through
    /// the real <c>activity.put</c> (per-dir-files-to-db S2: the table is the records' only home).
    /// </summary>
    internal void PlantActivity(string dir, string subject, string body)
    {
        Directory.CreateDirectory(dir);
        var graph = PlantGraph();
        Ah.TestKit.TestActivity.Put(graph.Hierarchy.StateDomains, AhHome, dir, subject, body);
    }

    /// <summary>Appends each JSON line of <paramref name="lines"/> to the roster of <paramref name="dir"/> through the real <c>peers.put</c> (per-dir-files-to-db S7).</summary>
    internal void PlantPeers(string dir, string lines)
    {
        Directory.CreateDirectory(dir);
        var graph = PlantGraph();
        Ah.TestKit.TestPeers.Put(graph.Hierarchy.StateDomains, AhHome, dir, lines);
    }

    /// <summary>The roster rows of <paramref name="dir"/>, one JSON line each, in append order.</summary>
    internal string[] PeerLines(string dir) => Ah.TestKit.TestPeers.Lines(AhHome, dir);

    /// <summary>Removes the roster rows of <paramref name="dir"/>.</summary>
    internal void ClearPeers(string dir) => Ah.TestKit.TestPeers.Clear(AhHome, dir);

    /// <summary>A sandbox whose tests plant or assert message files: explicit files mode (ah-msgs-db-only §13.5), until the slice that deletes files mode moves them.</summary>
    internal static Sandbox PlantedFiles() => new();

    /// <summary>A sandbox whose store has an explicit <c>mode.msgs = files</c> row: a home not cut over yet, for the one-way door's own tests.</summary>
    internal static Sandbox NotCutOver()
    {
        var sandbox = new Sandbox();
        _ = Ah.TestKit.TestMsgStore.FilesRoute(sandbox.AhHome);
        return sandbox;
    }

    /// <summary>One product run for a test that plants and reads messages as files: the product sees only the store (<see cref="Ah.TestKit.TestMsgStore.MirrorIn"/>).</summary>
    internal T Mirrored<T>(Func<T> run)
    {
        Ah.TestKit.TestMsgStore.MirrorIn(AhHome, Root);
        try
        {
            return run();
        }
        finally
        {
            Ah.TestKit.TestMsgStore.MirrorOut(AhHome);
        }
    }

    /// <summary>
    /// The message file of <paramref name="id"/> and <paramref name="type"/> in any <c>msgs/</c> directory under the root. <paramref name="id"/> may be
    /// the id or a <c>msg new|fill --plain</c> line, <c>&lt;path&gt;  &lt;id&gt;[  existing]</c>, whose last field other than <c>existing</c> is the id.
    /// </summary>
    internal string MsgFile(string id, string type = "request")
    {
        var bare = id.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Last(f => f != "existing");
        return Directory.EnumerateFiles(Root, $"{bare}--*--{type}.md", SearchOption.AllDirectories)
            .First(p => Path.GetFileName(Path.GetDirectoryName(p)) == "msgs");
    }

    internal string Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    internal string Executable(string relative)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Write(relative, "#!/bin/sh\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>A plugin root with the hook files and the manifests <c>where</c> reads.</summary>
    internal string InstallPlugin(string version = "1.2.3", string contract = "4", string[]? hooks = null)
    {
        Write("plugin/bin/ah-hook", "#!/bin/sh\n");
        Write("plugin/hooks/roster.mjs", "// roster\n");
        Write("plugin/hooks/msg.mjs", "// msg\n");
        foreach (var hook in hooks ?? ["sessionstart"])
        {
            Write($"plugin/hooks/{hook}.mjs", "// hook\n");
        }

        Write("plugin/.claude-plugin/plugin.json", $"{{\"name\":\"agent-hierarchy\",\"version\":\"{version}\"}}");
        Write("plugin/native/cutover.json", $"{{\"contract\":{contract}}}");
        return PluginRoot;
    }

    public void Dispose()
    {
        if (Root.Contains("ahprobe.", StringComparison.Ordinal))
        {
            _root.Delete(recursive: true);
        }
    }

    internal IReadOnlyList<string> Listing() =>
        [.. Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(Root, p)).Order()];
}

internal sealed class FakeEnv(string home, string ahHome, Dictionary<string, string?> variables, PluginInfo? plugin = null) : IAhEnvironment
{
    public string? GetVariable(string name) => variables.GetValueOrDefault(name);

    public bool TestMode => variables.GetValueOrDefault("AH_TEST") == "1";

    public string? GetTestOnly(string name) => TestMode ? GetVariable(name) : null;

    public string HomeDirectory { get; } = home;

    public string AhHome { get; } = ahHome;

    public PluginInfo? Plugin { get; } = plugin;
}

/// <summary>Thrown by the fake exec: the real one never returns, so neither may the fake.</summary>
internal sealed class ExecCalled : Exception
{
}

internal sealed class RecordingExec : IExec
{
    internal List<string> Argv { get; } = [];

    internal Dictionary<string, string?>? Environment { get; private set; }

    internal bool Called => Argv.Count > 0;

    [DoesNotReturn]
    public void Exec(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string?>? environment = null)
    {
        Argv.AddRange(argv);
        Environment = environment is null ? null : new Dictionary<string, string?>(environment);
        throw new ExecCalled();
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    /// <summary>The start time <see cref="KnowingSelf"/> gives this test process, which a team record it owns names.</summary>
    internal const string SelfStart = "2026-10-15T20:38:50.000Z";

    /// <summary>
    /// A runner whose <c>ps</c> answers this test process's start time with <see cref="SelfStart"/> and its parent with pid 1, so
    /// an ancestor walk from this process ends at once and a registration of this process proves its session.
    /// </summary>
    internal static FakeRunner KnowingSelf() => new() { Ps = SelfOnly };

    /// <summary>The <c>ps</c> answers of <see cref="KnowingSelf"/>: this process's start and parent, null for anything else.</summary>
    internal static string? SelfOnly(string[] argv)
    {
        if (argv.Length == 0 || argv[^1] != Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            return null;
        }

        return argv.Contains("lstart=") ? SelfStart + "\n" : argv.Contains("ppid=") ? "1\n" : null;
    }

    /// <summary>A live, bound registration of <paramref name="session"/> at this test process, proven by <see cref="SelfStart"/>.</summary>
    internal static void RegisterSelf(Ah.Mesh.IMailbox mailbox, string session, string teamFile, params string[] addresses) =>
        Assert.True(mailbox.WriteRegistration(new Ah.Mesh.RegistrationWrite(
            session,
            [.. addresses.Select(a => new Ah.Mesh.BoxRef(a, mailbox.BoxDirFor(teamFile, a[..a.IndexOf('@', StringComparison.Ordinal)])))],
            teamFile,
            "claude",
            Environment.ProcessId,
            PidStart: SelfStart,
            Bound: true)));

    internal List<string[]> Calls { get; } = [];

    /// <summary>The working directory each call in <see cref="Calls"/> was asked to run in, in the same order.</summary>
    internal List<string?> Directories { get; } = [];

    internal Func<string[], int> Exit { get; set; } = _ => 0;

    /// <summary>What a call writes to standard output; empty unless set.</summary>
    internal Func<string[], string>? Output { get; set; }

    /// <summary>What a call writes to standard error; empty unless set.</summary>
    internal Func<string[], string>? Stderr { get; set; }

    /// <summary>Whether a call is cut off at the output cap, as the real runner does: its group is killed, so there is no exit code.</summary>
    internal Func<string[], bool>? Truncated { get; set; }

    /// <summary>When set, a call whose argv carries <c>git</c> is run by this runner (and still recorded) instead of being scripted.</summary>
    internal IProcessRunner? GitPassthrough { get; set; }

    /// <summary>When set, what a scripted call to <c>ps</c> writes, ahead of <see cref="Output"/> and unrecorded in <see cref="Calls"/>; null passes the call on.</summary>
    internal Func<string[], string?>? Ps { get; set; }

    /// <summary>When set, a call to <c>ps</c> is run by this runner, so a process table reads the real processes.</summary>
    internal IProcessRunner? PsPassthrough { get; set; }

    // A real ps per hop was a fifth of a roster test (sampled). This process and what it runs under do not change while it runs, so each answer about them is asked once.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessResult> SelfChainAnswers = new(StringComparer.Ordinal);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> SelfChain =
        new(StringComparer.Ordinal) { [Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = 0 };

    /// <summary>The answer of <c>ps -o ppid=|lstart= -p &lt;pid&gt;</c> for this process or an ancestor of it, kept once it succeeds; any other <c>ps</c> call runs each time.</summary>
    private static ProcessResult SelfChainAnswer(IProcessRunner runner, ProcessRequest request, string[] argv)
    {
        if (!SelfChain.ContainsKey(argv[^1]) || !(argv.Contains("ppid=") || argv.Contains("lstart=")))
        {
            return runner.Run(request);
        }

        var key = string.Join(' ', argv.Skip(1)) + " TZ=" + request.Environment?.GetValueOrDefault("TZ");
        if (SelfChainAnswers.TryGetValue(key, out var kept))
        {
            return kept;
        }

        var result = runner.Run(request);
        if (result.ExitCode == 0)
        {
            SelfChainAnswers[key] = result;
            if (argv.Contains("ppid="))
            {
                SelfChain[System.Text.Encoding.UTF8.GetString(result.Stdout.Span).Trim()] = 0;
            }
        }

        return result;
    }

    public ProcessResult Run(ProcessRequest request)
    {
        var argv = request.Argv.ToArray();
        if (argv.Length > 0 && Path.GetFileName(argv[0]) == "ps" && Ps?.Invoke(argv) is { } psOut)
        {
            return new ProcessResult(0, null, false, false, System.Text.Encoding.UTF8.GetBytes(psOut), default);
        }

        Calls.Add(argv);
        Directories.Add(request.WorkingDirectory);
        if (PsPassthrough is not null && argv.Length > 0 && Path.GetFileName(argv[0]) == "ps")
        {
            return SelfChainAnswer(PsPassthrough, request, argv);
        }

        if (GitPassthrough is not null && argv.Contains("git"))
        {
            return GitPassthrough.Run(request);
        }

        var stdout = Output is null ? default : new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes(Output(argv)));
        var truncated = Truncated?.Invoke(argv) == true;
        var stderr = Stderr is null ? default : new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes(Stderr(argv)));
        return new ProcessResult(truncated ? null : Exit(argv), truncated ? 9 : null, false, truncated, stdout, stderr);
    }
}

internal sealed class Harness : IDisposable
{
    internal Harness(string argv0 = "ahd", Dictionary<string, string?>? extra = null, string? passwdHome = "")
    {
        Sandbox = new Sandbox();
        var variables = new Dictionary<string, string?>
        {
            ["HOME"] = Sandbox.Home,
            ["PATH"] = Sandbox.Bin,
            ["AH_PLUGIN_ROOT"] = null,
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                variables[key] = value;
            }
        }

        Variables = variables;
        var ahHome = variables.GetValueOrDefault("AH_HOME") is { Length: > 0 } configured ? configured : Sandbox.AhHome;
        Exec = new RecordingExec();
        Runner = new FakeRunner();
        var processPath = Sandbox.Executable("install/ahd");
        Context = new CliContext(
            new FakeEnv(Sandbox.Home, ahHome, variables),
            Exec,
            Runner,
            argv0,
            processPath,
            passwdHome == "" ? Sandbox.Home : passwdHome,
            501,
            () => System.Text.Encoding.UTF8.GetBytes(Stdin));
    }

    /// <summary>What a hook reads from standard input.</summary>
    internal string Stdin { get; set; } = string.Empty;

    internal Sandbox Sandbox { get; }

    internal Dictionary<string, string?> Variables { get; }

    internal RecordingExec Exec { get; }

    internal FakeRunner Runner { get; }

    internal CliContext Context { get; }

    internal (int Code, string Stdout, string Stderr) Run(params string[] args) => Run([], args);

    internal (int Code, string Stdout, string Stderr) Run(IReadOnlyList<ModuleRegistry> registries, params string[] args) =>
        Execute(Context, registries, args);

    /// <summary>A run of the same command as a process whose executable is reached through <paramref name="processPath"/>.</summary>
    internal (int Code, string Stdout, string Stderr) RunAs(string processPath, params string[] args) =>
        Execute(Context with { ProcessPath = processPath }, [], args);

    private (int Code, string Stdout, string Stderr) Execute(CliContext context, IReadOnlyList<ModuleRegistry> registries, string[] args)
    {
        // the message files a test wrote by hand are stored as msg new would have stored them (db mode, ah-msgs-db-only §13)
        var ahHome = Variables.TryGetValue("AH_HOME", out var set) && set is { Length: > 0 } ? set : Sandbox.AhHome;
        if (File.Exists(Path.Combine(ahHome, "state", "ah.db")))
        {
            Ah.TestKit.TestMsgStore.Sync(ahHome, Sandbox.Root);
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code;
        try
        {
            code = CliApp.Run(args, context, stdout, stderr, registries);
        }
        catch (ExecCalled)
        {
            code = 0;
        }

        return (code, stdout.ToString(), stderr.ToString());
    }

    public void Dispose() => Sandbox.Dispose();
}
