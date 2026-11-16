namespace Ah.Mesh;

/// <summary>
/// An in-process scope in which every <c>ah msg send</c> carries <c>hold_exempt</c> and goes to the mailbox files directly, never
/// through (or starting) the daemon. Only the quit and resume verbs open one; no command-line flag can.
/// </summary>
internal static class HoldExemptScope
{
    private static readonly AsyncLocal<bool> Open = new();

    public static bool Active => Open.Value;

    public static IDisposable Begin()
    {
        var before = Open.Value;
        Open.Value = true;
        return new Restore(before);
    }

    private sealed class Restore(bool before) : IDisposable
    {
        public void Dispose() => Open.Value = before;
    }
}
