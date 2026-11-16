namespace Ah.Commands;

/// <summary>
/// A roster verb's usage error or refusal. The message is written to stderr as <c>ah roster: &lt;message&gt;</c> and the
/// exit code is 2, as the Node CLI's <c>fail</c> does, unless the failure names another.
/// </summary>
internal sealed class RosterFailure(string message, string? refused = null, int exitCode = 2) : Exception(message)
{
    /// <summary>The <c>refused</c> code of a refusal that carries one (<see cref="RosterContext.Refuse"/>), else null.</summary>
    internal string? Refused { get; } = refused;

    internal int ExitCode { get; } = exitCode;
}
