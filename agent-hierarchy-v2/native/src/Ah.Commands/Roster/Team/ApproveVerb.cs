using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>roster approve &lt;member&gt; &lt;rule&gt;</c>: the Orchestrator lets one held call of a live member through a soft gate. It appends one
/// <c>soft-gate</c> <c>approve</c> row; the gate consumes it on the member's next call that rule would bounce.
/// </summary>
internal static class ApproveVerb
{
    [Verb("roster:approve")]
    internal static int Approve(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        if (c.Opts.Positionals is not [var member, var rule])
        {
            throw new RosterFailure("approve: usage: approve <member> <rule> [--cwd <path>]");
        }

        // ah2-310 A2': an approval no gate consults would print "approved" and change nothing.
        if (!Ah.Config.ConfigModule.ApprovableRules.Contains(rule))
        {
            throw new RosterFailure($"approve: no gate consults approvals for \"{rule}\"; rules that can be approved: {string.Join(", ", Ah.Config.ConfigModule.ApprovableRules)}", exitCode: 1);
        }

        if (!c.Services.Roster.ResolveMemberTeam(c.Dir, member).Found)
        {
            throw new RosterFailure($"approve: no member \"{member}\" in a team of this hierarchy");
        }

        c.Services.Hierarchy.Roster.AppendGateRouted(c.Dir, new JsonObject { ["type"] = "soft-gate", ["rule"] = rule, ["outcome"] = "approve", ["member"] = member });
        c.Out(new JsonObject { ["status"] = "approved", ["member"] = member, ["rule"] = rule, ["covers"] = Ah.Config.ConfigModule.ApprovalCovers });
        return 0;
    }
}
