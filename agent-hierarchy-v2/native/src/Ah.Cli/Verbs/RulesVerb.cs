using Ah.Config;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah rules</c>: read-only answers to protocol questions, each from the code the behaviour itself uses. <c>who-may-message</c> asks the
/// mesh's send decision; <c>next-step</c> reads the step table the directive is generated from. The user's rules of engagement are
/// <see cref="RulesStoreVerb"/>.
/// </summary>
internal static class RulesVerb
{
    internal const string Usage = "usage: ah rules who-may-message <name@team> <name@team> [--slug <slug>] [--cwd <dir>] | ah rules next-step <event>";

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
