namespace Ah.StateDb;

/// <summary>The synchronous level of a connection: what a commit waits for before it returns.</summary>
internal enum SyncLevel
{
    Normal,
    Full,
}

/// <summary>
/// Per-connection settings the opener applies. <paramref name="WalAutocheckpoint"/> null leaves SQLite's default; 0 turns automatic
/// checkpoints off, which the hook and CLI connections use so that the daemon alone decides when the log is folded back.
/// </summary>
internal sealed record StateDbOpenOptions(SyncLevel Synchronous, int? WalAutocheckpoint)
{
    internal static StateDbOpenOptions Default { get; } = new(SyncLevel.Full, null);
}
