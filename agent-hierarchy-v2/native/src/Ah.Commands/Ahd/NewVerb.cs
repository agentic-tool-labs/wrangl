using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Launch;

namespace Ah.Commands;

/// <summary>
/// <c>ahd new &lt;name&gt;</c>: starts an orchestrator session (<c>claude --agent wrangl:orchestrator</c>) in the caller's terminal. It writes no
/// team record: a <c>--team</c> is checked here and handed to the session as its initial prompt, and the session creates or adopts it.
/// </summary>
internal static class NewVerb
{
    internal const string Synopsis =
        "usage: ahd new <name> [-ct|--cli-type claude] [-t|--team <team>] [-m|--model <model>] [--tab] [-i|--interactive] [--dry-run]";

    /// <summary>The CLIs <c>--cli-type</c> accepts: each is a member kind <c>SpawnShape</c> can launch an orchestrator agent with.</summary>
    internal static readonly string[] CliTypes = ["claude"];

    internal sealed class Request
    {
        internal string? Name { get; set; }

        internal string CliType { get; set; } = "claude";

        internal string? Team { get; set; }

        internal string? Model { get; set; }

        internal bool Tab { get; set; }

        internal bool Interactive { get; set; }

        internal bool DryRun { get; set; }
    }

    /// <summary>
    /// The resolved launch: the argv (initial prompt last, if any) and the path the CLI was found at; with <c>--tab</c> also the
    /// <c>herdr tab create</c> argv and the herdr launch shape (its line carries the prompt) to run in that tab's pane.
    /// </summary>
    internal sealed record Plan(IReadOnlyList<string> Argv, string CliPath, string Cwd, LaunchContext Context, IReadOnlyList<string>? TabArgv = null, JsonObject? TabShape = null);

    [Verb("new")]
    internal static int New(string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var services = CommandsModule.RosterServices();
        if (services is null)
        {
            stderr.WriteLine("ahd new: the native roster services are not composed");
            return LifecycleVerbs.NotComposed;
        }

        var request = Parse(rest);
        if (request is null || (request.Name is null && !request.Interactive))
        {
            stderr.WriteLine(Synopsis);
            return 2;
        }

        var c = new RosterContext(services, RosterArgs.Parse(request.Team is null ? [] : ["--team", request.Team]), "new", services.CurrentDirectory, stdout, stderr, ["new", .. rest.Select(a => a == "-t" ? "--team" : a)]);
        try
        {
            if (request.Interactive)
            {
                if (Console.IsInputRedirected)
                {
                    throw Refuse(c, "interactive-needs-tty", "-i needs a terminal on stdin; pass the flags instead (" + Synopsis + ")");
                }

                request = Ask(c, request, Console.In);
            }

            var plan = Resolve(c, request);
            if (request.Interactive)
            {
                stderr.Write($"argv: {ShellLine(plan.Argv)}\n");
                if (!request.DryRun && !Confirm(Console.In, stderr))
                {
                    return 0;
                }
            }

            if (request.DryRun)
            {
                var argv = new JsonArray();
                argv.AddRange(plan.Argv.Select(a => (JsonValue)a));
                var dry = new JsonObject { ["ok"] = true, ["dry_run"] = true, ["argv"] = argv, ["cwd"] = plan.Cwd };
                if (plan.TabArgv is { } tabArgv)
                {
                    var tab = new JsonArray();
                    tab.AddRange(tabArgv.Select(a => (JsonValue)a));
                    dry["tab_argv"] = tab;
                    dry["tab_launch"] = plan.TabShape!["launch"].AsArray()[0];
                }

                c.Out(dry);
                return 0;
            }

            stderr.Write($"ahd new: {ShellLine(plan.Argv)}\n");
            return plan.TabArgv is null ? Foreground(plan) : InTab(c, plan);
        }
        catch (RosterFailure failure)
        {
            stderr.Write($"ahd new: {failure.Message}\n");
            return failure.ExitCode;
        }
    }

    /// <summary>The exit code of an interactive run that read end-of-file: nothing was launched.</summary>
    internal const int Aborted = 130;

