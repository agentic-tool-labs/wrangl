using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Launch.Tests;

/// <summary>
/// A scripted multiplexer behind <see cref="IProcessRunner"/>: it answers the herdr, tmux, shell, ps and codex calls the launch core
/// makes, keeps a model of the panes and agents herdr would hold, and records every request. Nothing here starts a process.
/// </summary>
internal sealed class FakeMux : IProcessRunner
{
    private readonly object _gate = new();
    private int _nextPane = 2;

    internal List<ProcessRequest> Requests { get; } = [];

    /// <summary>One line per call: the argv joined by spaces (a shell command is <c>/bin/sh -c &lt;command&gt;</c>).</summary>
    internal List<string> Calls { get; } = [];

    internal List<string> Panes { get; } = ["1-1"];

    internal Dictionary<string, (double X, double Y, double W, double H)> Rects { get; } = new()
    {
        ["1-1"] = (0, 0, 200, 50),
    };

    /// <summary>The agent status herdr reports for a name; absent means <c>agent_not_found</c>.</summary>
    internal Dictionary<string, string> Agents { get; } = [];

    /// <summary>The pane each held agent name sits on, for <c>agent get</c>; absent leaves the pane out of the answer.</summary>
    internal Dictionary<string, string> AgentPanes { get; } = [];

    /// <summary>The foreground pids of a pane for <c>pane process-info</c>; absent means only the pane's shell.</summary>
    internal Dictionary<string, (int Pid, string[]? Argv)[]> Foreground { get; } = [];

    internal const int ShellPid = 100;

    internal Dictionary<string, string> Screens { get; } = [];

    internal Dictionary<string, string> Buffers { get; } = [];

    /// <summary>The names <c>herdr agent start</c> refuses with a given error code, for the first <c>n</c> attempts.</summary>
    internal Dictionary<string, (string Code, int Times)> StartFailures { get; } = [];

    /// <summary>The stdout herdr prints on a successful start for a name; null prints a plain agent record.</summary>
    internal string DefaultStatusAfterStart { get; set; } = "idle";

    internal string? TmuxPanePrefix { get; set; } = "%";

    internal bool TmuxAvailable { get; set; } = true;

    internal int TmuxWindows { get; private set; }

    internal Func<ProcessRequest, ProcessResult?>? Override { get; set; }

    internal string CodexVersion { get; set; } = string.Empty;

    internal string PsOutput { get; set; } = string.Empty;

    internal bool PsFails { get; set; }

    internal Func<IReadOnlyList<string>, ProcessResult?>? Tmux { get; set; }

    public ProcessResult Run(ProcessRequest request)
    {
        lock (_gate)
        {
            Requests.Add(request);
            Calls.Add(string.Join(' ', request.Argv));
            if (Override?.Invoke(request) is { } over)
            {
                return over;
            }

            var argv = request.Argv;
            return argv[0] switch
            {
                "herdr" => Herdr(argv),
                "tmux" => TmuxCall(argv),
                "/bin/sh" => Shell(argv, request),
                "codex" => argv.Count > 1 && argv[1] == "--version" && CodexVersion.Length > 0 ? Out($"codex-cli {CodexVersion}\n") : Exit(127),
                "ps" => PsFails ? Exit(1) : Out(PsOutput),
                _ => Exit(127),
            };
        }
    }

    internal static ProcessResult Out(string stdout) => new(0, null, false, false, Encoding.UTF8.GetBytes(stdout), default);

