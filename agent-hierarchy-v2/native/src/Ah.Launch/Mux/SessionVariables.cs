namespace Ah.Launch;

/// <summary>
/// The variables that name one Claude Code session or ah member: never handed on to another session, a member, or a terminal
/// server. <c>CLAUDE_CONFIG_DIR</c> is the user's own configuration and is kept.
/// </summary>
internal static class SessionVariables
{
    public static bool IsSession(string name) =>
        name is "CLAUDECODE" or "CLAUDE_PID" or "AH_MEMBER_NAME" or "WRANGL_TEAM_FILE" or "WRANGL_EXPECTED_ROOT"
        || name.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal);
}
