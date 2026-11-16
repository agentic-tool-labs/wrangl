namespace Ah.Core.Time;

/// <summary>
/// A span, such as one routed daemon call, in which every process-table question is answered from one reading of the whole
/// process table, taken on the first question, instead of one <c>ps</c> per question. It flows with the async context and ends
/// when disposed.
/// </summary>
public static class ProcessSnapshotScope
{
    private static readonly AsyncLocal<Slot?> Current = new();

    /// <summary>
    /// The open scope's slot, or null outside one. A flow that outlives its scope (a task started inside it) sees none once the
    /// scope is disposed, and so is not answered from the old reading.
    /// </summary>
    public static Slot? Active => Current.Value is { Closed: false } open ? open : null;

    /// <summary>
    /// Opens a scope for the current async flow. Scopes do not nest: an inner one gets its own reading. A <paramref name="shared"/>
    /// scope may be answered from a reading under a second old that other shared scopes took; the default always takes its own.
    /// </summary>
    public static IDisposable Begin(bool shared = false)
    {
        var previous = Current.Value;
        var slot = new Slot(shared);
        Current.Value = slot;
        return new Closer(previous, slot);
    }

    private static readonly AsyncLocal<Slot?> CurrentGroups = new();

    /// <summary>The open grouped-reading span's slot, or null outside one.</summary>
    public static Slot? ActiveGroups => CurrentGroups.Value is { Closed: false } open ? open : null;

    /// <summary>
    /// Opens a span in which the questions that need process groups (the descendants of a process, the members of a group, every
    /// process) share one reading of the table, taken on the first of them. The other questions are answered as they are outside it:
    /// from the kernel where it can be asked. The reading is the span's own and is never reused by a later span.
    /// </summary>
    public static IDisposable BeginGroups()
    {
        var previous = CurrentGroups.Value;
        var slot = new Slot(false);
        CurrentGroups.Value = slot;
        return new GroupsCloser(previous, slot);
    }

    private static readonly AsyncLocal<Slot?> CurrentPass = new();

    /// <summary>The open pass's slot, or null outside one.</summary>
    public static Slot? ActivePass => CurrentPass.Value is { Closed: false } open ? open : null;

    /// <summary>
    /// Opens one pass over many members (a status refresh, a processes poll), in which each process's start and ownership are asked of
    /// the kernel once: a pid asked about again reads as it did first, so a process that ends during the pass reads live until the next.
    /// A routed call's scope does not do this, as a call can wait far longer than a pass.
    /// </summary>
    public static IDisposable BeginPass()
    {
        var previous = CurrentPass.Value;
        var slot = new Slot(false);
        CurrentPass.Value = slot;
        return new PassCloser(previous, slot);
    }

    private sealed class PassCloser(Slot? previous, Slot own) : IDisposable
    {
        public void Dispose()
        {
            own.Close();
            CurrentPass.Value = previous;
        }
    }

    private sealed class GroupsCloser(Slot? previous, Slot own) : IDisposable
    {
        public void Dispose()
        {
            own.Close();
            CurrentGroups.Value = previous;
        }
    }

    /// <summary>Holds the one reading a process table takes for the scope; its shape belongs to that table.</summary>
    public sealed class Slot(bool shared)
    {
        /// <summary>True when the reading may be one other shared scopes took in the last second.</summary>
        public bool Shared { get; } = shared;

        private readonly object _gate = new();
        private bool _taken;
        private object? _value;
        private volatile bool _closed;

        internal bool Closed => _closed;

        internal void Close() => _closed = true;

        /// <summary>The reading, taken by <paramref name="take"/> at most once per scope; a failed reading stays failed (null).</summary>
        public T? Get<T>(Func<T?> take)
            where T : class
        {
            lock (_gate)
            {
                if (!_taken)
                {
                    _taken = true;
                    _value = take();
                }

                return _value as T;
            }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string What, int Pid), object?> _perPid = new();

        /// <summary>What <paramref name="ask"/> answers about <paramref name="pid"/>, asked once in this span: asked again, the first answer stands.</summary>
        public T PerPid<T>(string what, int pid, Func<int, T> ask) => (T)_perPid.GetOrAdd((what, pid), static (key, a) => a(key.Pid), ask)!;

        private int _asks;

        /// <summary>Counts one question this scope answers without its reading; returns how many it has counted, this one included.</summary>
        public int CountAsk() => Interlocked.Increment(ref _asks);

        /// <summary>The reading this scope already holds, or null when none was taken or it failed; never takes one.</summary>
        public T? Held<T>()
            where T : class
        {
            lock (_gate)
            {
                return _value as T;
            }
        }

        /// <summary>The reading, kept once <paramref name="take"/> has produced one; a failed reading (null) is not kept, so the next question takes it again.</summary>
        public T? GetOnceRead<T>(Func<T?> take)
            where T : class
        {
            lock (_gate)
            {
                if (_value is T held)
                {
                    return held;
                }

                var made = take();
                _value = made;
                return made;
            }
        }
    }

    private sealed class Closer(Slot? previous, Slot own) : IDisposable
    {
        public void Dispose()
        {
            own.Close();
            Current.Value = previous;
        }
    }
}
