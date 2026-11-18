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

    /// <summary>The on-demand sections of the top-level Orchestrator's directive, in index order.</summary>
    public static IReadOnlyList<DirectiveIndexEntry> DirectiveIndex() => DirectiveText.Index;

    /// <summary>The index lines as the directive prints them, one per section.</summary>
    public static IReadOnlyList<string> DirectiveIndexLines() => [.. DirectiveText.Index.Select(DirectiveText.IndexLine)];

    /// <summary>
    /// The soft-gate rule of the team-commands note (ah2-216): its <c>nudge</c> row is the note given, its <c>read</c> row is
    /// <c>ah rules show peer-roster</c> run, each for one session since its latest reset.
    /// </summary>
    public const string TeamCommandsRule = "team-commands";

    /// <summary>The resolved <c>dispatch</c> setting; absent or unknown is <c>peers</c>.</summary>
    public static string DispatchMode(Ah.Core.Json.JsonObject resolved) => DirectiveText.DispatchMode(resolved);

    /// <summary>The body of an agent file without its frontmatter, trimmed; null when nothing is left.</summary>
    public static string? AgentBody(string fileText) => DirectiveText.AgentBody(fileText);

    /// <summary>
    /// The Orchestrator identity (the body of <c>agents/orchestrator.md</c>) split into what SessionStart keeps resident and what
    /// <c>ah rules show identity</c> prints; bullets that repeat a resident directive line are in neither.
    /// </summary>
    public static (string Resident, string Deferred) SplitOrchestratorIdentity(string body) => DirectiveText.SplitIdentity(body);
}
