namespace Ah.Abstractions;

/// <summary>A caller context that failed a check: <paramref name="Field"/> names the offending field.</summary>
public sealed record RequestContextProblem(string Message, string? Field);

/// <summary>
/// The check a caller context goes through before any request acts on its directories: <c>cwd</c> and <c>hierarchy_dir</c> must be
/// real absolute directories, <c>hierarchy_dir</c> owned by the daemon's user, and <c>main_hierarchy_dir</c> and
/// <c>messaging_socket_dir</c> (when present) the same. Nothing here comes from the process's own environment.
/// </summary>
public interface IRequestContextChecker
{
    /// <summary>The first failed check of <paramref name="context"/>, or null. An empty optional directory is absent and passes.</summary>
    RequestContextProblem? Check(RequestContext context);
}
