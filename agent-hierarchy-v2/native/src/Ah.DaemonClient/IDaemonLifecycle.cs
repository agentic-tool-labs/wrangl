namespace Ah.DaemonClient;

/// <summary>The <c>ahd</c> lifecycle verbs. The verb files of <c>Ah.Commands</c> call it.</summary>
public interface IDaemonLifecycle
{
    /// <summary>
    /// Runs <paramref name="verb"/> (<c>start</c>, <c>stop</c>, <c>restart</c>, <c>drain</c>, <c>status</c>, <c>health</c>,
    /// <c>version</c>, <c>logs</c> or <c>heal</c>) with the arguments after the verb name. Returns the exit code.
    /// </summary>
    Task<int> RunAsync(string verb, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>ah msg daemon start</c> or <c>stop</c>, in that CLI's text: <c>start</c> replaces an older running daemon with this
    /// client's version and prints <c>ahd &lt;version&gt; pid &lt;pid&gt;</c> (or <c>error: &lt;code&gt;: &lt;message&gt;</c> and exit 1);
    /// <c>stop</c> prints <c>stopped</c> or <c>not running</c> and exits 0. Returns the exit code.
    /// </summary>
    Task<int> RunMsgAliasAsync(string verb, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default);
}