    internal static ProcessResult Exit(int code, string stdout = "", string stderr = "") =>
        new(code, null, false, false, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr));

    private ProcessResult Shell(IReadOnlyList<string> argv, ProcessRequest request)
    {
        // The launch command is a herdr or tmux line; run it through the same model as if the shell had executed it.
        var words = SplitShell(argv[2]);
        if (words.Count == 0)
        {
            return Exit(0);
        }

        return words[0] switch
        {
            "herdr" => Herdr(words),
            "tmux" => TmuxCall(words),
            _ => Exit(0),
        };
    }

    private ProcessResult Herdr(IReadOnlyList<string> argv)
    {
        var a = argv.Count > 1 ? argv[1] : string.Empty;
        var b = argv.Count > 2 ? argv[2] : string.Empty;
        if (a == "pane" && b == "layout")
        {
            var panes = new JsonArray();
            foreach (var id in Panes)
            {
                var r = Rects[id];
                panes.Add(new JsonObject
                {
                    ["pane_id"] = id,
                    ["rect"] = new JsonObject { ["x"] = r.X, ["y"] = r.Y, ["width"] = r.W, ["height"] = r.H },
                });
            }

            return Out(Json(new JsonObject { ["result"] = new JsonObject { ["layout"] = new JsonObject { ["panes"] = panes } } }));
        }

        if (a == "pane" && b == "split")
        {
            var target = Flag(argv, "--pane")!;
            var direction = Flag(argv, "--direction")!;
            var t = Rects[target];
            var id = $"1-{_nextPane++}";
            if (direction == "right")
            {
                var w1 = Math.Ceiling(t.W / 2);
                Rects[id] = (t.X + w1, t.Y, t.W - w1, t.H);
                Rects[target] = (t.X, t.Y, w1, t.H);
            }
            else
            {
                var h1 = Math.Ceiling(t.H / 2);
                Rects[id] = (t.X, t.Y + h1, t.W, t.H - h1);
                Rects[target] = (t.X, t.Y, t.W, h1);
            }

            Panes.Add(id);
            return Out(Json(new JsonObject { ["result"] = new JsonObject { ["pane"] = new JsonObject { ["pane_id"] = id } } }));
        }

        if (a == "pane" && b == "rename")
        {
            return Out("{\"result\":{}}");
        }

        if (a == "pane" && b == "read")
        {
            return Out("recent pane output\n");
        }

        if (a == "pane" && b == "close")
        {
            Panes.Remove(argv[3]);
            return Out("{\"result\":{}}");
        }

        if (a == "agent" && b == "start")
        {
            var name = argv[3];
            if (StartFailures.TryGetValue(name, out var failure) && failure.Times > 0)
            {
                StartFailures[name] = (failure.Code, failure.Times - 1);
                return Exit(1, Json(new JsonObject { ["error"] = new JsonObject { ["code"] = failure.Code, ["message"] = "refused" } }));
            }

            Agents[name] = DefaultStatusAfterStart;
            return Out(Json(new JsonObject { ["result"] = new JsonObject { ["agent"] = new JsonObject { ["name"] = name } } }));
        }

        if (a == "agent" && b == "get")
        {
            var name = argv[3];
            if (!Agents.TryGetValue(name, out var status))
            {
                return Exit(1, Json(new JsonObject { ["error"] = new JsonObject { ["code"] = "agent_not_found" } }));
            }

            var held = new JsonObject { ["agent_status"] = status, ["interactive_ready"] = status is "idle" or "done" };
            if (AgentPanes.TryGetValue(name, out var holderPane))
            {
                held["pane_id"] = holderPane;
            }

            return Out(Json(new JsonObject { ["result"] = new JsonObject { ["agent"] = held } }));
        }

        if (a == "pane" && b == "process-info")
        {
            var pane = Flag(argv, "--pane")!;
            var foreground = new JsonArray();
            foreach (var (pid, processArgv) in Foreground.TryGetValue(pane, out var running) ? running : [(ShellPid, ["-zsh"])])
            {
                var process = new JsonObject { ["pid"] = pid };
                if (processArgv is not null)
                {
                    var words = new JsonArray();
                    foreach (var word in processArgv)
                    {
                        words.Add(word);
                    }

                    process["argv"] = words;
                }

                foreground.Add(process);
            }

            return Out(Json(new JsonObject
            {
                ["result"] = new JsonObject { ["process_info"] = new JsonObject { ["pane_id"] = pane, ["shell_pid"] = ShellPid, ["foreground_processes"] = foreground } },
            }));
        }

        if (a == "agent" && b == "rename" && argv.Count > 4 && argv[4] == "--clear")
        {
            foreach (var held in AgentPanes.Where(p => p.Value == argv[3]).Select(p => p.Key).ToList())
            {
                Agents.Remove(held);
                AgentPanes.Remove(held);
            }

            return Out("{\"result\":{}}");
        }

        if (a == "agent" && b == "read")
        {
            return Out(Screens.TryGetValue(argv[3], out var screen) ? screen : string.Empty);
        }

        if (a == "agent" && b == "list")
        {
            var agents = new JsonArray();
            foreach (var (name, status) in Agents)
            {
                agents.Add(new JsonObject { ["name"] = name, ["pane_id"] = "1-9", ["agent_status"] = status });
            }

            return Out(Json(new JsonObject { ["result"] = new JsonObject { ["agents"] = agents } }));
        }

        return Out("{}");
    }

    private ProcessResult TmuxCall(IReadOnlyList<string> argv)
    {
        if (Tmux?.Invoke(argv) is { } scripted)
        {
            return scripted;
        }

        if (!TmuxAvailable)
        {
            return Exit(127);
        }

        var sub = argv.Count > 1 ? argv[1] : string.Empty;
        if (sub == "new-window")
        {
            TmuxWindows++;
            return Out($"{TmuxPanePrefix}{TmuxWindows}\n");
        }

        return Exit(0);
    }

    private static string? Flag(IReadOnlyList<string> argv, string flag)
    {
        for (var i = 0; i < argv.Count - 1; i++)
        {
            if (argv[i] == flag)
            {
                return argv[i + 1];
            }
        }

        return null;
    }

    private static string Json(JsonObject o) => JsonText.Stringify(o, 0);

    /// <summary>A minimal POSIX word split: single and double quotes group, a backslash escapes a double-quoted or bare character.</summary>
    internal static List<string> SplitShell(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }
                else if (c == '\\' && q == '"' && i + 1 < text.Length)
                {
                    current.Append(text[++i]);
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                inWord = true;
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
                inWord = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    words.Add(current.ToString());
                    current.Clear();
                    inWord = false;
                }
            }
            else
            {
                current.Append(c);
                inWord = true;
            }
        }

        if (inWord)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
