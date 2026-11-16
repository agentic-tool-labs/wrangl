using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Config;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Launch.Tests;

/// <summary>Calls the native launch function a unit table names, with the case's arguments.</summary>
internal static class LaunchTableFns
{
    /// <summary>The tables this project runs, by file path under <c>tests/contracts/units</c> without its extension.</summary>
    internal static readonly HashSet<string> Tables =
    [
        "lib-roster/herdrOnPath",
        "lib-herdr/agentsFromList", "lib-herdr/AGENT_LIST_ARGV", "lib-herdr/herdrCallSync",
        "lib-blocked/excerptOf", "lib-blocked/promptNoteLine", "lib-blocked/readVisibleScreen", "lib-blocked/herdrAgentList", "lib-blocked/readPromptCore",
        "lib-mux/GUARDED_BINARIES", "lib-mux/guardShellCommand",
        "lib-snapshot/sessionIdOf", "lib-snapshot/buildSnapshot", "lib-snapshot/claudeProcessHas", "lib-snapshot/aliveReason",
        "roster-launch/next-split", "roster-launch/create-plan",
    ];

    internal static JsonValue? Dispatch(string key, UnitCase c, UnitSandbox sandbox)
    {
        var rig = LaunchRig.Create(sandbox, c);
        JsonValue? Arg(int i) => i < c.Args.Count ? sandbox.Decode(c.Args[i]) : null;
        string? Text(int i) => Arg(i) is { IsText: true } t ? t.AsString() : null;
        string? Stringified(int i) => Arg(i) is { IsNull: false } v ? JsConvert.ToJsString(v) : null;

        switch (key)
        {
            case "lib-roster/herdrOnPath":
                return rig.Herdr.OnPath();
            case "lib-herdr/agentsFromList":
                return rig.Herdr.AgentsFromList(Arg(0) ?? JsonValue.Null);
            case "lib-herdr/AGENT_LIST_ARGV":
                return Strings("agent", "list");
            case "lib-herdr/herdrCallSync":
            {
                var args = Arg(0)!.Value.AsArray().Select(v => v.AsString()).ToList();
                var opts = Arg(1) is { IsMap: true } o ? o.AsObject() : new JsonObject();
                double? timeout = opts.Get("timeoutMs") is { IsNumber: true } t ? t.AsNumber() : null;
                if (opts.Get("allowFailure") is { IsTruthy: true })
                {
                    var raw = rig.Herdr.InvokeAllowFailure(args, timeout);
                    return new JsonObject
                    {
                        ["ok"] = raw.Ok,
                        ["stdout"] = raw.Stdout,
                        ["stderr"] = raw.Stderr,
                        ["status"] = raw.Status is { } status ? status : JsonValue.Null,
                    };
                }

                return rig.Herdr.Invoke(args, timeout);
            }

            case "lib-blocked/excerptOf":
                return rig.Reading.ExcerptOf(Stringified(0), Arg(1) is { IsNull: false } n ? JsConvert.ToJsString(n) : null);
            case "lib-blocked/promptNoteLine":
                return rig.Reading.PromptNoteLine(Text(0)!, Text(1), Text(2)) is { } line ? line : JsonValue.Null;
            case "lib-blocked/readVisibleScreen":
                return rig.Reading.ReadVisibleScreen(Text(0)!);
            case "lib-blocked/herdrAgentList":
                return rig.Reading.HerdrAgentList() is { } agents ? agents : JsonValue.Null;
            case "lib-blocked/readPromptCore":
                return rig.Reading.ReadPromptCore(Arg(0) ?? JsonValue.Null, Text(1));
            case "lib-mux/GUARDED_BINARIES":
                return Strings([.. rig.Guard.GuardedBinaries]);
            case "lib-mux/guardShellCommand":
                rig.Guard.GuardShellCommand(Text(0)!);
                return null;
            case "lib-snapshot/sessionIdOf":
                return Snapshots(rig).SessionIdOf(Text(0)!, Text(1), Arg(2) ?? JsonValue.Null);
            case "lib-snapshot/buildSnapshot":
            {
                var at = Arg(2) is { IsText: true } d ? DateTimeOffset.Parse(d.AsString(), CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow;
                return Snapshots(rig).BuildSnapshot(Text(0)!, Text(1), at) is { } doc ? doc : JsonValue.Null;
            }

            case "lib-snapshot/claudeProcessHas":
                return Snapshots(rig).ClaudeProcessHas(Text(0)!) is { } has ? has : JsonValue.Null;
            case "lib-snapshot/aliveReason":
                return Snapshots(rig).AliveReason(Arg(0)) is { } reason ? reason : JsonValue.Null;
            case "roster-launch/next-split":
                return NextSplit(Arg(0)!.Value.AsArray().Select(v => v.AsString()).ToList());
            case "roster-launch/create-plan":
                return Plan(rig, c, Arg(0)!.Value.AsArray().Select(v => v.AsString()).ToList());
            default:
                throw new InvalidOperationException("no dispatch for " + key);
        }
    }

    private static SnapshotBuilder Snapshots(LaunchRig rig) =>
        new(rig.Io.Files, rig.Runner, rig.Clock, rig.Config, rig.Roster, rig.Hierarchy.Roster, rig.Hierarchy.Core, rig.Mailbox, rig.Hierarchy.StateCalls);

    private static JsonArray Strings(params string[] values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(v);
        }

        return array;
    }

