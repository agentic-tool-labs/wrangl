using System.Globalization;
using Ah.Watch;

namespace Ah.Daemon;

/// <summary>
/// The daemon's recent events, in memory. A cursor is <c>&lt;epoch&gt;.&lt;seq&gt;</c>: the epoch names this daemon start, the sequence
/// counts events within it. A cursor from another epoch, or older than the ring holds, cannot be continued: the client reads the
/// root view again.
/// </summary>
internal sealed class ViewEventLog(string epoch, int capacity = ViewEventLog.DefaultCapacity)
{
    internal const int DefaultCapacity = 512;

    private readonly object _gate = new();
    private readonly Queue<(long Seq, ViewEvent Event)> _ring = new();
    private long _seq;
    private long _floor;

    /// <summary>What a read found: events after the cursor, or a request to start over.</summary>
    /// <param name="Resync">True when the cursor could not be continued (none given, another epoch, or fallen off the ring).</param>
    /// <param name="Events">The events after the cursor, oldest first, each with its own cursor.</param>
    /// <param name="Cursor">The newest cursor; a client asks for events after it.</param>
    internal sealed record Page(bool Resync, IReadOnlyList<(string Id, ViewEvent Event)> Events, string Cursor);

    internal string Epoch { get; } = epoch;

    internal string Head
    {
        get
        {
            lock (_gate)
            {
                return Format(_seq);
            }
        }
    }

    private string Format(long seq) => Epoch + "." + seq.ToString(CultureInfo.InvariantCulture);

    internal void Append(IEnumerable<ViewEvent> events, int dropped = 0)
    {
        lock (_gate)
        {
            if (dropped > 0)
            {
                _seq += dropped;
                _floor = _seq;
                _ring.Clear();
            }

            foreach (var e in events)
            {
                _seq++;
                _ring.Enqueue((_seq, e));
                while (_ring.Count > capacity)
                {
                    _floor = _ring.Dequeue().Seq;
                }
            }
        }
    }

    /// <summary>Whether <paramref name="text"/> has the shape of a cursor.</summary>
    internal static bool TryParse(string text, out string epoch, out long seq)
    {
        epoch = string.Empty;
        seq = 0;
        var dot = text.LastIndexOf('.');
        if (dot <= 0 || dot == text.Length - 1 || !text[..dot].All(char.IsAsciiDigit))
        {
            return false;
        }

        epoch = text[..dot];
        return long.TryParse(text[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }

    /// <summary>The events after <paramref name="since"/>; null, a cursor of another epoch or one older than the ring holds asks for a re-sync.</summary>
    internal Page Read(string? since)
    {
        lock (_gate)
        {
            var head = Format(_seq);
            if (since is null || !TryParse(since, out var sinceEpoch, out var sinceSeq) || sinceEpoch != Epoch || sinceSeq > _seq || sinceSeq < _floor)
            {
                return new Page(true, [], head);
            }

            return new Page(false, [.. _ring.Where(e => e.Seq > sinceSeq).Select(e => (Format(e.Seq), e.Event))], head);
        }
    }
}
