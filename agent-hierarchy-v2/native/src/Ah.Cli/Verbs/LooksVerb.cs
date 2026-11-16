using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Ui;

namespace Ah.Cli.Verbs;

/// <summary><c>ah looks import &lt;file&gt;</c>: merges a looks object into the looks file, by the merge the page's route uses. Nothing is overwritten.</summary>
internal static class LooksVerb
{
    private const string Usage = "usage: ah looks import <file>";

    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 2 || args[0] != "import")
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        return Import(context.Env.AhHome, args[1], stdout, stderr);
    }

    internal static int Import(string ahHome, string file, TextWriter stdout, TextWriter stderr)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"ah looks: {file} could not be read ({e.GetType().Name})");
            return 1;
        }

        if (!JsonText.TryParse(bytes, out var parsed))
        {
            stderr.WriteLine($"ah looks: {file} is not JSON");
            return 1;
        }

        var reply = LooksFile.Merge(ahHome, parsed, explicitImport: true);
        var path = LooksFile.PathOf(ahHome);
        switch (reply.Outcome)
        {
            case LooksOutcome.Ok:
                stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ah looks: {reply.Added} added, {reply.Already} already there, {reply.Skipped} skipped; {path}"));
                return 0;
            case LooksOutcome.Corrupt:
                stderr.WriteLine($"ah looks: {path} does not read as looks; nothing changed, and the file is left for you to fix");
                return 1;
            default:
                stderr.WriteLine($"ah looks: {file}: {reply.Detail}");
                return 1;
        }
    }
}
