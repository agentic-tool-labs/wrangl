namespace Ah.State;

/// <summary>
/// Told of every state write the service completed successfully, after the write is durable and its after-write hook has run. It is
/// told nothing of a read or of a write that failed. It must return quickly (it only queues work): its failure never fails the write.
/// </summary>
public interface IStateWriteObserver
{
    /// <summary>The write <paramref name="domain"/>.<paramref name="op"/> with the canonical <paramref name="argsJson"/> succeeded and answered <paramref name="resultJson"/>.</summary>
    void Wrote(string domain, string op, ReadOnlyMemory<byte> argsJson, ReadOnlyMemory<byte> resultJson);
}
