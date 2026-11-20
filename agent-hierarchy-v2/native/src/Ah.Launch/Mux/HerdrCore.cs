using System.Security.Cryptography;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Mux;
using Ah.Core.Text;

namespace Ah.Launch;

/// <summary>
/// The herdr core (herdr desktop) translator: one herdr TUI call in, the <c>hc</c> / <c>herdr-app</c> calls that do it under core, and the
/// TUI-shaped answer the callers already parse out. Agents are named by the label <c>ah.name</c>, and a pane id is a terminal address
/// (<c>t7</c>). A TUI verb it has no mapping for fails with "`&lt;verb&gt;` is not supported under herdr core" and runs nothing.
/// </summary>
/// <param name="Mux">Runs <c>hc</c> and <c>herdr-app</c>.</param>
/// <param name="Environment">This process's environment: <c>PATH</c> for a launch line, <c>HC_TERMINAL_ID</c> for the caller's own terminal.</param>
/// <param name="Now">The clock the move polls against; null: the system clock.</param>
/// <param name="Sleep">The wait between polls; null: <see cref="Thread.Sleep(TimeSpan)"/>.</param>
/// <param name="MintHex">32 lowercase hex for a minted tab or space id; null: random.</param>
public sealed record HerdrCore(
    IMuxRunner Mux,
    IAhEnvironment Environment,
    Func<DateTimeOffset>? Now = null,
    Action<TimeSpan>? Sleep = null,
    Func<string>? MintHex = null)
{
    /// <summary>The label that names an agent's terminal.</summary>
    public const string NameLabel = "ah.name";

    /// <summary>The first executable <paramref name="name"/> on <paramref name="path"/>, as an absolute path; null when none is.</summary>
    internal static string? OnPath(string name, string? path)
    {
        if (name.Contains('/'))
        {
            return null;
        }

        foreach (var dir in (path ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            // a relative entry would resolve against ah's cwd, not the pane's
            if (!System.IO.Path.IsPathRooted(dir))
            {
                continue;
            }

            var candidate = System.IO.Path.Combine(dir, name);
            if (System.IO.File.Exists(candidate)
                && !OperatingSystem.IsWindows() && (System.IO.File.GetUnixFileMode(candidate) & (System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupExecute | System.IO.UnixFileMode.OtherExecute)) != 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The label that carries a spawn's nonce, so a terminal whose address was never recorded can still be found.</summary>
    public const string SpawnLabel = "ah.spawn";

    /// <summary>A fresh spawn nonce: 16 random lowercase hex.</summary>
    public static string MintSpawnNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>How long a move waits for the park to land, and then for the app to adopt the terminal.</summary>
    public static readonly TimeSpan MoveDeadline = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(50);

    /// <summary>The TUI key names with a known core name; any other key fails.</summary>
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        ["Enter"] = "enter",
        ["C-c"] = "ctrl+c",
        ["Escape"] = "esc",
    };

    /// <summary>The message of a TUI verb that has no core mapping.</summary>
    public static string Unsupported(string verb) => $"`{verb}` is not supported under herdr core";

    /// <summary>Runs the TUI call <paramref name="args"/> under core. <paramref name="environment"/> is the per-instance env a caller pins.</summary>
    public MuxResult Run(IReadOnlyList<string> args, double timeoutMs, int outputCapBytes = 1 << 20, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var call = new Call(this, timeoutMs, outputCapBytes, CoreEnvironment(environment), environment);
        var verb = args.Count >= 2 ? $"{args[0]} {args[1]}" : string.Join(' ', args);
        var rest = args.Skip(2).ToList();
        return verb switch
        {
            "agent get" when rest.Count >= 1 => call.AgentGet(rest[0]),
            "agent list" => call.AgentList(),
            "agent wait" when rest.Count >= 1 => call.AgentWait(
                rest[0],
                Flag(rest, "--until"),
                double.TryParse(Flag(rest, "--timeout"), System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : timeoutMs),
            "agent read" when rest.Count >= 1 => call.Read(rest[0], byName: true, rest),
            "pane read" when rest.Count >= 1 => call.Read(rest[0], byName: false, rest),
            "agent prompt" when rest.Count >= 2 => call.Send(rest[0], byName: true, rest[1]),
            "pane run" when rest.Count >= 2 => call.Send(rest[0], byName: false, string.Join(' ', rest.Skip(1))),
            "agent send-keys" when rest.Count >= 2 => call.SendKeys(rest[0], rest.Skip(1).ToList()),
            "agent start" when rest.Count >= 1 => call.Start(rest[0], rest.Skip(1).ToList()),
            "agent rename" when rest.Count >= 2 => call.Rename(rest[0], rest[1]),
            "agent focus" when rest.Count >= 1 => call.Focus(rest[0]),
            "pane process-info" when Flag(rest, "--pane") is { } pane => call.ProcessInfo(pane),
            "pane layout" => call.Layout(Flag(rest, "--pane")),
            "pane split" when Flag(rest, "--pane") is { } target && Flag(rest, "--direction") is { } direction =>
                call.Split(target, direction, Flag(rest, "--cwd"), rest.Contains("--no-focus")),
            "pane close" when rest.Count >= 1 => call.Close(rest[0]),
            "pane rename" => Answer(new JsonObject()),
            "pane zoom" when rest.Count >= 1 => call.Zoom(rest[0], on: !rest.Contains("--off")),
            "pane get" when rest.Count >= 1 => call.PaneGet(rest[0]),
            "pane list" => call.PaneList(Flag(rest, "--workspace")),
            "pane move" when rest.Count >= 1 => call.Move(rest[0], rest.Skip(1).ToList()),
            "session list" => call.SessionList(),
            "tab create" => call.TabCreate(Flag(rest, "--workspace"), Flag(rest, "--label") ?? string.Empty, Flag(rest, "--cwd"), rest.Contains("--no-focus")),
            "tab list" => call.TabList(),
            "tab rename" when rest.Count >= 2 => call.App(["do", "tab.rename", "--id", rest[0], "--name", rest[1]]).Then(Answer(new JsonObject())),
            "tab close" when rest.Count >= 1 => call.App(["do", "tab.close", "--id", rest[0]]).Then(Answer(new JsonObject())),
            "tab focus" when rest.Count >= 1 => call.App(["do", "tab.select", "--id", rest[0]]).Then(Answer(new JsonObject())),
            _ => Refuse(Unsupported(verb)),
        };
    }

    /// <summary>A TUI status for a core one: <c>status.state</c> as is, <c>unknown</c> when the terminal never reported.</summary>
    public static string MapStatus(JsonValue? status) =>
        status is { IsMap: true } s && s.Member("state") is { IsText: true } state ? state.AsString()
        : status is { IsText: true } text ? text.AsString()
        : "unknown";

    /// <summary>The TUI agent type for a core <c>status.app</c>: <c>claude-code</c> is <c>claude</c>, any other value as is, null none.</summary>
    public static string? MapAgentType(JsonValue? status) =>
        status is { IsMap: true } s && s.Member("app") is { IsText: true } app && app.AsString().Length > 0
            ? app.AsString() == "claude-code" ? "claude" : app.AsString()
            : null;

    /// <summary>Whether <c>herdr-app status</c> succeeds: the app is up.</summary>
    public bool AppPresent(double timeoutMs) => new Call(this, timeoutMs, 1 << 20, CoreEnvironment(null), null).AppStatus() is not null;

    /// <summary>The live terminal labelled <c>ah.spawn=<paramref name="nonce"/></c>: one, none, or an error when unreadable or several.</summary>
    public SpawnLookup SpawnAddress(string nonce, double timeoutMs) =>
        new Call(this, timeoutMs, 1 << 20, CoreEnvironment(null), null).Holders(SpawnLabel, nonce) switch
        {
            null => new SpawnLookup(null, $"hc ls --label {SpawnLabel}={nonce} could not be read"),
            [] => new SpawnLookup(null, null),
            [var one] => new SpawnLookup(one, null),
            var many => new SpawnLookup(null, $"{many.Count} live terminals carry {SpawnLabel}={nonce} ({string.Join(", ", many)}); none is picked"),
        };

    /// <summary><c>hc label <paramref name="address"/> k=v…</c>: sets each pair on the terminal; an empty value removes that key.</summary>
    public MuxResult Label(string address, IReadOnlyList<string> pairs, double timeoutMs) =>
        new Call(this, timeoutMs, 1 << 20, CoreEnvironment(null), null).Hc(["label", address, .. pairs]);

    private static string? Flag(List<string> args, string name)
    {
        var at = args.IndexOf(name);
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
    }

    private static MuxResult Answer(JsonValue doc) => new(true, JsonText.Stringify(doc, 0) + "\n", string.Empty, 0, false, null);

    private static MuxResult Refuse(string message) => new(false, string.Empty, message, 1, false, message);

    /// <summary>
    /// A TUI-style error answer: exit 1 with <c>{"error":{code,message}}</c> on stdout. <c>agent_not_found</c> is not live;
    /// <c>agent_name_ambiguous</c> (the name label on two or more live terminals) is any other code, so indeterminate.
    /// </summary>
    private static MuxResult Error(string name, string code, string why)
    {
        var text = JsonText.Stringify(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = why } }, 0) + "\n";
        return new MuxResult(false, text, string.Empty, 1, false, $"agent {name}: {code}: {why}");
    }

    private static MuxResult NotFound(string name) => Error(name, "agent_not_found", "agent_not_found");

    private static string Quote(string word) => Ah.Core.CoreModule.QuoteShellWord(word);

    /// <summary>
    /// The env for every core call: the TUI's pinned socket becomes <c>HC_SOCKET</c>, and no Claude Code session variable is passed
    /// on. An <c>hc</c> call can start the herdr-core server, which hands its own environment to every terminal it opens after; a
    /// session's <c>CLAUDECODE</c>/<c>CLAUDE_CODE_*</c> there would mark every Claude started in herdr desktop as that session's child
    /// (transcripts off) and spread its messaging token. Null takes this process's own environment.
    /// </summary>
    internal static Dictionary<string, string?> CoreEnvironment(IReadOnlyDictionary<string, string?>? environment)
    {
        var map = environment is not null
            ? new Dictionary<string, string?>(environment, StringComparer.Ordinal)
            : System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(static e => (string)e.Key, static e => (string?)e.Value, StringComparer.Ordinal);
        // only a pinned map names a socket for core; this process's own HERDR_SOCKET_PATH is the TUI's and is dropped below
        if (environment is not null && map.Remove("HERDR_SOCKET_PATH", out var socket) && socket is { Length: > 0 })
        {
            map["HC_SOCKET"] = socket;
        }

        // A Claude session's and an ah member's own identity never reach core (a member's own values reach it through --settings).
        // Only an inherited map also drops HERDR_*: that is this process's herdr TUI pane, which belongs to no terminal core opens.
        // HERDR_APP_HOME stays: it names the herdr desktop that herdr-app talks to.
        var inherited = environment is null;
        foreach (var key in map.Keys.Where(k => SessionVariables.IsSession(k)
            || (inherited && k.StartsWith("HERDR_", StringComparison.Ordinal) && k != "HERDR_APP_HOME")).ToList())
        {
            map.Remove(key);
        }

        return map;
    }

    private string Mint(string prefix) => prefix + (MintHex?.Invoke() ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));

    private DateTimeOffset Clock() => Now?.Invoke() ?? DateTimeOffset.UtcNow;

    private void Wait() => (Sleep ?? Thread.Sleep)(PollEvery);

    /// <summary>One translated call: the timeout, output cap and env every core call it makes shares.</summary>
    private sealed class Call(HerdrCore core, double timeoutMs, int cap, IReadOnlyDictionary<string, string?>? environment, IReadOnlyDictionary<string, string?>? given)
    {
        public MuxResult Hc(IReadOnlyList<string> args) => core.Mux.Run("hc", args, timeoutMs, cap, null, environment);

        public MuxResult App(IReadOnlyList<string> args) => core.Mux.Run("herdr-app", args, timeoutMs, cap, null, environment);

        public static JsonValue? Json(MuxResult r) => r.Ok && JsonText.TryParse(Utf8Text.Encode(r.Stdout), out var v) ? v : (JsonValue?)null;

        public JsonValue? AppStatus() => Json(App(["status"]));

        private string? CallerTerminal() =>
            (given is not null && given.TryGetValue("HC_TERMINAL_ID", out var g) ? g : null) is { Length: > 0 } own ? own
            : core.Environment.GetVariable("HC_TERMINAL_ID") is { Length: > 0 } env ? env : null;

        /// <summary>The live terminal labelled <c>ah.name=<paramref name="name"/></c>, or the not-found answer to return.</summary>
        private (string? Address, MuxResult? Failure) Lookup(string name)
        {
            var listed = Hc(["ls", "--json", "--label", $"{NameLabel}={name}"]);
            if (!listed.Ok)
            {
                return (null, listed);
            }

            if (Json(listed)?.Member("terminals") is not { IsArray: true } rows)
            {
                return (null, Refuse($"hc ls produced unexpected shape (missing .terminals)"));
            }

            var live = rows.AsArray()
                .Where(r => r.Member("state") is not { IsText: true } s || s.AsString() != "finished")
                .Select(r => r.Member("address") is { IsText: true } a ? a.AsString() : null)
                .Where(a => a is not null)
                .ToList();
            return live switch
            {
                [] => (null, NotFound(name)),
                [var one] => (one, null),
                _ => (null, Error(name, "agent_name_ambiguous", $"{NameLabel}={name} matches {live.Count} live terminals ({string.Join(", ", live)}); none is picked")),
            };
        }

        /// <summary>The live terminals labelled <c><paramref name="key"/>=<paramref name="value"/></c>; null when <c>hc ls</c> fails or is unreadable.</summary>
        public List<string>? Holders(string key, string value) => LiveAddresses(Hc(["ls", "--json", "--label", $"{key}={value}"]));

        private static List<string>? LiveAddresses(MuxResult listed) =>
            Json(listed)?.Member("terminals") is { IsArray: true } rows
                ? rows.AsArray()
                    .Where(r => r.Member("state") is not { IsText: true } s || s.AsString() != "finished")
                    .Select(r => r.Member("address") is { IsText: true } a ? a.AsString() : null)
                    .OfType<string>()
                    .ToList()
                : null;

        private static MuxResult Taken(string name)
        {
            var taken = JsonText.Stringify(new JsonObject { ["error"] = new JsonObject { ["code"] = "agent_name_taken", ["message"] = $"{NameLabel}={name} is held by another live terminal" } }, 0) + "\n";
            return new MuxResult(false, taken, string.Empty, 1, false, $"agent {name}: agent_name_taken");
        }

        /// <summary>
        /// After labelling <paramref name="address"/>: when any other live terminal holds the name, yield, dropping this one's name label.
        /// Two overlapping post-checks both yield, and the user re-runs. A post-check that cannot be read yields too. With
        /// <paramref name="started"/> the terminal was made by this start (<c>hc run</c>), and a yield kills it.
        /// </summary>
        private MuxResult? LostTheName(string name, string address, bool started)
        {
            var holders = Holders(NameLabel, name);
            if (holders is null || holders.Any(h => h != address))
            {
                _ = Hc(["label", address, $"{NameLabel}="]);
                if (started)
                {
                    _ = Hc(["kill", address]);
                }

                // an unreadable check is not a name in use: its own code, so it is not told to pick another name
                return holders is null ? Error(name, "agent_name_unchecked", "could not check the name; try again") : Taken(name);
            }

            return null;
        }

        private (string? Address, MuxResult? Failure) Target(string id, bool byName) => byName ? Lookup(id) : (id, null);

        private (JsonValue? Doc, MuxResult? Failure) Get(string address)
        {
            var got = Hc(["get", address, "--json"]);
            return got.Ok
                ? Json(got) is { IsMap: true } doc ? (doc, null) : (null, Refuse($"hc get {address} produced unparseable output"))
                : (null, got);
        }

        private static bool Finished(JsonValue doc) => doc.Member("state") is { IsText: true } s && s.AsString() == "finished";

        private static string? Label(JsonValue doc, string key) =>
            doc.Member("labels")?.Member(key) is { IsText: true } v && v.AsString().Length > 0 ? v.AsString() : null;

        public MuxResult AgentGet(string name)
        {
            var (address, failure) = Lookup(name);
            if (failure is not null)
            {
                return failure;
            }

            var (doc, bad) = Get(address!);
            if (bad is not null)
            {
                return bad;
            }

            if (Finished(doc!.Value))
            {
                return NotFound(name);
            }

            return Answer(new JsonObject { ["result"] = new JsonObject { ["agent"] = AgentRow(doc.Value, address!, name) } });
        }

        private static JsonObject AgentRow(JsonValue doc, string address, string? name)
        {
            var status = doc.Member("status");
            var row = new JsonObject
            {
                ["name"] = name is not null ? name : JsonValue.Null,
                ["pane_id"] = address,
                ["agent_status"] = MapStatus(status),
            };
            if (MapAgentType(status) is { } type)
            {
                row["agent"] = type;
            }

            if (Label(doc, "herdr.tab") is { } tab)
            {
                row["tab_id"] = tab;
            }

            if (Label(doc, "herdr.space") is { } space)
            {
                row["workspace_id"] = space;
            }

            if (doc.Member("cwd") is { IsText: true } cwd)
            {
                row["cwd"] = cwd;
            }

            return row;
        }

        public MuxResult AgentList()
        {
            var listed = Hc(["ls", "--json"]);
            if (!listed.Ok)
            {
                return listed;
            }

            if (Json(listed)?.Member("terminals") is not { IsArray: true } rows)
            {
                return Refuse("hc ls produced unexpected shape (missing .terminals)");
            }

            var agents = new JsonArray();
            foreach (var r in rows.AsArray())
            {
                if (r.Member("address") is not { IsText: true } a)
                {
                    continue;
                }

                // a terminal that vanished between the listing and its get is skipped, not a failed listing
                if (Get(a.AsString()).Doc is not { } doc)
                {
                    continue;
                }

                if (!Finished(doc))
                {
                    agents.Add(AgentRow(doc, a.AsString(), Label(doc, NameLabel)));
                }
            }

            return Answer(new JsonObject { ["result"] = new JsonObject { ["agents"] = agents } });
        }

        public MuxResult Read(string id, bool byName, List<string> rest)
        {
            var (address, failure) = Target(id, byName);
            if (failure is not null)
            {
                return failure;
            }

            var lines = Flag(rest, "--source") == "visible" || Flag(rest, "--lines") is not { } n ? "0" : n;
            return Hc(["read", address!, "--lines", lines]);
        }

        public MuxResult Send(string id, bool byName, string text)
        {
            var (address, failure) = Target(id, byName);
            return failure ?? Hc(["send", address!, text]).Then(Answer(new JsonObject()));
        }

        public MuxResult SendKeys(string name, IReadOnlyList<string> keys)
        {
            var mapped = new List<string>();
            foreach (var key in keys)
            {
                if (!Keys.TryGetValue(key, out var coreKey))
                {
                    return Refuse($"key {key} has no herdr core name");
                }

                mapped.Add(coreKey);
            }

            var (address, failure) = Lookup(name);
            return failure ?? Hc(["keys", address!, .. mapped]).Then(Answer(new JsonObject()));
        }

        /// <summary>
        /// <c>agent start N [--kind K] [--pane T] [--cwd C] [-- args]</c>: the launch line, with <c>PATH</c> set to this process's, typed
        /// into pane T and labelled; with no pane, a hidden terminal from <c>hc run</c>.
        /// </summary>
        public MuxResult Start(string name, IReadOnlyList<string> rest)
        {
            var dash = rest.ToList().IndexOf("--");
            var options = dash >= 0 ? rest.Take(dash).ToList() : rest.ToList();
            var tail = dash >= 0 ? rest.Skip(dash + 1).ToList() : [];
            var kind = Flag(options, "--kind") ?? "claude";
            var pane = Flag(options, "--pane");
            var cwd = Flag(options, "--cwd");
            var nonce = Flag(options, "--spawn");

            // the launch line is typed into a pane, where a control character would act as a key
            var pathWord = "PATH=" + (core.Environment.GetVariable("PATH") ?? string.Empty);
            if (tail.Append(kind).Append(pathWord).Append(cwd ?? string.Empty).FirstOrDefault(static w => w.Any(char.IsControl)) is { } bad)
            {
                return Refuse($"a launch word holds a control character: {JsonText.Quote(bad)}");
            }

            // Names can clash across repos and core labels enforce nothing, so the refusal herdr gave under the TUI is made here.
            var listed = Hc(["ls", "--json", "--label", $"{NameLabel}={name}"]);
            if (!listed.Ok)
            {
                return listed;
            }

            if (LiveAddresses(listed) is not { } holders)
            {
                return Refuse("hc ls produced unexpected shape (missing .terminals)");
            }

            if (holders.Any(h => h != pane))
            {
                return Taken(name);
            }

            var labels = new List<string> { $"{NameLabel}={name}" };
            if (nonce is { Length: > 0 })
            {
                labels.Add($"{SpawnLabel}={nonce}");
            }

            var argv = new List<string> { "env", pathWord, kind };
            argv.AddRange(tail);
            string address;
            if (pane is { Length: > 0 } target)
            {
                var labelled = Hc(["label", target, .. labels]);
                if (!labelled.Ok)
                {
                    return labelled;
                }

                if (LostTheName(name, target, started: false) is { } lost)
                {
                    return lost;
                }

                // The pane's own login shell already has the user's PATH, and a typed line is cut near 1 KB, so the
                // line names the binary by its absolute path instead of carrying PATH (often over 1 KB on its own).
                var typed = new List<string> { OnPath(kind, core.Environment.GetVariable("PATH")) ?? kind };
                typed.AddRange(tail);
                var sent = Hc(["send", target, string.Join(' ', typed.Select(Quote))]);
                if (!sent.Ok)
                {
                    return sent;
                }

                address = target;
            }
            else
            {
                var run = new List<string> { "run", "--bg", "--json", "--no-parent" };
                foreach (var label in labels)
                {
                    run.AddRange(["--label", label]);
                }

                if (cwd is { Length: > 0 })
                {
                    run.AddRange(["--cwd", cwd]);
                }

                run.Add("--");
                run.AddRange(argv);
                var started = Hc(run);
                if (!started.Ok)
                {
                    return started;
                }

                if (Json(started)?.Member("address") is not { IsText: true } made)
                {
                    return Refuse("hc run produced unexpected shape (missing .address)");
                }

                address = made.AsString();
                if (LostTheName(name, address, started: true) is { } lost)
                {
                    return lost;
                }
            }

            return Answer(new JsonObject { ["result"] = new JsonObject { ["agent"] = new JsonObject { ["name"] = name, ["pane_id"] = address } } });
        }

        /// <summary><c>agent rename P --clear</c> removes the name label; <c>agent rename N NEW</c> relabels N's terminal.</summary>
        public MuxResult Rename(string id, string to)
        {
            if (to == "--clear")
            {
                return Hc(["label", id, $"{NameLabel}="]).Then(Answer(new JsonObject()));
            }

            var (address, failure) = Lookup(id);
            return failure ?? Hc(["label", address!, $"{NameLabel}={to}"]).Then(Answer(new JsonObject()));
        }

        public MuxResult Focus(string id)
        {
            var (address, failure) = MuxIds.IsHerdrCoreAddress(id) ? (id, null) : Lookup(id);
            return failure ?? App(["do", "pane.focus", "--terminal", address!]).Then(Answer(new JsonObject()));
        }

        public MuxResult ProcessInfo(string address)
        {
            var (doc, bad) = Get(address);
            if (bad is not null)
            {
                return bad;
            }

            var foreground = new JsonArray();
            if (doc!.Value.Member("foreground") is { IsMap: true } fg)
            {
                var proc = new JsonObject
                {
                    ["pid"] = fg.Member("pid") ?? JsonValue.Null,
                    ["argv"] = fg.Member("argv") is { IsArray: true } a ? a : new JsonArray(),
                };
                if (fg.Member("cwd") is { IsText: true } cwd)
                {
                    proc["cwd"] = cwd;
                }

                foreground.Add(proc);
            }

            var info = new JsonObject
            {
                ["shell_pid"] = doc.Value.Member("pid") ?? JsonValue.Null,
                ["foreground_processes"] = foreground,
            };
            return Answer(new JsonObject { ["result"] = new JsonObject { ["process_info"] = info } });
        }

        /// <summary>One terminal of <c>herdr-app ls</c>, with its tab and space ids.</summary>
        private sealed record Placed(string Terminal, string? Pane, string? Tab, string? Space);

        private (List<Placed>? Panes, MuxResult? Failure) Listing()
        {
            var ls = App(["ls"]);
            if (!ls.Ok)
            {
                return (null, ls);
            }

            if (Json(ls)?.Member("spaces") is not { IsArray: true } spaces)
            {
                return (null, Refuse("herdr-app ls produced unexpected shape (missing .spaces)"));
            }

            var found = new List<Placed>();
            foreach (var space in spaces.AsArray())
            {
                var spaceId = space.Member("id") is { IsText: true } s ? s.AsString() : null;
                foreach (var tab in space.Member("tabs") is { IsArray: true } tabs ? tabs.AsArray() : [])
                {
                    var tabId = tab.Member("id") is { IsText: true } t ? t.AsString() : null;
                    foreach (var term in tab.Member("terminals") is { IsArray: true } terms ? terms.AsArray() : [])
                    {
                        if (term.Member("terminal") is { IsText: true } address)
                        {
                            found.Add(new Placed(address.AsString(), term.Member("pane") is { IsText: true } p ? p.AsString() : null, tabId, spaceId));
                        }
                    }
                }
            }

            return (found, null);
        }

        public MuxResult Layout(string? pane)
        {
            var target = pane ?? CallerTerminal();
            if (target is null)
            {
                return Refuse("pane layout --current needs HC_TERMINAL_ID under herdr core");
            }

            if (AppStatus() is not { } status)
            {
                return Refuse("`pane layout` needs herdr-app under herdr core");
            }

            var (listing, failure) = Listing();
            if (failure is not null)
            {
                return failure;
            }

            var tab = listing!.FirstOrDefault(p => p.Terminal == target)?.Tab;
            if (tab is null)
            {
                return Refuse($"pane {target} is not in any herdr-app tab");
            }

            var panes = new JsonArray();
            foreach (var p in listing!.Where(p => p.Tab == tab))
            {
                var (doc, bad) = Get(p.Terminal);
                if (bad is not null)
                {
                    return bad;
                }

                var cols = doc!.Value.Member("cols") is { IsNumber: true } c ? c.AsNumber() : 0;
                var rows = doc.Value.Member("rows") is { IsNumber: true } r ? r.AsNumber() : 0;
                panes.Add(new JsonObject
                {
                    ["pane_id"] = p.Terminal,
                    ["rect"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = cols, ["height"] = rows },
                    ["size_known"] = !(cols == 200 && rows == 120),
                });
            }

            var activeTab = status.Member("tab") is { IsText: true } at ? at.AsString() : null;
            var focused = status.Member("focused_pane") is { IsText: true } fp ? listing!.FirstOrDefault(p => p.Pane == fp.AsString())?.Terminal : null;
            var layout = new JsonObject
            {
                ["positions"] = false,
                ["tab_id"] = tab,
                // nothing in core reports pane zoom (status.zoom is the window's font zoom)
                ["zoomed"] = false,
                ["focused_pane_id"] = activeTab == tab && focused is not null ? focused : JsonValue.Null,
                ["panes"] = panes,
            };
            return Answer(new JsonObject { ["result"] = new JsonObject { ["layout"] = layout } });
        }

        public MuxResult Split(string target, string direction, string? cwd, bool noFocus)
        {
            if (direction is not ("right" or "down"))
            {
                return Refuse($"pane split --direction {direction} is not supported under herdr core");
            }

            if (AppStatus() is not { } status)
            {
                return Refuse("`pane split` needs herdr-app under herdr core");
            }

            var (listing, failure) = Listing();
            if (failure is not null)
            {
                return failure;
            }

            if (listing!.FirstOrDefault(p => p.Terminal == target)?.Pane is not { } appPane)
            {
                return Refuse($"pane {target} is not in any herdr-app tab");
            }

            var split = App(["do", "pane.split", "--pane", appPane, "--direction", direction]);
            if (!split.Ok)
            {
                return split;
            }

            // the split took focus: with --no-focus it goes back to the pane that had it, whatever happens next
            try
            {
                if (Json(split)?.Member("terminal") is not { IsText: true } made)
                {
                    return Refuse("herdr-app pane.split produced unexpected shape (missing .terminal)");
                }

                var address = made.AsString();
                if (cwd is { Length: > 0 } && Get(target).Doc?.Member("cwd") is var targetCwd && !(targetCwd is { IsText: true } tc && tc.AsString() == cwd))
                {
                    var cd = Hc(["send", address, "cd " + Quote(cwd)]);
                    if (!cd.Ok)
                    {
                        return cd;
                    }
                }

                return Answer(new JsonObject { ["result"] = new JsonObject { ["pane"] = new JsonObject { ["pane_id"] = address } } });
            }
            finally
            {
                if (noFocus && status.Member("focused_pane") is { IsText: true } before)
                {
                    _ = App(["do", "pane.focus", "--pane", before.AsString()]);
                }
            }
        }

        public MuxResult Close(string address) => Hc(["kill", address]).Then(Answer(new JsonObject()));

        /// <summary>Pane zoom state is not reported under core: <c>--off</c> does nothing; <c>--on</c> is one <c>pane.zoom</c> toggle.</summary>
        public MuxResult Zoom(string address, bool on)
        {
            if (!on)
            {
                return Answer(new JsonObject());
            }

            var (listing, failure) = Listing();
            if (failure is not null)
            {
                return failure;
            }

            return listing!.FirstOrDefault(p => p.Terminal == address)?.Pane is { } appPane
                ? App(["do", "pane.zoom", "--pane", appPane]).Then(Answer(new JsonObject()))
                : Refuse($"pane {address} is not in any herdr-app tab");
        }

        public MuxResult PaneGet(string address)
        {
            if (AppStatus() is not null)
            {
                var (listing, failure) = Listing();
                if (failure is not null)
                {
                    return failure;
                }

                if (listing!.FirstOrDefault(p => p.Terminal == address) is { } placed)
                {
                    return Answer(new JsonObject { ["result"] = new JsonObject { ["pane"] = PaneRow(placed) } });
                }
            }

            var (doc, bad) = Get(address);
            if (bad is not null)
            {
                return bad;
            }

            if (Finished(doc!.Value))
            {
                return Refuse($"pane_not_found: {address}");
            }

            return Answer(new JsonObject { ["result"] = new JsonObject { ["pane"] = PaneRow(new Placed(address, null, Label(doc.Value, "herdr.tab"), Label(doc.Value, "herdr.space"))) } });
        }

        private static JsonObject PaneRow(Placed p)
        {
            var row = new JsonObject { ["pane_id"] = p.Terminal };
            if (p.Tab is not null)
            {
                row["tab_id"] = p.Tab;
            }

            if (p.Space is not null)
            {
                row["workspace_id"] = p.Space;
            }

            return row;
        }

        public MuxResult PaneList(string? workspace)
        {
            var panes = new JsonArray();
            if (AppStatus() is not null)
            {
                var (listing, failure) = Listing();
                if (failure is not null)
                {
                    return failure;
                }

                foreach (var p in listing!.Where(p => workspace is null || p.Space == workspace))
                {
                    panes.Add(PaneRow(p));
                }
            }
            else
            {
                var listed = Hc(["ls", "--json"]);
                if (!listed.Ok)
                {
                    return listed;
                }

                foreach (var r in Json(listed)?.Member("terminals") is { IsArray: true } rows ? rows.AsArray() : [])
                {
                    if (r.Member("address") is { IsText: true } a && !(r.Member("state") is { IsText: true } s && s.AsString() == "finished"))
                    {
                        panes.Add(new JsonObject { ["pane_id"] = a });
                    }
                }
            }

            return Answer(new JsonObject { ["result"] = new JsonObject { ["panes"] = panes } });
        }

        /// <summary>The one core instance, as a session-list row: the socket the call pins, else this session's terminal socket.</summary>
        public MuxResult SessionList()
        {
            var socket = (environment is not null && environment.TryGetValue("HC_SOCKET", out var pinned) ? pinned : null)
                ?? (given is not null && given.TryGetValue("HC_TERMINAL_SOCKET", out var g) ? g : null)
                ?? core.Environment.GetVariable("HC_TERMINAL_SOCKET");
            return new MuxResult(true, socket is { Length: > 0 } ? $"core {socket}\n" : string.Empty, string.Empty, 0, false, null);
        }

        public MuxResult TabCreate(string? workspace, string label, string? cwd, bool noFocus)
        {
            if (AppStatus() is not { } status)
            {
                return Refuse(Unsupported("tab create") + " without herdr-app");
            }

            var space = workspace ?? (CallerTerminal() is { } own && Get(own).Doc is { } ownDoc ? Label(ownDoc, "herdr.space") : null);
            if (space is null)
            {
                return Refuse("tab create: the caller's herdr space is unknown under herdr core");
            }

            var argv = new List<string> { "do", "tab.new", "--space", space, "--name", label };
            if (cwd is { Length: > 0 })
            {
                argv.AddRange(["--folder", cwd]);
            }

            var made = App(argv);
            if (!made.Ok)
            {
                return made;
            }

            // tab.new selected the new tab and its space: with --no-focus the old ones come back, whatever happens next
            try
            {
                return TabCreated(Json(made));
            }
            finally
            {
                if (noFocus && status.Member("space") is { IsText: true } oldSpace && status.Member("tab") is { IsText: true } oldTab)
                {
                    _ = App(["do", "space.select", "--id", oldSpace.AsString()]);
                    _ = App(["do", "tab.select", "--id", oldTab.AsString()]);
                }
            }
        }

        private static MuxResult TabCreated(JsonValue? reply)
        {
            if (reply is not { IsMap: true } doc || doc.Member("tab") is not { IsText: true } tab || doc.Member("terminal") is not { IsText: true } root)
            {
                return Refuse("herdr-app tab.new produced unexpected shape (missing .tab or .terminal)");
            }

            return Answer(new JsonObject
            {
                ["result"] = new JsonObject
                {
                    ["tab"] = new JsonObject { ["tab_id"] = tab, ["workspace_id"] = doc.Member("space") ?? JsonValue.Null },
                    ["root_pane"] = new JsonObject { ["pane_id"] = root },
                },
            });
        }

        public MuxResult TabList()
        {
            var ls = App(["ls"]);
            if (!ls.Ok)
            {
                return ls;
            }

            var tabs = new JsonArray();
            foreach (var space in Json(ls)?.Member("spaces") is { IsArray: true } spaces ? spaces.AsArray() : [])
            {
                foreach (var tab in space.Member("tabs") is { IsArray: true } list ? list.AsArray() : [])
                {
                    tabs.Add(new JsonObject
                    {
                        ["tab_id"] = tab.Member("id") ?? JsonValue.Null,
                        ["label"] = tab.Member("name") ?? JsonValue.Null,
                        ["workspace_id"] = space.Member("id") ?? JsonValue.Null,
                    });
                }
            }

            return Answer(new JsonObject { ["result"] = new JsonObject { ["tabs"] = tabs } });
        }

        /// <summary>
        /// <c>pane move &lt;t&gt; --tab X --split D | --new-tab [--workspace W]</c>: snapshot the view, park the terminal, wait for the park to
        /// land, show the target (X's space and tab, whose focused pane is the split target; or the space for a new tab), give the terminal
        /// a pane with <c>terminal.keep --place D|tab</c>, wait for it in <c>ls</c>, and restore the view in a <c>finally</c>.
        /// <c>--new-workspace</c> parks and relabels into minted <c>sp</c>/<c>tb</c> ids instead, which moves no focus. A miss keeps the
        /// terminal once more with no place, so it is never left hidden, and answers <c>changed: "false"</c>.
        /// </summary>
        public MuxResult Move(string address, List<string> rest)
        {
            var (listing, failure) = Listing();
            if (failure is not null)
            {
                return failure;
            }

            if (rest.Contains("--new-workspace") && Flag(rest, "--tab") is null && !rest.Contains("--new-tab"))
            {
                return Relabel(address, core.Mint("sp"), core.Mint("tb"));
            }

            string space;
            string? tab;
            string place;
            if (Flag(rest, "--tab") is { } x)
            {
                if (listing!.FirstOrDefault(p => p.Tab == x)?.Space is not { } xSpace)
                {
                    return Refuse($"pane move: tab {x} is not in herdr-app");
                }

                space = xSpace;
                tab = x;
                place = Flag(rest, "--split") is "down" ? "down" : "right";
            }
            else if (rest.Contains("--new-tab"))
            {
                if ((Flag(rest, "--workspace") ?? listing!.FirstOrDefault(p => p.Terminal == address)?.Space) is not { } current)
                {
                    return Refuse($"pane move: the space of {address} is unknown");
                }

                space = current;
                tab = null;
                place = "tab";
            }
            else
            {
                return Refuse(Unsupported("pane move") + " without --tab, --new-tab or --new-workspace");
            }

            if (AppStatus() is not { } view)
            {
                return Refuse("`pane move` needs herdr-app under herdr core");
            }

            try
            {
                var parked = App(["do", "terminal.park", "--terminal", address]);
                if (!parked.Ok)
                {
                    return Missed(address, "terminal.park failed: " + JsText.Trim(parked.Stderr));
                }

                if (!Until(() => Get(address).Doc is { } d && Label(d, "herdr.space") is null))
                {
                    return Missed(address, "the park did not land");
                }

                var shown = App(["do", "space.select", "--id", space]);
                if (shown.Ok && tab is not null)
                {
                    shown = App(["do", "tab.select", "--id", tab]);
                }

                if (!shown.Ok)
                {
                    return Missed(address, "showing the target failed: " + JsText.Trim(shown.Stderr));
                }

                var kept = App(["do", "terminal.keep", "--terminal", address, "--place", place]);
                if (!kept.Ok)
                {
                    return Missed(address, "terminal.keep failed: " + JsText.Trim(kept.Stderr));
                }

                var landedTab = tab ?? (Json(kept)?.Member("tab") is { IsText: true } made ? made.AsString() : null);
                return Until(() => Listing().Panes?.Any(p => p.Terminal == address && (landedTab is null ? p.Space == space : p.Tab == landedTab)) == true)
                    ? Moved(landedTab, space)
                    : Missed(address, $"herdr-app did not show {address} in {(landedTab is null ? "space " + space : "tab " + landedTab)} within {MoveDeadline.TotalSeconds:0} s");
            }
            finally
            {
                RestoreView(view);
            }
        }

        /// <summary>The <c>--new-workspace</c> move: park, wait, label into a new space and tab, wait for the app to adopt it. No focus moves.</summary>
        private MuxResult Relabel(string address, string space, string tab)
        {
            var parked = App(["do", "terminal.park", "--terminal", address]);
            if (!parked.Ok)
            {
                return Missed(address, "terminal.park failed: " + JsText.Trim(parked.Stderr));
            }

            if (!Until(() => Get(address).Doc is { } d && Label(d, "herdr.space") is null))
            {
                return Missed(address, "the park did not land");
            }

            var labelled = Hc(["label", address, $"herdr.space={space}", $"herdr.tab={tab}"]);
            if (!labelled.Ok)
            {
                return Missed(address, "hc label failed: " + JsText.Trim(labelled.Stderr));
            }

            return Until(() => Listing().Panes?.Any(p => p.Terminal == address && p.Tab == tab) == true)
                ? Moved(tab, space)
                : Missed(address, $"herdr-app did not adopt {address} into tab {tab} within {MoveDeadline.TotalSeconds:0} s");
        }

        private static MuxResult Moved(string? tab, string space)
        {
            var result = new JsonObject { ["changed"] = "true", ["workspace_id"] = space };
            if (tab is not null)
            {
                result["tab_id"] = tab;
            }

            return Answer(new JsonObject { ["result"] = new JsonObject { ["move_result"] = result } });
        }

        /// <summary>
        /// Puts the user's view back as <c>herdr-app status</c> had it: its space, its tab, then its focused pane when that pane still
        /// exists. Every app call that moves focus ends with it, on every exit path.
        /// </summary>
        public void RestoreView(JsonValue view)
        {
            if (view.Member("space") is { IsText: true } space)
            {
                _ = App(["do", "space.select", "--id", space.AsString()]);
            }

            if (view.Member("tab") is { IsText: true } tab)
            {
                _ = App(["do", "tab.select", "--id", tab.AsString()]);
            }

            if (view.Member("focused_pane") is { IsText: true } pane && Listing().Panes?.Any(p => p.Pane == pane.AsString()) == true)
            {
                _ = App(["do", "pane.focus", "--pane", pane.AsString()]);
            }
        }

        /// <summary>
        /// <c>agent wait &lt;name&gt; [--until S] [--timeout ms]</c>: checks the status, then <c>hc wait</c>s for what is left, until the
        /// status is S (without --until: anything but working) or the deadline, which answers the TUI's <c>timeout</c> error.
        /// </summary>
        public MuxResult AgentWait(string name, string? until, double deadlineMs)
        {
            var deadline = core.Clock() + TimeSpan.FromMilliseconds(deadlineMs);
            while (true)
            {
                var got = AgentGet(name);
                var status = Json(got)?.Member("result")?.Member("agent")?.Member("agent_status") is { IsText: true } s ? s.AsString() : null;
                if (!got.Ok || (until is null ? status != "working" : status == until))
                {
                    return got;
                }

                var left = deadline - core.Clock();
                if (left <= TimeSpan.Zero)
                {
                    return Error(name, "timeout", $"still {status} after {deadlineMs} ms");
                }

                var address = Json(got)!.Value.Member("result")!.Value.Member("agent")!.Value.Member("pane_id")!.Value.AsString();
                if (!Hc(["wait", address, "--timeout", Math.Ceiling(left.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), "--json"]).Ok)
                {
                    core.Wait(); // a failed hc wait returns at once; do not spin on it
                }
            }
        }

        private MuxResult Missed(string address, string why)
        {
            _ = App(["do", "terminal.keep", "--terminal", address]);
            return Answer(new JsonObject { ["result"] = new JsonObject { ["move_result"] = new JsonObject { ["changed"] = "false", ["reason"] = why } } });
        }

        private bool Until(Func<bool> done)
        {
            var deadline = core.Clock() + MoveDeadline;
            while (true)
            {
                if (done())
                {
                    return true;
                }

                if (core.Clock() >= deadline)
                {
                    return false;
                }

                core.Wait();
            }
        }
    }
}

/// <summary>Sequencing for translated calls.</summary>
internal static class HerdrCoreResults
{
    /// <summary><paramref name="next"/> when <paramref name="first"/> succeeded, else <paramref name="first"/> itself.</summary>
    public static MuxResult Then(this MuxResult first, MuxResult next) => first.Ok ? next : first;
}
