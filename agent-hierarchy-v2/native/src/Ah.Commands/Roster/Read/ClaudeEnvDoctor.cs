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
        if (team.Member(ClaudeEnvironment.Field) is not { IsText: true } leadValue || team.Member("members") is not { IsArray: true } members)
        {
            return null;
        }

        var lead = leadValue.AsString();
        var differing = new List<string>();
        foreach (var m in members.AsArray())
        {
            if (m.Member(ClaudeEnvironment.Field) is not { IsText: true } own || own.AsString() == lead || MemberPlanning.Text(m.Member("name")) is not { } name)
            {
                continue;
            }

            if (c.Services.Launch!.Teardown.MemberLiveness(c.Dir, m, c.TeamFile).Get("live") is { IsTruthy: true })
            {
                differing.Add($"{name} runs in {Spelled(own.AsString())}");
            }
        }

        return differing.Count == 0 ? null : ("ok", $"{string.Join("; ", differing)}; the lead's is {Spelled(lead)}");
    }

    private static string Spelled(string value) => value.Length == 0 ? "the default (CLAUDE_CONFIG_DIR unset)" : JsonText.Quote(value);
}
