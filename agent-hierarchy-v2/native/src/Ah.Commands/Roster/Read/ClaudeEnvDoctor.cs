using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// The <c>claude-env</c> doctor row (ah2-570 E5): a live member whose recorded Claude environment differs from the one its team record
/// holds for the lead. Information only: it names both and never warns or refuses.
/// </summary>
internal static class ClaudeEnvDoctor
{
    internal static (string Status, string Detail)? Row(RosterContext c)
    {
        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (team.Member("members") is not { IsArray: true } members)
        {
            return null;
        }

        var differing = new List<string>();
        foreach (var m in members.AsArray())
        {
            if (MemberPlanning.Text(m.Member("name")) is not { } name)
            {
                continue;
            }

            var differs = ClaudeEnvironment.Variables
                .Where(v => team.Member(v.Field) is { IsText: true } lead && m.Member(v.Field) is { IsText: true } own && own.AsString() != lead.AsString())
                .Select(v => $"{name} runs with {Spelled(v.Variable, m.Member(v.Field)!.Value.AsString())}, the lead's is {Spelled(v.Variable, team.Member(v.Field)!.Value.AsString())}")
                .ToList();
            if (differs.Count > 0 && c.Services.Launch!.Teardown.MemberLiveness(c.Dir, m, c.TeamFile).Get("live") is { IsTruthy: true })
            {
                differing.AddRange(differs);
            }
        }

        return differing.Count == 0 ? null : ("ok", string.Join("; ", differing));
    }

    private static string Spelled(string variable, string value) => value.Length == 0 ? $"{variable} unset" : $"{variable}={JsonText.Quote(value)}";
}
