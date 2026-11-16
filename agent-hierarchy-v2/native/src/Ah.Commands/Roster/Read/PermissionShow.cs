using Ah.Config;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// <c>permissions show &lt;member&gt;</c>: the launch-only settings file the member runs with, or the reason it runs with inline settings
/// and no permission rules. It changes nothing.
/// </summary>
internal static class PermissionShow
{
    internal static int Run(RosterContext c, string member)
    {
        if (!PermissionDiagnose.IsMemberName(member))
        {
            throw new RosterFailure("permissions show: the member name must be letters, digits, '.', '_' or '-' (no path)");
        }

        var file = NodePath.Join(c.TeamHome, "launch", member + ".settings.json");
        if (c.Services.Files.Exists(file))
        {
            c.Stdout.Write(c.Services.Files.ReadText(file));
            return 0;
        }

        c.Stdout.WriteLine($"inline (no permissions): {NoFileReason(c)}");
        return 0;
    }

    private static string NoFileReason(RosterContext c)
    {
        if (c.Services.Environment.Plugin?.Root is not { Length: > 0 })
        {
            return "generator unavailable";
        }

        string?[] configPaths = [c.MemberAllowProjectConfig,c.Services.Config.UserConfigPath()];
        var enabled = RosterModule.MemberAllowEnabled(c.Services.Files, configPaths);
        var consent = ConfigModule.EvaluateMemberLaunchConsent(c.Services.Files, c.ConsentAsksPath, enabled, RosterModule.MemberAllowOnAt(c.Services.Files, c.Services.Config.UserConfigPath()));
        return consent.Effective ? "no launch file for this member yet: it has not been launched or resumed yet" : consent.Reason!;
    }
}
