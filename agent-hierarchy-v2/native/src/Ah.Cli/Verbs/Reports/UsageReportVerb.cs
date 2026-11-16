using Ah.Commands;
using Ah.Hierarchy;

namespace Ah.Cli.Verbs;

/// <summary><c>ahd ah usage-report [session|day|week|month|all | --team &lt;name&gt; | --fleet] [--json]</c>: the zero-token usage report.</summary>
internal static class UsageReportVerb
{
    /// <summary>The record of a team the roster lists; anything else, such as a name with a path in it, is no team.</summary>
    internal static Ah.Core.Json.JsonValue TeamRecord(Composition.ServiceGraph g, string dir, string team) =>
        g.Roster.ListTeamNames(dir).Contains(team) ? g.Roster.ReadTeamRecord(dir, team) : Ah.Core.Json.JsonValue.Null;

    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah: usage-report: the services have not been composed");
            return 70;
        }

        var g = services.Value;
        var cwd = Directory.GetCurrentDirectory();
        var dir = BacklogVerb.DirOf(g, cwd);
        return CommandsModule.RunUsageReport(
            new UsageReportServices(
                g.Io.Files,
                g.Io.Writer,
                g.Io.Clock,
                g.Config,
                HierarchyModule.CreateUsage(g.Io.Files, g.Environment, Ah.StateDb.StateDbModule.CreateUsageRows(g.Environment.AhHome)),
                cwd,
                Environment.ProcessId)
            {
                TeamRecord = team => TeamRecord(g, dir, team),
                ProjectsDir = Path.Combine(g.Environment.HomeDirectory, ".claude", "projects"),
                ProgressDir = Path.Combine(HierarchyModule.ProgressPoolOf(g.Hierarchy.Messages.PoolContext(dir).MainHierarchyDirectory, dir), "progress"),
                SessionHistory = () => Ah.StateDb.StateDbModule.OpenSessionAuthority(g.Environment.AhHome).ListHistory(dir),
                TeamNames = () => g.Roster.ListTeamNames(dir),
            },
            args,
            stdout,
            stderr);
    }
}