    /// <summary>
    /// The <c>-i</c> prompts, in order (name, cli-type, team, model, tab), each defaulting to the flag already given, then to the
    /// resolution a flagless run would make. An answer is checked as its flag is; a refusal is printed and the question asked again.
    /// </summary>
    internal static Request Ask(RosterContext c, Request given, TextReader input)
    {
        var stderr = c.Stderr;
        string? Check(Action check)
        {
            try
            {
                check();
                return null;
            }
            catch (RosterFailure failure)
            {
                return failure.Message;
            }
        }

        var trapped = c.TrapRefusals;
        c.TrapRefusals = true;
        try
        {
            var answered = new Request { Interactive = true, DryRun = given.DryRun };
            answered.Name = Prompt(input, stderr, "name", given.Name ?? string.Empty, a => a.Length == 0 ? "a name is required" : Check(() => CheckName(c, a)));
            answered.CliType = Prompt(input, stderr, "cli-type", given.CliType, a => Check(() => CheckCliType(c, a)));
            var team = Prompt(input, stderr, "team (empty = none)", given.Team ?? string.Empty, a => a.Length == 0 ? null : Check(() => TeamPrompt(c, a)));
            answered.Team = team.Length == 0 ? null : team;
            string resolvedModel;
            var rosterRefused = false;
            try
            {
                resolvedModel = ResolveModel(c, null);
            }
            catch (RosterFailure)
            {
                resolvedModel = "inherit";
                rosterRefused = true;
            }

            var model = Prompt(input, stderr, "model", given.Model ?? resolvedModel, a => Check(() => ResolveModel(c, a)));
            answered.Model = ChosenModel(given.Model, model, resolvedModel, rosterRefused);
            var tab = Prompt(input, stderr, "tab (y/N)", given.Tab ? "y" : "n", a => a is "y" or "Y" or "n" or "N" ? null : "answer y or n");
            answered.Tab = tab is "y" or "Y";
            return answered;
        }
        finally
        {
            c.TrapRefusals = trapped;
        }
    }

    /// <summary>
    /// The <c>-m</c> an interactive answer stands for: null (resolve as a flagless run would) only when no flag was given and the
    /// answer is the resolution's own model. A roster model that was refused shows <c>inherit</c> as the default; accepting it
    /// must inherit, not re-read the roster and refuse again.
    /// </summary>
    internal static string? ChosenModel(string? flag, string answer, string resolved, bool rosterRefused) =>
        flag is null && answer == resolved && !rosterRefused ? null : answer;

    /// <summary>
    /// One line prompt on <paramref name="stderr"/>: Enter takes <paramref name="defaultValue"/>; an answer <paramref name="check"/>
    /// rejects (a message) is printed and asked again; end-of-file aborts with <see cref="Aborted"/>.
    /// </summary>
    internal static string Prompt(TextReader input, TextWriter stderr, string label, string defaultValue, Func<string, string?> check, string? shown = null)
    {
        while (true)
        {
            stderr.Write(shown ?? $"{label} [{defaultValue}]: ");
            var line = input.ReadLine() ?? throw new RosterFailure("aborted; nothing was launched", null, Aborted);
            var answer = line.Trim().Length == 0 ? defaultValue : line.Trim();
            if (check(answer) is not { } problem)
            {
                return answer;
            }

            stderr.Write($"ahd new: {problem}\n");
        }
    }

    /// <summary><c>Launch? [Y/n]</c>: Enter or y launches, n does not; end-of-file aborts with <see cref="Aborted"/>.</summary>
    internal static bool Confirm(TextReader input, TextWriter stderr) =>
        Prompt(input, stderr, "Launch?", "y", a => a is "y" or "Y" or "n" or "N" ? null : "answer y or n", "Launch? [Y/n] ") is "y" or "Y";

