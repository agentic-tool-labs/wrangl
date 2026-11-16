using Ah.Commands;
using Ah.Config;

namespace Ah.Cli.Verbs;

/// <summary><c>ahd ah issue-intake --cwd &lt;abs&gt; --out-dir &lt;abs&gt; (--issues &lt;ref,...&gt; | --labelled)</c>: the issue intake gate.</summary>
internal static class IssueIntakeVerb
{
    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah: issue-intake: the services have not been composed");
            return 70;
        }

        var g = services.Value;
        return CommandsModule.RunIssueIntake(
            new IssueIntakeServices(context.Runner, g.Io.Writer, g.Io.Clock, ConfigModule.CreateConventions(context.Runner, g.Io.Files, () => context.Env.HomeDirectory), g.Io.HookErrors),
            args,
            stdout);
    }
}
