namespace Ah.Abstractions;

/// <summary>One hook event: the hook's name, the whole event read from stdin, and the caller's environment.</summary>
public sealed record HookInvocation(
    string Name,
    ReadOnlyMemory<byte> Event,
    IReadOnlyDictionary<string, string?> Environment,
    IHookErrorLog ErrorLog);

/// <summary>What a hook gave: its stdout and exit code, and text for stderr, which Claude Code shows when the exit code is 2.</summary>
public sealed record HookOutcome(ReadOnlyMemory<byte> Stdout, int ExitCode, ReadOnlyMemory<byte> Stderr = default);

/// <summary>
/// A daemon route call. The host has applied the body cap and the drain check before a handler sees it.
/// <paramref name="PeerPid"/> is the process that connected the socket this request arrived on, read by the host from the accepted
/// socket and never from the request; null when the host could not read it.
/// </summary>
public sealed record RouteRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string> PathParameters,
    int? PeerPid = null);

/// <summary>Who is on the other end of an accepted local socket, as the operating system reports it.</summary>
public interface IPeerIdentity
{
    /// <summary>
    /// The pid of the process that connected <paramref name="accepted"/>, or null when the system cannot say (the peer is gone, or
    /// the platform has no such call). Read once when the connection is accepted: later reads follow whichever process last wrote.
    /// </summary>
    int? PeerPid(System.Net.Sockets.Socket accepted);
}

public sealed record RouteResponse(int Status, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body);