    /// <summary>The flags, or null for a usage error: an unknown flag, a flag missing its value, or a second name.</summary>
    internal static Request? Parse(IReadOnlyList<string> rest)
    {
        var request = new Request();
        for (var i = 0; i < rest.Count; i++)
        {
            var token = rest[i];
            string? Value() => i + 1 < rest.Count ? rest[++i] : null;
            switch (token)
            {
                case "-ct" or "--cli-type":
                    if (Value() is not { } cli)
                    {
                        return null;
                    }

                    request.CliType = cli;
                    break;
                case "-t" or "--team":
                    if (Value() is not { } team)
                    {
                        return null;
                    }

                    request.Team = team;
                    break;
                case "-m" or "--model":
                    if (Value() is not { } model)
                    {
                        return null;
                    }

                    request.Model = model;
                    break;
                case "--tab":
                    request.Tab = true;
                    break;
                case "-i" or "--interactive":
                    request.Interactive = true;
                    break;
                case "--dry-run":
                    request.DryRun = true;
                    break;
                default:
                    if (token.StartsWith('-') || request.Name is not null)
                    {
                        return null;
                    }

                    request.Name = token;
                    break;
            }
        }

        return request;
    }

    /// <summary>
    /// Every check, in the spec's order (name, cli-type, model, team, PATH, name-in-use), before any side effect; then the argv from
    /// <c>SpawnShape</c> in orchestrator mode, with the team prompt appended. Throws a <see cref="RosterFailure"/> on a refusal.
    /// </summary>
    internal static Plan Resolve(RosterContext c, Request request)
    {
        var name = request.Name!;
        CheckName(c, name);
        CheckCliType(c, request.CliType);
        var model = ResolveModel(c, request.Model);
        var prompt = TeamPrompt(c, request.Team);
        var cliPath = c.Services.Launch!.Herdr.FindOnPath(request.CliType)
            ?? throw Refuse(c, "cli-not-found", $"{request.CliType} was not found: no executable named {request.CliType} in any PATH directory");
        var herdr = c.Services.Launch!.Herdr;
        if (request.Tab && (!herdr.OnPath() || c.EnvText("HERDR_ENV") != "1"))
        {
            throw Refuse(c, "herdr-absent", "--tab needs herdr (herdr on PATH and HERDR_ENV=1): run without --tab, or start from inside herdr");
        }

        CheckNameInUse(c, name);
        if (request.Tab && herdr.AgentState(name).Live)
        {
            throw Refuse(c, "name-in-use", $"name \"{name}\" is held by a live herdr agent; pick another name");
        }

        var member = new JsonObject
        {
            ["name"] = name,
            ["role"] = "orchestrator",
            ["kind"] = request.CliType,
            ["model"] = model,
            ["autoMode"] = "auto",
        };
        var context = new LaunchContext(c.Cwd, null, c.Services.Config.TeamPrefix(c.Cwd, null), c.Registry) { Orchestrator = true };
        var outcome = c.Services.Launch!.Shapes.SpawnShape(member, "foreground", context);
        if (outcome.Fail is { } fail)
        {
            throw new RosterFailure(fail);
        }

        if (outcome.Shape.Get("argv") is not { IsArray: true } shaped)
        {
            throw new RosterFailure(outcome.Shape.Get("refuse") is { IsText: true } refuse ? refuse.AsString() : "the launch shape has no argv; nothing was launched");
        }

        var argv = shaped.AsArray().Select(a => a.AsString()).ToList();
        if (prompt is not null)
        {
            argv.Add(prompt);
        }

        if (!request.Tab)
        {
            return new Plan(argv, cliPath, c.Cwd, context);
        }

        var tabOutcome = c.Services.Launch!.Shapes.SpawnShape(member, "herdr", context);
        if (tabOutcome.Fail is { } tabFail)
        {
            throw new RosterFailure(tabFail);
        }

        if (tabOutcome.Shape.Get("refuse") is { IsText: true } tabRefuse)
        {
            throw new RosterFailure(tabRefuse.AsString());
        }

        var tabShape = tabOutcome.Shape.Clone();
        if (prompt is not null)
        {
            var line = MemberPlanning.JsConvertText(tabShape["launch"].AsArray()[0]) + " " + RosterContext.ShQuote(prompt);
            tabShape["launch"] = new JsonArray { line };
        }

        return new Plan(argv, cliPath, c.Cwd, context, ["herdr", .. StreamSupport.TabCreateArgv(c, name, c.Cwd, focus: true)], tabShape);
    }

