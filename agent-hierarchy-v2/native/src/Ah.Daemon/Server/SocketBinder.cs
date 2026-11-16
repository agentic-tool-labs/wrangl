using System.Net.Sockets;
using System.Text;

namespace Ah.Daemon;

/// <summary>Binds the daemon's socket itself and removes an existing path only when its daemon is dead.</summary>
internal static class SocketBinder
{
    /// <summary>The longest socket path, in bytes, either runtime binds.</summary>
    internal const int MaxSocketPath = 103;

    internal static int PathBytes(string path) => Encoding.UTF8.GetByteCount(path);

    /// <summary>
    /// A bound, not yet listening socket. The path is checked before any endpoint is constructed. A path that exists makes
    /// the bind fail unless a connection to it is refused and <paramref name="ownerDead"/> says no daemon owns it (a dead
    /// daemon's file), which is removed and bound again; a live daemon's socket is never stolen. The socket file is owner-only from the moment it exists: it is
    /// restricted before the caller can start listening, so no other user can connect under a permissive umask. On failure
    /// <paramref name="error"/> carries Node's wording.
    /// </summary>
    internal static Socket? Bind(string path, out string? error, Func<bool>? ownerDead = null)
    {
        error = null;
        if (PathBytes(path) > MaxSocketPath)
        {
            error = $"socket path {path} is longer than {MaxSocketPath} bytes; refusing to start";
            return null;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
            {
                if (OperatingSystem.IsWindows())
                {
                    _ = Directory.CreateDirectory(parent);
                }
                else if (Directory.Exists(parent))
                {
                    if ((File.GetUnixFileMode(parent) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                    {
                        socket.Dispose();
                        error = $"refusing to bind: {parent} is group- or world-writable";
                        return null;
                    }
                }
                else
                {
                    _ = Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            try
            {
                socket.Bind(new UnixDomainSocketEndPoint(path));
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse && ownerDead is not null && NobodyListens(path) && ownerDead())
            {
                // A killed daemon leaves its socket file behind. A live daemon can refuse a connect too (backlog full, mid-start), so
                // the file goes only when ahd.pid also names no live daemon.
                File.Delete(path);
                socket.Bind(new UnixDomainSocketEndPoint(path));
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            socket.Dispose();
            error = $"listen failed: {e.Message}";
            return null;
        }
        catch (SocketException e)
        {
            socket.Dispose();
            error = e.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"listen EADDRINUSE: address already in use {path}"
                : $"listen {e.SocketErrorCode}: {e.Message}";
            return null;
        }

        // A daemon fenced out leaves this socket undisposed because the path may be a replacement's by then, and the Socket finalizer
        // removes the bound path by name just as Dispose does. Only an explicit Dispose may remove it; the handle still closes when finalized.
#pragma warning disable CA1816 // the socket's own finalizer is the one suppressed, from the only place that binds it
        GC.SuppressFinalize(socket);
#pragma warning restore CA1816
        return socket;
    }

    /// <summary>True only when a connection to <paramref name="path"/> is refused: nothing is listening on that socket file.</summary>
    private static bool NobodyListens(string path)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(path));
            return false;
        }
        catch (SocketException e)
        {
            return e.SocketErrorCode == SocketError.ConnectionRefused;
        }
    }
}
