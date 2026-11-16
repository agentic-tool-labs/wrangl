using Ah.Abstractions;

namespace Ah.Config;

public static partial class ConfigModule
{
    /// <summary>The directive, nudge, role-session notice, routing/contract blocks and status report text.</summary>
    public static IDirectiveText CreateDirectiveText(IConfigCore config, IRoleContracts contracts, Func<ITeamFiles> teamFiles)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(teamFiles);
        return new DirectiveText(config, contracts, teamFiles);
    }
}