    /// <summary>The value after <paramref name="flag"/> in <paramref name="argv"/>, or null.</summary>
    private static string? Flag(List<string> argv, string flag)
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

    /// <summary><c>roster.mjs next-split</c>: the CLI's JSON, as the oracle reports it.</summary>
    private static JsonObject NextSplit(List<string> argv)
    {
        var mode = Flag(argv, "--mode")!;
        var paneCount = double.Parse(Flag(argv, "--pane-count")!, CultureInfo.InvariantCulture);
        var self = Flag(argv, "--self")!;
        var created = JsonText.Parse(Flag(argv, "--created")!).AsArray().Select(v => v.AsString()).ToList();
        var geometry = JsonText.Parse(Flag(argv, "--geometry")!).AsArray();
        try
        {
            var d = new LayoutRules().NextSplit(mode, paneCount, self, created, geometry);
            return new JsonObject
            {
                ["exit"] = 0,
                ["stdout"] = new JsonObject { ["target"] = d.Target is null ? JsonValue.Null : d.Target, ["direction"] = d.Direction },
                ["stderr"] = string.Empty,
            };
        }
        catch (InvalidOperationException err)
        {
            return new JsonObject { ["exit"] = 2, ["stdout"] = string.Empty, ["stderr"] = $"roster.mjs: {err.Message}\n" };
        }
    }

    /// <summary>
    /// The launch shapes (and layout plan) <c>roster.mjs create --plan</c> reports, or the launch <c>spawn-ad-hoc --dry-run</c> reports for
    /// a custom member: rebuilt from the roster file in the sandbox and the names and routes Node derived for it.
    /// </summary>
    private static JsonObject Plan(LaunchRig rig, UnitCase c, List<string> argv)
    {
        var expectedStdout = c.Expect?.Member("stdout");
        var team = Flag(argv, "--team");
        var transport = new MuxTransports(rig.Env, rig.Mux).Detect();
        var teamFile = team ?? rig.Config.TeamPrefix(rig.Cwd, null);
        var registry = rig.Config.ResolveConfig(rig.Cwd, new ResolveOptions(Team: team));
        var ctx = new LaunchContext(rig.Cwd, teamFile, rig.Config.TeamPrefix(rig.Cwd, team), registry, argv[0], team);
        var shapes = Shapes(rig);

        if (argv[0] == "spawn-ad-hoc")
        {
            var name = expectedStdout?.Member("name") is { IsText: true } n ? n.AsString() : "alpha-architect";
            var member = new JsonObject
            {
                ["role"] = argv[1],
                ["name"] = name,
                ["route"] = rig.Registry.RouteMail,
                ["kind"] = Flag(argv, "--kind")!,
                ["harness"] = Flag(argv, "--harness")!,
            };
            if (Flag(argv, "--command") is { } command)
            {
                member["command"] = command;
            }

            var spawn = member.Get("command") is { IsTruthy: true } ? shapes.SpawnShape(member, transport, ctx).Shape : null;
            if (spawn?.Get("refuse") is { IsTruthy: true } refuse)
            {
                return new JsonObject { ["exit"] = 2, ["stdout"] = string.Empty, ["stderr"] = $"roster.mjs: spawn-ad-hoc: {JsConvert.ToJsString(refuse)}\n" };
            }

            return new JsonObject
            {
                ["exit"] = 0,
                ["stdout"] = new JsonObject { ["launch"] = spawn?.Get("launch") ?? new JsonArray() },
            };
        }

        var roster = JsonText.Parse(rig.Io.Files.ReadBytes(Path.Combine(rig.Cwd, ".claude", "ah2", "agent-hierarchy.json"))).AsObject();
        var rosterMembers = roster.Get("roster")!.Value.Member("members")!.Value.AsArray();
        var planned = expectedStdout!.Value.Member("members")!.Value.AsArray();
        var members = new JsonArray();
        var withRoutes = new List<JsonValue>();
        for (var i = 0; i < rosterMembers.Count; i++)
        {
            var row = rosterMembers[i].AsObject().Clone();
            row["name"] = planned[i].Member("name")!.Value;
            row["route"] = planned[i].Member("route")!.Value;
            withRoutes.Add(row);
            JsonValue spawn = rig.Config.RouteHasPane(row.Get("route"))
                ? shapes.ShapeFor(row, transport, ctx).Shape
                : JsonValue.Null;
            members.Add(new JsonObject { ["name"] = row["name"], ["spawn"] = spawn });
        }

        var mode = expectedStdout.Value.Member("layout")!.Value.Member("mode")!.Value.AsString();
        var layoutPlan = new LayoutRules().LayoutPlan(mode, transport, withRoutes, rig.Cwd);
        return new JsonObject
        {
            ["exit"] = 0,
            ["stdout"] = new JsonObject
            {
                ["transport"] = transport,
                ["layout_plan"] = layoutPlan is null ? JsonValue.Null : layoutPlan,
                ["members"] = members,
            },
        };
    }

