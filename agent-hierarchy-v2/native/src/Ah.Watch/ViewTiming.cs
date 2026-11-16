using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Ah.Watch;

/// <summary>The timing of one view request, from the moment it opened.</summary>
public interface IViewTimingScope
{
    /// <summary>The view path the request asked for.</summary>
    string Path { get; }

    /// <summary>Counts <paramref name="ticks"/> stopwatch ticks spent serialising the answer.</summary>
    void AddJson(long ticks);

    /// <summary>Counts one build of the page; a request that needs the herdr snapshot builds twice.</summary>
    void AddBuild();

    /// <summary>The one <c>view-timing</c> log line of the request, ended now.</summary>
    string Line();
}

/// <summary>
/// Per-request timing of one view build, switched on by <c>AH_VIEW_TIMING=1</c> in the daemon's environment. A request opens a
/// <see cref="ViewTimingScope"/> that follows the request through its async calls; the instrumented readers and runners add to the
/// scope they find there, and a call made with no scope open records nothing. With the switch off no scope is ever opened and the
/// decorators are not part of the composition.
/// </summary>
internal static class ViewTiming
{
    /// <summary>The environment variable that turns the timing on.</summary>
    internal const string Switch = "AH_VIEW_TIMING";

    private static readonly AsyncLocal<ViewTimingScope?> Ambient = new();

    /// <summary>The scope of the request this call belongs to, or null.</summary>
    internal static ViewTimingScope? Current => Ambient.Value;

    /// <summary>Whether <paramref name="value"/>, the value of the switch, turns the timing on.</summary>
    internal static bool IsOn(string? value) => value == "1";

    /// <summary>Opens the scope of a request for the view at <paramref name="path"/> and makes it the current one.</summary>
    internal static ViewTimingScope Open(string path, Func<long>? now = null, int siteEvery = ViewTimingScope.DefaultSiteEvery)
    {
        var scope = new ViewTimingScope(path, now, siteEvery);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>Times a section of the build until the result is disposed. Without a scope it times nothing.</summary>
    internal static SectionTimer Section(string name) => Ambient.Value is { } scope ? new SectionTimer(scope, name) : default;
}

/// <summary>A section being timed; dispose it to close the section.</summary>
internal readonly struct SectionTimer : IDisposable
{
    private readonly ViewTimingScope? _scope;
    private readonly int _depth;

    internal SectionTimer(ViewTimingScope scope, string name)
    {
        _scope = scope;
        _depth = scope.BeginSection(name);
    }

    /// <summary>Closes the section.</summary>
    public void Dispose() => _scope?.EndSection(_depth);
}

/// <summary>What one request did: its sections, its reads, its git and process-table calls and its serialising.</summary>
internal sealed class ViewTimingScope : IViewTimingScope
{
    private const int TopDirs = 10;

    private sealed class Frame(string name, long start)
    {
        internal string Name { get; } = name;

        internal long Start { get; } = start;

        internal long Child { get; set; }
    }

    private sealed class DirTally
    {
        internal int Files;
        internal long Bytes;
        internal int Lists;
        internal int Stats;
    }

    private readonly object _gate = new();
    private readonly Func<long> _now;
    private readonly long _start;
    private readonly List<Frame> _stack = [];
    private readonly Dictionary<string, long> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirTally> _dirs = new(StringComparer.Ordinal);
    private long _opens;
    private long _bytes;
    private long _lists;
    private long _stats;
    private long _gitCalls;
    private long _gitTicks;
    private readonly SortedDictionary<string, long> _calls = new(StringComparer.Ordinal);
    private long _gitStarted;
    private long _gitDone;
    private long _builds;
    private long _psCalls;
    private long _psTicks;
    private long _execCalls;
    private long _execTicks;
    private long _jsonTicks;
    private long _cacheHits;
    private long _cacheMisses;
    private long _uncacheable;
    private long _memoHits;

    internal const int DefaultSiteEvery = 64;

    private readonly int _siteEvery;
    private readonly SortedDictionary<string, long> _sites = new(StringComparer.Ordinal);
    private long _siteSeq;

    internal ViewTimingScope(string path, Func<long>? now = null, int siteEvery = DefaultSiteEvery)
    {
        _siteEvery = siteEvery;
        Path = path;
        _now = now ?? Stopwatch.GetTimestamp;
        _start = _now();
    }

    /// <inheritdoc />
    public string Path { get; }

    internal int BeginSection(string name)
    {
        lock (_gate)
        {
            _stack.Add(new Frame(name, _now()));
            return _stack.Count - 1;
        }
    }

    internal void EndSection(int depth)
    {
        lock (_gate)
        {
            if (depth != _stack.Count - 1)
            {
                return;
            }

            var frame = _stack[depth];
            _stack.RemoveAt(depth);
            var elapsed = _now() - frame.Start;
            _sections[frame.Name] = _sections.GetValueOrDefault(frame.Name) + elapsed - frame.Child;
            if (_stack.Count > 0)
            {
                _stack[^1].Child += elapsed;
            }
        }
    }

