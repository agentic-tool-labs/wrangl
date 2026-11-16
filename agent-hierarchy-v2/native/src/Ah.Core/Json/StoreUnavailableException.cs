namespace Ah.Core.Json;

/// <summary>
/// A store that is in use (a domain in <c>db</c> mode) cannot be read, so an absence found in it proves nothing. It is not an
/// <see cref="InvalidOperationException"/> or an I/O error on purpose: a handler written for those must not turn it into "nothing there".
/// </summary>
public sealed class StoreUnavailableException : Exception
{
    public StoreUnavailableException()
        : base("the state store cannot be read")
    {
    }

    public StoreUnavailableException(string message)
        : base(message)
    {
    }

    public StoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// What to do next, from the reason: a busy, locked or importing store is tried again, an older
    /// binary is updated, and only a store known to be damaged is pointed at a restore, which replaces it. Any other reason gets no advice (null).
    /// </summary>
    public string? Advice =>
        Has("busy") || Has("locked") || Has("importing") ? "try again in a moment"
        : Has("update ah") ? "update ah"
        : Has("not a database") || Has("malformed") || Has("corrupt") || Has("damaged") || Has("store-missing") ? "restore it with: ah db restore"
        : null;

    private bool Has(string word) => Message.Contains(word, StringComparison.Ordinal);
}