    /// <summary>
    /// Opens the herdr tab, then starts the orchestrator in its root pane through the member launcher's herdr run step. A name herdr
    /// already holds is refused there as at spawn; the launcher closes the pane of a failed start.
    /// </summary>
    internal static int InTab(RosterContext c, Plan plan)
    {
        var herdr = c.Services.Launch!.Herdr;
        var created = herdr.Invoke(plan.TabArgv!.Skip(1).ToList());
        if (created.Member("result")?.Member("root_pane")?.Member("pane_id") is not { IsTruthy: true } pane)
        {
            if (created.Member("result")?.Member("tab")?.Member("tab_id") is { IsTruthy: true } tabId)
            {
                _ = herdr.InvokeAllowFailure(["tab", "close", MemberPlanning.JsConvertText(tabId)]);
            }

            throw new RosterFailure("herdr tab create produced unexpected shape (missing .result.root_pane.pane_id); the tab was closed and nothing was launched");
        }

        var member = new JsonObject
        {
            ["name"] = plan.Argv[plan.Argv.ToList().IndexOf("--name") + 1],
            ["role"] = "orchestrator",
            ["kind"] = MemberPlanning.JsConvertText(plan.TabShape!.Get("kind")),
            ["spawn"] = plan.TabShape,
            ["transport_id"] = pane,
        };
        var launched = c.Services.Launch!.Launcher.LaunchMemberCore(member, "herdr", plan.Context);
        var status = MemberPlanning.JsConvertText(launched.Get("launch_status"));
        if (status is "ready" or "blocked-at-startup")
        {
            return 0;
        }

        if (launched.Get("launch_result")?.Member("refused") is { IsText: true } refused && refused.AsString() == "name-in-use")
        {
            throw Refuse(c, "name-in-use", $"name \"{member["name"].AsString()}\" is held by a herdr agent; pick another name");
        }

        throw new RosterFailure($"the orchestrator did not start in the new tab: {MemberPlanning.JsConvertText(launched.Get("error"))}");
    }

    internal static void CheckName(RosterContext c, string name)
    {
        var verdict = c.Services.Config.ValidateHerdrName(JsonValue.FromString(name));
        if (verdict.Get("ok") is not { IsTruthy: true })
        {
            throw Refuse(c, "name-unusable", $"name \"{name}\" is not usable: a name must match [a-z][a-z0-9_-]{{0,31}} (a lowercase letter, then up to 31 lowercase letters, digits, _ or -)");
        }
    }

    internal static void CheckCliType(RosterContext c, string cliType)
    {
        if (!CliTypes.Contains(cliType, StringComparer.Ordinal))
        {
            throw Refuse(c, "cli-type-unsupported", $"cli-type \"{cliType}\" is not supported (supported: {string.Join(", ", CliTypes)})");
        }
    }

    /// <summary>The model: <c>-m</c>, else the resolved roster block's <c>orchestrator_model</c>, else <c>inherit</c>; refused unless the orchestrate class allows it.</summary>
    internal static string ResolveModel(RosterContext c, string? flag)
    {
        var model = flag;
        string? source = null;
        if (model is null)
        {
            var config = c.Services.Config;
            var selection = config.RosterSelection(c.Cwd, JsonValue.FromObject(new JsonObject { ["flag"] = JsonValue.Null }));
            var key = selection is not null && selection.Get("key") is { IsText: true } k ? k.AsString() : null;
            if (config.ResolveRoster(c.Cwd, key, config.TeamPrefix(c.Cwd, null), c.Registry) is { IsMap: true } resolved
                && resolved.Member("orchestrator_model") is { IsNull: false } configured)
            {
                model = configured.IsText ? configured.AsString() : JsonText.Stringify(configured, 0);
                source = MemberPlanning.JsConvertText(resolved.Member("path"));
            }
        }

        model ??= "inherit";
        var cls = c.Services.Config.RoleClass("sub-orchestrator", c.Registry);
        IReadOnlyList<string> allowed = cls is null ? [] : c.Services.Launch!.Registry.ClassModels(cls) ?? [];
        if (!allowed.Contains(model, StringComparer.Ordinal))
        {
            var from = source is null ? string.Empty : $" (orchestrator_model in {source})";
            throw Refuse(c, "model-unusable", $"model \"{model}\"{from} is not usable for an orchestrator (allowed: {string.Join(", ", allowed)})");
        }

        return model;
    }