    /// <summary>Counts a read of the file at <paramref name="path"/> that returned <paramref name="bytes"/> bytes.</summary>
    public void AddRead(string path, long bytes)
    {
        lock (_gate)
        {
            _opens++;
            _bytes += bytes;
            var tally = TallyOf(DirectoryKey(path, isDirectory: false));
            tally.Files++;
            tally.Bytes += bytes;
        }
    }

    /// <summary>
    /// Counts a call that reached the disk reader, by method, by what the path is (<c>msgs</c> directly in a pool, <c>msgs-archive</c>, <c>msgs-sub</c> deeper
    /// below <c>msgs</c>, <c>registrations</c> or other) and by the listing scope: <c>in</c> when one is open and covers the path, <c>out</c> when one is open and
    /// does not, <c>noscope</c> when none is open.
    /// </summary>
    public void AddCall(string method, string path, bool directory)
    {
        var kind = "other";
        if (path.EndsWith("/msgs", StringComparison.Ordinal))
        {
            kind = "msgs";
        }
        else if (path.LastIndexOf("/msgs/", StringComparison.Ordinal) is var at and >= 0)
        {
            var rest = path[(at + "/msgs/".Length)..];
            kind = !rest.Contains('/', StringComparison.Ordinal) ? "msgs"
                : rest.StartsWith("archive/", StringComparison.Ordinal) && rest.IndexOf('/', "archive/".Length) < 0 ? "msgs-archive"
                : "msgs-sub";
        }
        else if (path.Contains("/registrations", StringComparison.Ordinal))
        {
            kind = "registrations";
        }

        var coverage = !Ah.Core.Files.ListingScope.IsOpen ? "noscope"
            : (directory ? Ah.Core.Files.ListingScope.Covers(path) : Ah.Core.Files.ListingScope.CoversFile(path)) ? "in" : "out";
        var key = method + "/" + kind + "/" + coverage;
        string? site = null;
        if (_siteEvery > 0 && Interlocked.Increment(ref _siteSeq) % _siteEvery == 0)
        {
            site = key + "@" + CallSite();
        }

        lock (_gate)
        {
            _calls[key] = _calls.GetValueOrDefault(key) + 1;
            if (site is not null)
            {
                _sites[site] = _sites.GetValueOrDefault(site) + 1;
            }
        }
    }

    /// <summary>The first frame above the reader wrappers, as <c>file:line</c>; only a sample of calls pays for a stack walk, and a build without symbols names none.</summary>
    private static string CallSite()
    {
        foreach (var frame in new StackTrace(true).GetFrames())
        {
            if (frame.GetFileName() is not { } file)
            {
                continue;
            }

            var name = System.IO.Path.GetFileName(file);
            if (name is "ViewTiming.cs" or "TimedIo.cs" or "ListingScope.cs")
            {
                continue;
            }

            return name + ":" + frame.GetFileLineNumber().ToString(CultureInfo.InvariantCulture);
        }

        return "?";
    }

    /// <summary>Counts a listing of the directory <paramref name="path"/>.</summary>
    public void AddList(string path)
    {
        lock (_gate)
        {
            _lists++;
            TallyOf(DirectoryKey(path, isDirectory: true)).Lists++;
        }
    }

    /// <summary>Counts a stat or an existence check of <paramref name="path"/>.</summary>
    public void AddStat(string path)
    {
        lock (_gate)
        {
            _stats++;
            TallyOf(DirectoryKey(path, isDirectory: false)).Stats++;
        }
    }

    /// <summary>Counts a finished process run that took <paramref name="ticks"/> stopwatch ticks, by what it ran.</summary>
    public void AddProcess(string executable, long ticks)
    {
        lock (_gate)
        {
            switch (executable)
            {
                case "git":
                    _gitCalls++;
                    _gitTicks += ticks;
                    break;
                case "ps":
                    _psCalls++;
                    _psTicks += ticks;
                    break;
                default:
                    _execCalls++;
                    _execTicks += ticks;
                    break;
            }
        }
    }

    /// <summary>Adds what one page build's listing scope answered.</summary>
    internal void AddCache(Ah.Core.Files.ListingCounts counts)
    {
        lock (_gate)
        {
            _cacheHits += counts.CacheHits;
            _cacheMisses += counts.CacheMisses;
            _uncacheable += counts.Uncacheable;
            _memoHits += counts.MemoHits;
        }
    }

    /// <inheritdoc />
    public void AddBuild()
    {
        lock (_gate)
        {
            _builds++;
        }
    }

    /// <summary>Counts a git refresh this request started; it runs behind the request, so it may finish after the line is written.</summary>
    public void AddGitStarted()
    {
        lock (_gate)
        {
            _gitStarted++;
        }
    }

    /// <summary>Counts a git refresh this request started as finished.</summary>
    public void AddGitDone()
    {
        lock (_gate)
        {
            _gitDone++;
        }
    }

