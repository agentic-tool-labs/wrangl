using Ah.Cli.Composition;

namespace Ah.Cli;

internal static class Program
{
    /// <summary>True only in the real ah/ahd binary: set first thing in <see cref="Main"/>, which no test host runs.</summary>
    internal static bool IsRealBinary => s_realBinary;

    private static bool s_realBinary;

    private static int Main(string[] args)
    {
        s_realBinary = true;
        Ah.Core.Time.PhaseTrace.SinceProcessStart("process-start-to-main");
        using var keepCases = Ah.Core.Text.PathGuard.KeepCases();

        // A shim hands over the plugin root it found from its own location as a leading option; a root that is not this plugin's
        // directory is refused before anything runs.
        var (requestedRoot, remaining) = Delegation.PluginRootOption.Split(args);
        if (requestedRoot is not null)
        {
            if (Delegation.PluginRootOption.Problem(requestedRoot, BuildInfo.PluginName) is { } problem)
            {
                // Exit 2 from a hook is a block (Stop) or an erased prompt (UserPromptSubmit): a hook answers by its own rule.
                if (Verbs.HookVerb.IsHookInvocation(remaining))
                {
                    return Verbs.HookVerb.Refuse(
                        Verbs.HookVerb.AfterHook(remaining),
                        $"ah: --plugin-root {problem}, so this hook did not run. Run the plugin's scripts/install.sh --apply, or ask the user to.",
                        Console.Out,
                        Console.Error);
                }

                Console.Error.WriteLine($"ahd: --plugin-root {problem}; nothing was run");
                return 2;
            }

            args = remaining;
        }

        // A hook runs before anything else is built: the host itself is constructed under the hook's own handler, so
        // nothing that fails there can leave a gate without an answer.
        if (Verbs.HookVerb.IsHookInvocation(args))
        {
            return Verbs.HookVerb.Run(null, Verbs.HookVerb.AfterHook(args), Console.Out, Console.Error);
        }

        return StopOnDeletedCwd(() => CliApp.Run(args, CliHost.Create(), Console.Out, Console.Error), Directory.GetCurrentDirectory, Console.Error);
    }

    /// <summary>
    /// Many verbs read the working directory, the service graph first among them, so the one catch is here: a read that fails because
    /// the directory was deleted under the shell is one line and exit 1. Any other missing file stays the crash it was.
    /// </summary>
    internal static int StopOnDeletedCwd(Func<int> run, Func<string> readCwd, TextWriter stderr)
    {
        try
        {
            return run();
        }
        catch (FileNotFoundException) when (CwdIsGone(readCwd))
        {
            stderr.WriteLine("ahd: current directory no longer exists; cd elsewhere and retry");
            return 1;
        }
    }

    private static bool CwdIsGone(Func<string> readCwd)
    {
        try
        {
            readCwd();
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
    }
}