    /// <summary>
    /// The initial prompt for <c>--team</c>, or null without one: a missing team is validated and handed over to be created, a team whose
    /// owner is gone to be adopted; a team whose owner is live is refused.
    /// </summary>
    internal static string? TeamPrompt(RosterContext c, string? team)
    {
        if (team is null)
        {
            return null;
        }

        c.TeamArg = team;
        c.TeamFile = team;
        c.RepoBasename = c.Services.Config.TeamPrefix(c.Cwd, team);
        var doc = c.Services.Roster.ReadTeamRecord(c.Dir, team);
        if (doc.IsNull)
        {
            TeamScopes.CheckNewTeamName(c, c.Dir);
            return $"Team {team} is new: stand it up with /wrangl:agent-team (team {team}).";
        }

        if (OwnerLive(c, doc))
        {
            throw TeamScopes.RefuseLiveTeam(c, c.Dir, doc);
        }

        return $"Team {team} exists and its owner is gone: adopt it with /wrangl:agent-team (team {team}).";
    }

    /// <summary>The team's owner is live: the live-team check create uses, and the recorded pid not reused (whoami's pid_start check).</summary>
    private static bool OwnerLive(RosterContext c, JsonValue doc)
    {
        if (!c.Services.Roster.TeamIsLive(doc, null, c.Services.Clock.UtcNow))
        {
            return false;
        }

        var orchestrator = doc.Member("orchestrator");
        var pid = orchestrator?.Member("pid") is { IsNumber: true } p ? (int)p.AsNumber() : 0;
        var recordedStart = orchestrator?.Member("pid_start") is { IsText: true } start && start.AsString().Length > 0 ? start.AsString() : null;
        return !(c.Services.Hierarchy.Core.Liveness?.PidReused(pid, recordedStart) ?? false);
    }

    /// <summary>Refuses a name a live member of any team record under the hierarchy directory holds.</summary>
    internal static void CheckNameInUse(RosterContext c, string name)
    {
        var teardown = c.Services.Teardown;
        if (teardown is null)
        {
            return;
        }

        foreach (var team in new string?[] { null }.Concat(c.Services.Roster.ListTeamNames(c.Dir)))
        {
            if (c.Services.Roster.ReadTeamRecord(c.Dir, team).Member("members") is not { IsArray: true } members)
            {
                continue;
            }

            foreach (var member in members.AsArray())
            {
                if (member.Member("name") is { IsText: true } held && held.AsString() == name
                    && teardown.MemberLiveness(c.Dir, member, team).Get("live") is { IsTruthy: true })
                {
                    throw Refuse(c, "name-in-use", $"name \"{name}\" is held by a live member of team {team ?? "(default)"} ({c.Services.Roster.TeamPath(c.Dir, team)}); pick another name");
                }
            }
        }
    }

    /// <summary>
    /// Runs the CLI as a child in the caller's terminal (stdio inherited, cwd the caller's) and returns its exit code. SIGINT is
    /// ignored while waiting: the child, in the same foreground process group, handles Ctrl-C, and this process must not die under it.
    /// </summary>
    internal static int Foreground(Plan plan)
    {
        var start = new System.Diagnostics.ProcessStartInfo(plan.CliPath) { WorkingDirectory = plan.Cwd, UseShellExecute = false };
        foreach (var arg in plan.Argv.Skip(1))
        {
            start.ArgumentList.Add(arg);
        }

        using var ignoreInterrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT, signal => signal.Cancel = true);
        System.Diagnostics.Process? started;
        try
        {
            started = System.Diagnostics.Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new RosterFailure($"{plan.CliPath} could not be started ({error.Message}); nothing was launched");
        }

        using var child = started ?? throw new RosterFailure($"{plan.CliPath} did not start");
        child.WaitForExit();
        return child.ExitCode;
    }

    /// <summary>The argv as one shell line: a word of only safe characters bare, any other single-quoted.</summary>
    internal static string ShellLine(IEnumerable<string> argv) =>
        string.Join(' ', argv.Select(a => a.Length > 0 && a.All(ch => char.IsAsciiLetterOrDigit(ch) || "-_./:=@%+,".Contains(ch)) ? a : RosterContext.ShQuote(a)));

    internal static RosterFailure Refuse(RosterContext c, string code, string message) =>
        c.Refuse(new JsonObject { ["refused"] = code, ["message"] = message }, message);
}
