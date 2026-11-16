namespace Ah.Cli.Verbs.Qa;

/// <summary><c>ah qa</c>: submit, claim, end and look at jobs of the daemon's QA queue.</summary>
internal static class QaVerb
{
    internal const int NotComposed = 70;

    internal static int Run(CliContext context, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var opts = QaOptions.Parse(args);
        var verb = opts.Positional.Count > 0 ? opts.Positional[0] : null;
        if (verb is not null)
        {
            opts.Positional.RemoveAt(0);
        }

        if (opts.Has("help") || verb is null)
        {
            stdout.Write(QaUsage.Text + "\n");
            return 0;
        }

        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah: qa: the services have not been composed");
            return NotComposed;
        }

        try
        {
            return new QaCommand(services.Value, opts, verb, new QaOutput(stdout, stderr)).Run();
        }
        catch (QaExit exit)
        {
            return exit.Code;
        }
    }
}
