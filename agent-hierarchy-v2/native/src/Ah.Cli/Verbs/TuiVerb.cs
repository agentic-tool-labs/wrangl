using Ah.Core.Json;
using Ah.DaemonClient;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ahd tui [--team NAME] [--once]</c>: the terminal view is a separate binary, <c>ahd-tui</c>, installed beside <c>ahd</c>. This verb
/// checks that binary's contract sidecar against its own and replaces the process with it, passing the argument tail unchanged; the
/// client parses its own flags and owns every exit code. <c>--help</c> prints the control CLI's usage block.
/// </summary>
internal static class TuiVerb
{
    /// <summary>The exit code of a verb whose binary is not there, or does not match.</summary>
    internal const int NotInstalled = 70;

    internal static int Run(CliContext context, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        if (rest.Contains("--help", StringComparer.Ordinal) && DaemonClientModule.Lifecycle is { } lifecycle)
        {
            return lifecycle.RunAsync("status", ["--help"], stdout, stderr).GetAwaiter().GetResult();
        }

        var self = CliContext.RealPath(context.ProcessPath);
        var sibling = Path.Combine(Path.GetDirectoryName(self) ?? ".", "ahd-tui");
        if (!File.Exists(sibling))
        {
            stderr.WriteLine($"ahd: tui needs ahd-tui beside {self}, and it is not there; reinstall with the plugin's scripts/install.sh");
            return NotInstalled;
        }

        var theirs = ReadContract(sibling + ".contract");
        if (theirs is null)
        {
            stderr.WriteLine($"ahd: {sibling} has no readable contract sidecar ({sibling}.contract); reinstall with the plugin's scripts/install.sh");
            return NotInstalled;
        }

        if (BuildInfo.Contract is { } mine && theirs != mine)
        {
            stderr.WriteLine($"ahd: ahd-tui is contract {theirs} and this ahd is contract {mine}; reinstall with the plugin's scripts/install.sh");
            return NotInstalled;
        }

        var argv = new List<string>(rest.Length + 1) { sibling };
        argv.AddRange(rest);
        context.Exec.Exec(argv, null);
        return NotInstalled;
    }

    /// <summary>The <c>contract</c> of a sidecar <c>{version, contract}</c> as text, or null when there is no such readable integer.</summary>
    private static string? ReadContract(string path)
    {
        try
        {
            var doc = JsonText.Parse(File.ReadAllText(path));
            return doc.Member("contract") is { IsNumber: true } n && double.IsInteger(n.AsNumber())
                ? JsConvert.NumberToString(n.AsNumber())
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }
}
