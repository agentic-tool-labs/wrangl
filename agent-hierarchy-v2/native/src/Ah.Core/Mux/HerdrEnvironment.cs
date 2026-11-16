namespace Ah.Core.Mux;

/// <summary>
/// The environment a herdr (or tmux) child process is started with.
/// </summary>
/// <remarks>
/// A process started inside a herdr pane inherits <c>HERDR_SOCKET_PATH</c>, <c>HERDR_PANE_ID</c> and <c>HERDR_ENV</c>, and a
/// bare <c>herdr</c> call then addresses that pane's own server and pane. A call that must address one named instance
/// therefore sets the socket explicitly and drops the inherited pane identity. An environment passed to a process request
/// replaces the inherited one outright, so both methods return a complete map built from the parent's.
/// </remarks>
public static class HerdrEnvironment
{
    private const string SocketPath = "HERDR_SOCKET_PATH";

    private static readonly string[] InstanceVariables =
    [
        SocketPath,
        "HERDR_CLIENT_SOCKET_PATH",
        "HERDR_PANE_ID",
        "HERDR_ENV",
        "HERDR_SESSION",
        "HERDR_WORKSPACE_ID",
        "HERDR_TAB_ID",
    ];

    /// <summary>
    /// The parent's environment for a call against the herdr server listening at <paramref name="socketPath"/>:
    /// <c>HERDR_SOCKET_PATH</c> set to it, and the inherited identity variables removed (<c>HERDR_CLIENT_SOCKET_PATH</c>,
    /// <c>HERDR_PANE_ID</c>, <c>HERDR_ENV</c>, <c>HERDR_SESSION</c>, <c>HERDR_WORKSPACE_ID</c>, <c>HERDR_TAB_ID</c>). Every other
    /// variable, including <c>HERDR_HOME</c>, <c>HERDR_BIN_PATH</c> and <c>PATH</c>, is kept.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="socketPath"/> is not an absolute path free of control characters.</exception>
    public static IReadOnlyDictionary<string, string?> ForInstance(IReadOnlyDictionary<string, string?> parent, string socketPath)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!IsCleanAbsolutePath(socketPath))
        {
            throw new ArgumentException("the socket path must be an absolute path without control characters", nameof(socketPath));
        }

        var map = Copy(parent);
        map[SocketPath] = socketPath;
        return map;
    }

    /// <summary>
    /// The parent's environment with the instance-selecting and identity variables removed and every other variable kept. For
    /// <c>herdr session list</c>, which discovers instances itself, and for calls to other tools that must not see this
    /// process's herdr identity.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> WithoutInstance(IReadOnlyDictionary<string, string?> parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return Copy(parent);
    }

    private static Dictionary<string, string?> Copy(IReadOnlyDictionary<string, string?> parent)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in parent)
        {
            if (value is not null && Array.IndexOf(InstanceVariables, name) < 0)
            {
                map[name] = value;
            }
        }

        return map;
    }

    private static bool IsCleanAbsolutePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Length > 1024)
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c < 0x20 || c == 0x7f)
            {
                return false;
            }
        }

        return true;
    }
}
