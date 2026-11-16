using System.Collections;
using Ah.Abstractions;

namespace Ah.Cli.Composition;

/// <summary>
/// Runs every child the daemon starts without the identity of the session that happened to start the daemon: the daemon may have been
/// started from a member's or an Orchestrator's shell, and a child that inherited <c>AH_MEMBER_NAME</c>, <c>WRANGL_TEAM_FILE</c>,
/// <c>CLAUDE_PID</c> or any other <c>AH_*</c> or <c>CLAUDE_*</c> name would act as that member. Every such name is dropped except the
/// allowlist. A request that states its own environment keeps it, less those names.
/// </summary>
internal sealed class IdentityScrubbingRunner(IProcessRunner inner, Func<IDictionary>? currentEnvironment = null) : IProcessRunner
{
    /// <summary>The <c>AH_*</c> and <c>CLAUDE_*</c> names a child keeps: where ah's state lives is the same for every process of the user.</summary>
    internal static readonly string[] Allowed = ["AH_HOME"];

    internal static bool IsIdentity(string name) =>
        (name.StartsWith("AH_", StringComparison.Ordinal) || name.StartsWith("WRANGL_", StringComparison.Ordinal) || name.StartsWith("CLAUDE_", StringComparison.Ordinal)) && !Allowed.Contains(name);

    public ProcessResult Run(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return inner.Run(request with { Environment = Scrubbed(request.Environment) });
    }

    private Dictionary<string, string?> Scrubbed(IReadOnlyDictionary<string, string?>? stated)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (stated is not null)
        {
            foreach (var (key, value) in stated)
            {
                map[key] = value;
            }
        }
        else
        {
            foreach (DictionaryEntry entry in (currentEnvironment ?? Environment.GetEnvironmentVariables)())
            {
                map[(string)entry.Key] = entry.Value as string;
            }
        }

        foreach (var name in map.Keys.Where(IsIdentity).ToList())
        {
            _ = map.Remove(name);
        }

        return map;
    }
}