    /// <inheritdoc />
    public void AddJson(long ticks)
    {
        lock (_gate)
        {
            _jsonTicks += ticks;
        }
    }

    /// <summary>
    /// The place a path is tallied under: its directory relative to the hierarchy root (the directory above its <c>.claude/ah2</c>),
    /// cut at depth 3 (<c>.claude/ah2/msgs</c>); a path outside any <c>.claude/ah2</c> is tallied under the last three segments of
    /// its directory.
    /// </summary>
    internal static string DirectoryKey(string path, bool isDirectory)
    {
        var dir = isDirectory ? path.TrimEnd('/') : ParentOf(path);
        var at = dir.IndexOf("/.claude/ah2", StringComparison.Ordinal);
        if (at >= 0)
        {
            var rest = dir[(at + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            return string.Join('/', rest.Take(3));
        }

        var segments = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "…/" + string.Join('/', segments.Skip(Math.Max(0, segments.Length - 3)));
    }

    private static string ParentOf(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut <= 0 ? string.Empty : path[..cut];
    }

    private DirTally TallyOf(string key)
    {
        if (!_dirs.TryGetValue(key, out var tally))
        {
            tally = new DirTally();
            _dirs[key] = tally;
        }

        return tally;
    }

    private static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string Line()
    {
        lock (_gate)
        {
            var total = _now() - _start;
            var attributed = _sections.Values.Sum();
            var line = new StringBuilder();
            line.Append("view-timing path=").Append(Path)
                .Append(" total_ms=").Append(Ms(total))
                .Append(" sections={");
            foreach (var (name, ticks) in _sections.OrderBy(static s => s.Key, StringComparer.Ordinal))
            {
                line.Append(name).Append(':').Append(Ms(ticks)).Append(',');
            }

            line.Append("other:").Append(Ms(Math.Max(0, total - attributed)))
                .Append("} io={opens:").Append(_opens.ToString(CultureInfo.InvariantCulture))
                .Append(",reads:").Append(_opens.ToString(CultureInfo.InvariantCulture))
                .Append(",bytes:").Append(_bytes.ToString(CultureInfo.InvariantCulture))
                .Append(",lists:").Append(_lists.ToString(CultureInfo.InvariantCulture))
                .Append(",stats:").Append(_stats.ToString(CultureInfo.InvariantCulture))
                .Append("} dirs=[");
            var top = _dirs
                .OrderByDescending(static d => d.Value.Bytes)
                .ThenByDescending(static d => d.Value.Files + d.Value.Lists + d.Value.Stats)
                .ThenBy(static d => d.Key, StringComparer.Ordinal)
                .Take(TopDirs);
            line.Append(string.Join(
                ',',
                top.Select(static d => $"{d.Key}:files={d.Value.Files.ToString(CultureInfo.InvariantCulture)}:bytes={d.Value.Bytes.ToString(CultureInfo.InvariantCulture)}:lists={d.Value.Lists.ToString(CultureInfo.InvariantCulture)}:stats={d.Value.Stats.ToString(CultureInfo.InvariantCulture)}")));
            line.Append("] git={n:").Append(_gitCalls.ToString(CultureInfo.InvariantCulture)).Append(",ms:").Append(Ms(_gitTicks))
                .Append(",started:").Append(_gitStarted.ToString(CultureInfo.InvariantCulture))
                .Append(",pending:").Append(Math.Max(0, _gitStarted - _gitDone).ToString(CultureInfo.InvariantCulture))
                .Append("} ps={n:").Append(_psCalls.ToString(CultureInfo.InvariantCulture)).Append(",ms:").Append(Ms(_psTicks))
                .Append("} exec={n:").Append(_execCalls.ToString(CultureInfo.InvariantCulture)).Append(",ms:").Append(Ms(_execTicks))
                .Append("} json_ms=").Append(Ms(_jsonTicks))
                .Append(" cache={hits:").Append(_cacheHits.ToString(CultureInfo.InvariantCulture))
                .Append(",misses:").Append(_cacheMisses.ToString(CultureInfo.InvariantCulture))
                .Append(",uncacheable:").Append(_uncacheable.ToString(CultureInfo.InvariantCulture))
                .Append(",memo:").Append(_memoHits.ToString(CultureInfo.InvariantCulture))
                .Append("} builds=").Append(_builds.ToString(CultureInfo.InvariantCulture))
                .Append(" calls={").Append(string.Join(',', _calls.Select(static c => c.Key + ":" + c.Value.ToString(CultureInfo.InvariantCulture)))).Append('}')
                .Append(" sites(1/").Append(_siteEvery.ToString(CultureInfo.InvariantCulture)).Append(")={")
                .Append(string.Join(',', _sites.OrderByDescending(static c => c.Value).Take(40).Select(static c => c.Key + ":" + c.Value.ToString(CultureInfo.InvariantCulture)))).Append('}');
            return line.ToString();
        }
    }
}
