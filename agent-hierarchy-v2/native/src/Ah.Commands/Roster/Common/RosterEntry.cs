using Ah.Abstractions;

namespace Ah.Commands;

/// <summary>
/// <c>ah roster &lt;verb&gt; [args]</c>: hands the invocation to the registered <c>roster:&lt;verb&gt;</c> verb. A verb word no
/// stream has built yet is "not implemented natively" (exit 70); a word that is no roster verb gets Node's usage error.
/// </summary>
internal static class RosterEntry
{
    [Verb("roster")]
    internal static int Run(string[] rest, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(rest);
        var opts = RosterArgs.Parse(rest);
        var command = opts.Positionals.Count > 0 ? opts.Positionals[0] : null;
        if (command is not null)
        {
            var name = "roster:" + command;
            foreach (var registered in CommandsModule.Registry.Verbs)
            {
                if (registered.Name == name)
                {
                    return registered.Run(rest, stdout, stderr);
                }
            }
        }

        return RosterCli.Execute(rest, stdout, stderr, c =>
        {
            if (RosterCli.NodeVerbs.Contains(c.Command))
            {
                c.Stderr.WriteLine(RosterCli.NotNative);
                return 70;
            }

            throw RosterCli.UnknownVerb(c.Command);
        });
    }
}
