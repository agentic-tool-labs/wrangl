namespace Ah.Cli.Verbs.Msg;

/// <summary>
/// <c>ah msg</c>: the message-file CLI. Every verb is the Node CLI's, over the native Hierarchy, Roster, Config and
/// mailbox services: same arguments, same stdout and stderr bytes, same exit codes.
/// </summary>
internal static class MsgVerb
{
    internal const int NotComposed = 70;

    internal static int Run(CliContext context, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var output = new MsgOutput(stdout, stderr);
        var opts = MsgOptions.Parse(args);
        var cmd = opts.Positional.Count > 0 ? opts.Positional[0] : null;
        if (cmd is not null)
        {
            opts.Positional.RemoveAt(0);
        }

        if (opts.IsBare("help") || cmd is null)
        {
            stdout.Write(MsgUsage.Text + "\n");
            return 0;
        }

        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah: msg: the services have not been composed");
            return NotComposed;
        }

        try
        {
            var command = new MsgCommand(context, services.Value, opts, cmd, output, stdout, stderr);
            return command.Run();
        }
        catch (MsgExit exit)
        {
            return exit.Code;
        }
    }
}