    /// <summary>
    /// A rendered plan without the member marker the native shapes add to each member's environment, so it compares with the
    /// recorded Node plan: the marker in the settings the claude shapes pass, in a custom command's environment prefix and in the
    /// <c>-c</c> argument a Codex launch carries.
    /// </summary>
    internal static string WithoutMemberMarker(string rendered)
    {
        var text = Regex.Replace(rendered, @",\\+""AH_MEMBER_NAME\\+"":\\+""[^""\\]*\\+""", string.Empty);
        text = Regex.Replace(text, @"AH_MEMBER_NAME=(?:'\\\\''[^']*'\\\\''|'[^']*') ", string.Empty);
        text = Regex.Replace(text, @" '-c' 'shell_environment_policy\.set\.AH_MEMBER_NAME=\\+""[^""\\]*\\+""'", string.Empty);
        return Regex.Replace(text, @",""-c"",""shell_environment_policy\.set\.AH_MEMBER_NAME=\\+""[^""\\]*\\+""""", string.Empty);
    }

    internal static SpawnShapes Shapes(LaunchRig rig) => new(
        rig.Env,
        rig.Io.Files,
        rig.Runner,
        rig.Config,
        rig.Roster,
        rig.Rules,
        rig.Registry,
        rig.Harnesses,
        rig.Hierarchy.Messages,
        new NoTiers());

    /// <summary>What a case's recorded outcome reduces to for the tables that run Node's whole CLI: only the part R0 computes.</summary>
    internal static JsonValue ProjectExpected(string key, JsonValue expected)
    {
        if (key != "roster-launch/create-plan")
        {
            return expected;
        }

        var exit = expected.Member("exit") ?? JsonValue.Null;
        var stdout = expected.Member("stdout") ?? JsonValue.Null;
        if (stdout.IsMap && stdout.Member("members") is { IsArray: true } members)
        {
            var rows = new JsonArray();
            foreach (var m in members.AsArray())
            {
                rows.Add(new JsonObject { ["name"] = m.Member("name") ?? JsonValue.Null, ["spawn"] = m.Member("spawn") ?? JsonValue.Null });
            }

            return new JsonObject
            {
                ["exit"] = exit,
                ["stdout"] = new JsonObject
                {
                    ["transport"] = stdout.Member("transport") ?? JsonValue.Null,
                    ["layout_plan"] = stdout.Member("layout_plan") ?? JsonValue.Null,
                    ["members"] = rows,
                },
            };
        }

        if (stdout.IsMap && stdout.Member("launch") is { } launch)
        {
            return new JsonObject { ["exit"] = exit, ["stdout"] = new JsonObject { ["launch"] = launch } };
        }

        return new JsonObject { ["exit"] = exit, ["stdout"] = string.Empty, ["stderr"] = expected.Member("stderr") ?? string.Empty };
    }
}
