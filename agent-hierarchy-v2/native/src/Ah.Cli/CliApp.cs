using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Cli.Verbs.AhdCtl;
using Ah.Cli.Verbs.Msg;

namespace Ah.Cli;

internal static class CliApp
{
    internal static int Run(string[] args, CliContext context, TextWriter stdout, TextWriter stderr) =>
        Run(args, context, stdout, stderr, Modules.Registries);

    /// <summary>The namespace comes from the basename of argv[0]: <c>ah</c> is the ah namespace, anything else is ahd.</summary>
    internal static int Run(
        string[] args, CliContext context, TextWriter stdout, TextWriter stderr, IReadOnlyList<ModuleRegistry> registries)
    {
        if (Refused(args, context, stderr))
        {
            return 2;
        }

        var at = args.Length > 1 && args[0] == "ah" ? 1 : 0;
        if (args.Length == at + 1 && args[at] == "--version")
        {
            // The mascot is for a person at a terminal; a script reads the version line, which stays the last line and unchanged.
            if (context.StdoutIsTerminal && context.Env.GetVariable("NO_COLOR") is not { Length: > 0 })
            {
                stdout.Write(Mascot());
            }

            // The bin/ah shim runs the one binary as `<binary> ah …`, so a leading `ah` is the command the user typed.
            stdout.WriteLine($"{(at == 1 ? "ah" : Path.GetFileName(context.Argv0))} {BuildInfo.Version} (contract {BuildInfo.Contract})");
            return 0;
        }

        try
        {
            return Path.GetFileName(context.Argv0) == "ah"
                ? RunAh(args, context, stdout, stderr, registries)
                : RunAhd(args, context, stdout, stderr, registries);
        }
        catch (Ah.Core.Json.StoreUnavailableException error)
        {
            stderr.WriteLine($"ah: the state store cannot be read ({error.Message}){(error.Advice is { } advice ? "; " + advice : string.Empty)}");
            return 1;
        }
    }

    /// <summary>The Wrangl mascot in 24-bit colour, embedded from <c>assets/mascot.ansi</c> (made by <c>scripts/gen-mascot.py</c>).</summary>
    internal static string Mascot()
    {
        using var stream = typeof(CliApp).Assembly.GetManifestResourceStream("mascot.ansi");
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The verbs that must run whatever the state home is: they report where things are, are how a user recovers, or (a hook)
    /// answer for themselves, a gate with a deny. Every other verb, the daemon's <c>serve</c> included, stops with exit 2 when the
    /// state home is neither the default nor trusted.
    /// </summary>
    private static readonly string[] HomeFreeVerbs = ["where", "check", "home", "hook", "--help", "-h", "help", "--version"];

    private static bool Refused(string[] args, CliContext context, TextWriter stderr)
    {
        var at = args.Length > 1 && args[0] == "ah" ? 1 : 0;
        var verb = args.Length > at ? args[at] : string.Empty;
        // `login on|off|status` only touch the login item (which serves the default home alone); `login run` starts a daemon.
        var loginReadOnly = verb == "login" && !(args.Length > at + 1 && args[at + 1] == "run");
        if (verb.Length == 0 || HomeFreeVerbs.Contains(verb) || loginReadOnly || OperatingSystem.IsWindows())
        {
            return false;
        }

        var files = Ah.Platform.PlatformModule.CreateIo(null).Files;
        var verdict = AhHomeGate.Check(context.Env, files, context.Uid);
        if (verdict.Accepted)
        {
            return false;
        }

        stderr.WriteLine(AhHomeGate.ForStandardError(verdict, context.Env.GetVariable("AH_HOME"), !Console.IsErrorRedirected));
        return true;
    }

    /// <summary>Registered verbs that belong to the ah namespace: under the ahd namespace they are not verbs.</summary>
    private static readonly string[] AhOnlyVerbs = ["roster"];

    private static int NoHelp(TextWriter stderr)
    {
        stderr.WriteLine("ahd: the control CLI's usage text is not available in this build");
        return 70;
    }

    private static int RunAhd(
        string[] args, CliContext context, TextWriter stdout, TextWriter stderr, IReadOnlyList<ModuleRegistry> registries)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("usage: ahd <verb> [args]  (try: ahd --help)");
            return 2;
        }

        var verb = args[0];
        var rest = args[1..];
        switch (verb)
        {
            case "--help":
            case "-h":
            case "help":
                return Ah.DaemonClient.DaemonClientModule.Lifecycle is { } lifecycle
                    ? lifecycle.RunAsync("status", ["--help"], stdout, stderr).GetAwaiter().GetResult()
                    : NoHelp(stderr);
            case "where":
                return WhereVerb.Run(context, rest, stdout, stderr);
            case "check":
                return CheckVerb.Run(context, stdout, stderr);
            case "login":
                return LoginVerb.Run(context, rest, stdout, stderr, () => RunRegistered("start", [], stdout, stderr, registries));
            case "hook":
                return HookVerb.Run(context, rest, stdout, stderr);
            case "contract":
            case "inspect":
            case "ui":
                return AhdCtlVerb.Run(context, verb, rest, stdout, stderr);
            case "tui":
                return TuiVerb.Run(context, rest, stdout, stderr);
            case "ah":
                return RunAh(rest, context, stdout, stderr, registries);
        }

        foreach (var registry in registries)
        {
            foreach (var registered in registry.Verbs)
            {
                if (registered.Name == verb && !AhOnlyVerbs.Contains(verb))
                {
                    return registered.Run(rest, stdout, stderr);
                }
            }
        }

