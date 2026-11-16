using Ah.Core.Json;

namespace Ah.Commands;

internal static class TeamShowVerb
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "cwd", "team", "json" };

    /// <summary><c>ah roster team show [--team T] [--json]</c>: prints the team document, indented, or on one line with <c>--json</c>; never writes.</summary>
    internal static int Run(RosterContext c)
    {
        if (c.Opts.Positionals is not ["show"])
        {
            throw new RosterFailure("usage: ah roster team show [--team <name>] [--json] [--cwd <path>]");
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"team show: unrecognized flag --{key} (use --team, --json or --cwd)");
            }
        }

        var roster = c.Services.Roster;
        var record = roster.ReadTeamFileAt(roster.TeamPath(c.Dir, c.TeamArg));
        if (record.IsNull)
        {
            throw new RosterFailure($"team show: no team {c.TeamArg ?? "(default)"} in {c.Dir}");
        }

        c.Stdout.WriteLine(JsonText.Stringify(record, c.Opts.IsTrue("json") ? 0 : 2));
        return 0;
    }
}
