using Ah.Config;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah rules</c>: read-only answers to protocol questions, each from the code the behaviour itself uses. <c>who-may-message</c> asks the
/// mesh's send decision; <c>next-step</c> reads the step table the directive is generated from. The user's rules of engagement are
/// <see cref="RulesStoreVerb"/>.
/// </summary>
internal static class RulesVerb
{
    internal const string Usage = "usage: ah rules who-may-message <name@team> <name@team> [--slug <slug>] [--cwd <dir>] | ah rules next-step <event> | ah rules index | ah rules show <key> [--cwd <dir>]";

    internal static int Run(CliContext context, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        var verb = args.Count > 0 ? args[0] : null;
        if (RulesStoreVerb.Handles(verb))
        {
            return RulesStoreVerb.Run(context, args, stdout, stderr);
        }

        var rest = args.Skip(1).ToList();
        return verb switch
        {
            "--help" => Help(stdout),
            "next-step" => NextStep(rest, stdout, stderr),
            "who-may-message" => WhoMayMessage(context, rest, stdout, stderr),
            "index" => Index(rest, stdout, stderr),
            "show" => Show(context, rest, stdout, stderr),
            _ => Fail(stderr, Usage + "\n" + RulesStoreVerb.Usage),
        };
    }

    internal static int Help(TextWriter stdout)
    {
        stdout.WriteLine(Usage);
        stdout.WriteLine(RulesStoreVerb.Usage);
        return 0;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        return 2;
    }

    private static int NextStep(List<string> rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Count != 1)
        {
            return Fail(stderr, Usage);
        }

        if (ConfigModule.NextStepFor(rest[0]) is not { } step)
        {
            return Fail(stderr, $"ah rules: unknown event '{rest[0]}'; events: {string.Join(", ", ConfigModule.NextStepEvents())}");
        }

        stdout.WriteLine(step);
        return 0;
    }

    /// <summary>The index of the top-level Orchestrator's on-demand sections, as its directive prints it.</summary>
    private static int Index(List<string> rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Count != 0)
        {
            return Fail(stderr, Usage);
        }

        foreach (var line in ConfigModule.DirectiveIndexLines())
        {
            stdout.WriteLine(line);
        }

        return 0;
    }

    /// <summary>
    /// One on-demand section of the top-level Orchestrator's directive, built from the caller's resolved config as the directive is. An
    /// unknown key, or a section that cannot be built, prints nothing on stdout and fails with the valid keys.
    /// </summary>
    private static int Show(CliContext context, List<string> rest, TextWriter stdout, TextWriter stderr)
    {
        string? cwd = null;
        var keys = new List<string>();
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--cwd")
            {
                if (i + 1 >= rest.Count)
                {
                    return Fail(stderr, Usage);
                }

                cwd = rest[++i];
            }
            else if (rest[i].StartsWith("--", StringComparison.Ordinal))
            {
                return Fail(stderr, $"ah rules: show does not accept {rest[i]}");
            }
            else
            {
                keys.Add(rest[i]);
            }
        }

        var valid = string.Join(", ", ConfigModule.DirectiveIndex().Select(e => e.Key));
        if (keys.Count != 1 || !ConfigModule.DirectiveIndex().Any(e => e.Key == keys[0]))
        {
            return Fail(stderr, $"ah rules: unknown section '{(keys.Count == 1 ? keys[0] : string.Join(' ', keys))}'; sections: {valid}");
        }

        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah rules: the services have not been composed");
            return 70;
        }

        var graph = services.Value;
        var dir = Path.GetFullPath(cwd ?? Directory.GetCurrentDirectory());
        string? section;
        try
        {
            var sessionId = context.Env.GetVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } id ? id : null;
            var resolved = graph.Config.ResolveConfig(dir, new ResolveOptions(SessionId: sessionId));
            var identity = keys[0] == "identity" && context.Env.Plugin?.Root is { Length: > 0 } root
                ? ConfigModule.AgentBody(graph.Io.Files.ReadText(Path.Combine(root, "agents", "orchestrator.md")))
                : null;
            var directive = ConfigModule.CreateDirectiveText(graph.Config, ConfigModule.CreateRoleContracts(graph.Config, graph.Io.Files, context.Env), () => graph.Roster);
            section = directive.DirectiveSection(keys[0], resolved, graph.Config.HierarchyDir(dir), identity);
        }
        catch (IOException e)
        {
            stderr.WriteLine($"ah rules: {e.Message}");
            return 1;
        }

        if (section is null)
        {
            return Fail(stderr, $"ah rules: section '{keys[0]}' could not be built here; sections: {valid}");
        }

        stdout.WriteLine(section);
        return 0;
    }

    private static int WhoMayMessage(CliContext context, List<string> rest, TextWriter stdout, TextWriter stderr)
    {
        string? slug = null;
        string? cwd = null;
        var principals = new List<string>();
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i] is "--slug" or "--cwd")
            {
                if (i + 1 >= rest.Count)
                {
                    return Fail(stderr, Usage);
                }

                if (rest[i] == "--slug")
                {
                    slug = rest[++i];
                }
                else
                {
                    cwd = rest[++i];
                }
            }
            else if (rest[i].StartsWith("--", StringComparison.Ordinal))
            {
                return Fail(stderr, $"ah rules: who-may-message does not accept {rest[i]}");
            }
            else
            {
                principals.Add(rest[i]);
            }
        }

        if (principals.Count != 2 || principals.Any(p => p != "user" && !p.Contains('@', StringComparison.Ordinal)))
        {
            return Fail(stderr, Usage);
        }

        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah rules: the services have not been composed");
            return 70;
        }

        var graph = services.Value;
        Ah.Mesh.MeshVerdict verdict;
        try
        {
            verdict = graph.Sender.Explain(graph.Callers.RequestContext(Path.GetFullPath(cwd ?? Directory.GetCurrentDirectory())), principals[0], principals[1], slug);
        }
        catch (IOException e)
        {
            stderr.WriteLine($"ah rules: {e.Message}");
            return 1;
        }

        stdout.WriteLine(!verdict.Allow ? "no" : verdict.Warn ? $"yes (warn: {verdict.Would})" : "yes");
        stdout.WriteLine($"rule: {verdict.Rule}");
        if (!verdict.Allow && verdict.Code is not null)
        {
            stdout.WriteLine($"code: {verdict.Code}");
        }

        if (!verdict.Allow && verdict.Message is not null)
        {
            stdout.WriteLine($"message: {verdict.Message}");
        }

        return 0;
    }
}