        stderr.WriteLine($"ahd: unknown command '{verb}'");
        return 2;
    }

    /// <summary>Runs the registered verb <paramref name="verb"/>, or says it is not implemented natively and exits 70.</summary>
    private static int RunRegistered(
        string verb, string[] args, TextWriter stdout, TextWriter stderr, IReadOnlyList<ModuleRegistry> registries)
    {
        foreach (var registry in registries)
        {
            foreach (var registered in registry.Verbs)
            {
                if (registered.Name == verb)
                {
                    return registered.Run(args, stdout, stderr);
                }
            }
        }

        stderr.WriteLine($"ahd: {verb} is not implemented natively");
        return 70;
    }

    private static int RunRoster(string[] args, TextWriter stdout, TextWriter stderr, IReadOnlyList<ModuleRegistry> registries)
    {
        foreach (var registry in registries)
        {
            foreach (var registered in registry.Verbs)
            {
                if (registered.Name == "roster")
                {
                    return registered.Run(args, stdout, stderr);
                }
            }
        }

        stderr.WriteLine("ah: roster is not implemented natively");
        return 70;
    }

    private static int RunAh(
        string[] args, CliContext context, TextWriter stdout, TextWriter stderr, IReadOnlyList<ModuleRegistry> registries)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("usage: ah <roster|progress|ledger|msg|qa|looks|alarm|proc|admin|view|allow|rules|test|usage|usage-report|herdr|backlog|work|adr|db|issue-intake|hook> [args]");
            return 2;
        }

        // `ah --help`, `ah -h` and `ah help [group]` print the no-argument usage above, on stdout, and succeed.
        if (args[0] is "--help" or "-h" or "help")
        {
            RunAh([], context, stdout, stdout, registries);
            return 0;
        }

        // a hook cannot wait, `ah db` works on a database as it finds it, and `ah proc` stops things whatever the daemon is doing
        // `ah ahd heal` is the one ahd verb reachable through ah (crash-resume §14.11): it judges a daemon that may never answer
        var heal = args is ["ahd", "heal", ..];
        // `ah where` contacts no daemon, as `ahd where` (ah2-122 D2)
        // `ah view events` is the herdr mod's poll: a read never replaces or drains the daemon (ah2-123 D1)
        var viewEvents = args is ["view", "events", ..];
        if (!heal && !viewEvents && args[0] is not ("hook" or "db" or "proc" or "admin" or "gate" or "push-guard" or "where") && context.Services is { } upgradeServices)
        {
            SchemaUpgradeWait.Run(context.Env, () => upgradeServices.Value.Daemon, stderr);
        }

        switch (args[0])
        {
            case "ahd" when heal:
                return RunAhd(args[1..], context, stdout, stderr, registries);
            case "where":
                return WhereVerb.Run(context, args[1..], stdout, stderr);
            case "hook":
                return HookVerb.Run(context, args[1..], stdout, stderr);
            case "gate":
                stdout.WriteLine("ah gate: this command was removed (the pipeline asks once per run whether an Ultra-Advisor may be used; nothing to do)");
                return 0;
            case "push-guard":
                stdout.WriteLine("ah push-guard: this command was removed (ah has no push guard; nothing to do)");
                return 0;
            case "msg":
                return MsgVerb.Run(context, args[1..], stdout, stderr);
            case "qa":
                return Ah.Cli.Verbs.Qa.QaVerb.Run(context, args[1..], stdout, stderr);
            case "looks":
                return LooksVerb.Run(context, args[1..], stdout, stderr);
            case "alarm":
                return AlarmVerb.Run(context, args[1..], stdout, stderr);
            case "proc":
                return ProcVerb.Run(context, args[1..], stdout, stderr);
            case "admin":
                return AdminVerb.Run(context, args[1..], stdout, stderr);
            case "view":
                if (context.Services is not { } viewServices)
                {
                    stderr.WriteLine("ah view: the services have not been composed");
                    return AhdCtlVerb.NotComposed;
                }

                return ViewVerb.Run(context, viewServices.Value.Daemon, args[1..], stdout, stderr);
            case "allow":
                if (args.Length >= 2 && args[1] is "on" or "off" or "list")
                {
                    return RunRoster(["member-allow", .. args[1..]], stdout, stderr, registries);
                }

                stderr.WriteLine("usage: ah allow on | off | list [--json]");
                return 2;
            case "rules":
                return RulesVerb.Run(context, args[1..], stdout, stderr);
            case "test":
                return TestVerb.Run(context, args[1..], stdout, stderr);
            case "usage":
                return UsageVerb.Run(context, args[1..], stdout, stderr);
            case "herdr":
                return HerdrVerb.Run(context, args[1..], stdout, stderr);
            case "usage-report":
                return UsageReportVerb.Run(context, args[1..], stdout, stderr);
            case "issue-intake":
                return IssueIntakeVerb.Run(context, args[1..], stdout, stderr);
            case "db":
                return Ah.Cli.Verbs.Db.DbVerb.Run(context, args[1..], stdout, stderr);
            case "backlog":
                return BacklogVerb.Run(context, args[1..], stdout, stderr);
            case "work":
                return WorkVerb.Run(context, args[1..], stdout, stderr);
            case "adr":
                return AdrVerb.Run(context, args[1..], stdout, stderr);
            case "roster":
                return RunRoster(args[1..], stdout, stderr, registries);
            case "progress":
                return RunRoster(["progress", .. args[1..]], stdout, stderr, registries);
            case "ledger":
                return RunRoster(["ledger", .. args[1..]], stdout, stderr, registries);
            default:
                stderr.WriteLine($"ah: unknown command '{args[0]}'");
                return 2;
        }
    }
}
