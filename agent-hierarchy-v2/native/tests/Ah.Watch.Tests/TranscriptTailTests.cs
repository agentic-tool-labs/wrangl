using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>An in-memory reader that counts how much of a file is read; a file can be a generated one far larger than memory.</summary>
internal sealed class TranscriptFiles : IFileReader
{
    private readonly Dictionary<string, (byte[] Data, long Virtual, ulong Inode)> _files = new(StringComparer.Ordinal);
    private ulong _nextInode = 100;

    public int Ranges { get; set; }

    public int Tails { get; set; }

    public int WholeReads { get; set; }

    public int MaxRequest { get; private set; }

    public long BytesRead { get; private set; }

    /// <summary>Counts the files ever added: what a directory's modification time stands for.</summary>
    public int DirChanges { get; private set; }

    public int Lists { get; private set; }

    public void Put(string path, byte[] data, bool replace = false)
    {
        if (!_files.ContainsKey(path))
        {
            DirChanges++;
        }

        var inode = _files.TryGetValue(path, out var old) && !replace ? old.Inode : _nextInode++;
        _files[path] = (data, 0, inode);
    }

    public void Append(string path, string text) => Put(path, [.. _files[path].Data, .. Encoding.UTF8.GetBytes(text)]);

    /// <summary>A file of <paramref name="length"/> bytes whose last bytes are the real <paramref name="tail"/>; the rest is filler that is never newline-terminated.</summary>
    public void PutHuge(string path, long length, byte[] tail) => _files[path] = (tail, length, _nextInode++);

    public FileTail ReadTail(string path, int maxBytes)
    {
        Tails++;
        MaxRequest = Math.Max(MaxRequest, maxBytes);
        var (data, virt, _) = _files[path];
        if (virt > 0)
        {
            var take = (int)Math.Min(maxBytes, data.Length);
            BytesRead += take;
            return new FileTail(data[^take..], virt, true);
        }

        var n = Math.Min(maxBytes, data.Length);
        BytesRead += n;
        return new FileTail(data[^n..], data.Length, data.Length > n);
    }

    public FileRange ReadRange(string path, long offset, int maxBytes)
    {
        Ranges++;
        MaxRequest = Math.Max(MaxRequest, maxBytes);
        var (data, virt, _) = _files[path];
        var length = virt > 0 ? virt : data.Length;
        if (offset >= length)
        {
            return new FileRange([], length);
        }

        var n = (int)Math.Min(maxBytes, length - offset);
        BytesRead += n;
        if (virt > 0)
        {
            var from = offset - (virt - data.Length);
            return from >= 0 ? new FileRange(data[(int)from..(int)Math.Min(data.Length, from + n)], length) : new FileRange(Enumerable.Repeat((byte)'x', n).ToArray(), length);
        }

        return new FileRange(data[(int)offset..(int)(offset + n)], length);
    }

    public FileStat? Stat(string path, bool followSymlinks = true) =>
        _files.TryGetValue(path, out var f) ? new FileStat(FileKind.File, f.Virtual > 0 ? f.Virtual : f.Data.Length, 0, f.Inode, 0, 0)
        : _files.Keys.Any(k => k.StartsWith(path + "/", StringComparison.Ordinal)) ? new FileStat(FileKind.Directory, 0, DirChanges, 0, 0, 0) : null;

    public string ReadText(string path)
    {
        WholeReads++;
        throw new NotSupportedException();
    }

    public byte[] ReadBytes(string path)
    {
        WholeReads++;
        throw new NotSupportedException();
    }

    public NoFollowRead ReadNoFollow(string path, int maxBytes) => throw new NotSupportedException();

    public bool Exists(string path) => _files.ContainsKey(path);

    public string RealPath(string path) => path;

    public string? ReadLink(string path) => null;

    public IReadOnlyList<string> List(string directory) =>
        Listed(directory);

    private IReadOnlyList<string> Listed(string directory)
    {
        Lists++;
        return [.. _files.Keys.Where(k => k.StartsWith(directory + "/", StringComparison.Ordinal)).Select(k => k[(directory.Length + 1)..].Split('/')[0]).Distinct(StringComparer.Ordinal)];
    }

    public bool IsExecutable(string path) => false;

    public void WriteAtomic(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) => throw new NotSupportedException();

    public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) => throw new NotSupportedException();

    public void Rename(string fromPath, string toPath) => throw new NotSupportedException();

    public bool Unlink(string path) => throw new NotSupportedException();

    public void Mkdir(string path, UnixFileMode? mode = null) => throw new NotSupportedException();

    public void Append(string path, ReadOnlySpan<byte> record) => throw new NotSupportedException();
}

/// <summary>Takes assistant usage the way the shared parser does; the rest of the interface is not used by the tail.</summary>
internal sealed class TestUsage : IHierarchyUsage
{
    public string UsageFile => throw new NotSupportedException();

    public string CacheFile => throw new NotSupportedException();

    public int PruneAt => 0;

    public int PruneTo => 0;

    public JsonValue RoleFor(JsonValue? agentType, JsonValue? agentRoles = null) => throw new NotSupportedException();

    public JsonObject ZeroUsage() => throw new NotSupportedException();

    public JsonObject AddUsage(JsonObject accumulator, JsonValue? usage) => throw new NotSupportedException();

    public JsonValue SumTranscript(string file) => throw new NotSupportedException();

    public AssistantUsage? AssistantUsageOf(JsonValue entry) =>
        entry.Member("type") is { IsText: true } t && t.AsString() == "assistant" && entry.Member("message")?.Member("usage") is { IsTruthy: true } u
            ? new AssistantUsage(u, null, null) : null;

    public void PruneUsageFile(IStoreWriteContext writer) => throw new NotSupportedException();

    public void AppendUsageRecord(IStoreWriteContext writer, JsonValue? record, string? operationId = null, bool prune = true) => throw new NotSupportedException();

    public JsonArray ReadUsageRecords() => throw new NotSupportedException();
}

public sealed class TranscriptTailTests
{
    private const string Path = "/h/.claude/projects/p/s.jsonl";

    internal static string User(string text, string stamp = "2026-01-01T00:00:00Z") =>
        "{\"type\":\"user\",\"timestamp\":\"" + stamp + "\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}\n";

    internal static string Assistant(int input, int output, string stamp, string extra = "") =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + stamp + "\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\",\"input\":{\"command\":\"SECRET-INPUT\"}}" + extra
        + "],\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":3,\"cache_read_input_tokens\":4}}}\n";

    private static (TranscriptTail Tail, TranscriptFiles Files) Make(string content)
    {
        var files = new TranscriptFiles();
        files.Put(Path, Encoding.UTF8.GetBytes(content));
        return (new TranscriptTail(files, new TestUsage()), files);
    }

    [Fact]
    public void AFileLargerThanTheWindowDropsItsPartialFirstLine()
    {
        var line = User(new string('a', 1000));
        var content = string.Concat(Enumerable.Repeat(line, 400));
        var (tail, files) = Make(content);

        Assert.True(tail.Advance(Path));

        Assert.True(content.Length > TranscriptTail.Window);
        Assert.All(tail.Entries, e => Assert.Equal("user", e.Type));
        Assert.Equal(0, (tail.OldestHeldOffset - (content.Length - TranscriptTail.Window)) % 1);
        Assert.True(tail.OldestHeldOffset >= content.Length - TranscriptTail.Window);
        Assert.Equal(0, tail.OldestHeldOffset % line.Length);
        Assert.Equal(content.Length, tail.NewestOffset);
        Assert.Equal(1, files.Tails);
        Assert.Equal(0, files.Ranges);
    }

    [Fact]
    public void AnAppendIsFollowedFromTheNewestOffsetAndEqualsARebuild()
    {
        var (tail, files) = Make(User("one"));
        _ = tail.Advance(Path);
        var script = new[] { Assistant(1, 2, "2026-01-01T00:00:01Z"), User("three"), "{\"type\":\"summary\"}\n", Assistant(5, 6, "2026-01-01T00:00:04Z") };
        foreach (var step in script)
        {
            files.Append(Path, step);
            _ = tail.Advance(Path);
            var rebuilt = new TranscriptTail(files, new TestUsage());
            _ = rebuilt.Advance(Path);

            Assert.Equal(rebuilt.Entries.Select(e => JsonText.Stringify(e.ToJson(), 0)), tail.Entries.Select(e => JsonText.Stringify(e.ToJson(), 0)));
            Assert.Equal(rebuilt.NewestOffset, tail.NewestOffset);
            Assert.Equal(rebuilt.UsageSince(1), tail.UsageSince(1));
            Assert.Equal(rebuilt.AsOfMs, tail.AsOfMs);
        }

        Assert.Equal(0, tail.Resets);
    }

    [Fact]
    public void AHalfWrittenLastLineIsNotConsumedUntilItsNewlineArrives()
    {
        var (tail, files) = Make(User("one"));
        _ = tail.Advance(Path);
        var offset = tail.NewestOffset;

        files.Append(Path, "{\"type\":\"user\",\"mess");
        _ = tail.Advance(Path);
        Assert.Equal(offset, tail.NewestOffset);
        Assert.Single(tail.Entries);

        files.Append(Path, "age\":{\"content\":\"x\"}}\n");
        _ = tail.Advance(Path);
        Assert.Equal(2, tail.Entries.Count);
    }

    [Fact]
    public void ALineLongerThanTheWindowIsReadInWindowsAndKeptWhole()
    {
        var (tail, files) = Make(User("one"));
        _ = tail.Advance(Path);
        var big = User(new string('b', 600_000));

        files.Append(Path, big);
        _ = tail.Advance(Path);

        Assert.Equal(2, tail.Entries.Count);
        Assert.False(tail.Entries[1].Oversize);
        Assert.Equal(big.Length, tail.Entries[1].Bytes);
        Assert.True(files.MaxRequest <= TranscriptTail.Window);
    }

    [Fact]
    public void ALineOverTheCapBecomesAnOversizeEntryAndTheNextLineIsStillRead()
    {
        var (tail, files) = Make(User("one"));
        _ = tail.Advance(Path);
        var huge = User(new string('c', 4 * 1024 * 1024 + 10_000));

        files.Append(Path, huge + User("after"));
        _ = tail.Advance(Path);

        Assert.Equal(3, tail.Entries.Count);
        Assert.True(tail.Entries[1].Oversize);
        Assert.Equal("other", tail.Entries[1].Type);
        Assert.False(tail.Entries[2].Oversize);
        Assert.Equal("user", tail.Entries[2].Type);
        Assert.Equal(files.Stat(Path)!.Value.Size, tail.NewestOffset);
    }

    [Fact]
    public void AShrinkResetsAndGrowthWithTheSameIdentityDoesNot()
    {
        var (tail, files) = Make(User("one") + User("two"));
        _ = tail.Advance(Path);
        files.Append(Path, User("three"));
        _ = tail.Advance(Path);
        Assert.Equal(0, tail.Resets);

        files.Put(Path, Encoding.UTF8.GetBytes(User("x")));
        _ = tail.Advance(Path);

        Assert.Equal(1, tail.Resets);
        Assert.Equal("shrink", tail.LastResetReason);
        Assert.Single(tail.Entries);
    }

    [Fact]
    public void AReplacedFileResetsEvenWhenItIsLonger()
    {
        var (tail, files) = Make(User("one"));
        _ = tail.Advance(Path);

        files.Put(Path, Encoding.UTF8.GetBytes(User("one") + User("two") + User("three")), replace: true);
        _ = tail.Advance(Path);

        Assert.Equal(1, tail.Resets);
        Assert.Equal("identity", tail.LastResetReason);
        Assert.Equal(3, tail.Entries.Count);
    }

    [Fact]
    public void AnUnknownTypeIsOtherAndTheEntryCarriesNoToolInputOrResultContent()
    {
        var results = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"is_error\":true,\"content\":\"SECRET-RESULT\"}]}}\n";
        var (tail, _) = Make("{\"type\":\"file-history-snapshot\",\"x\":1}\n" + Assistant(1, 2, "2026-01-01T00:00:01Z") + results + "not json\n");

        _ = tail.Advance(Path);

        Assert.Equal(["other", "assistant", "user", "other"], tail.Entries.Select(e => e.Type));
        var text = string.Concat(tail.Entries.Select(e => JsonText.Stringify(e.ToJson(), 0)));
        Assert.DoesNotContain("SECRET-INPUT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-RESULT", text, StringComparison.Ordinal);
        Assert.Contains("\"tool_use_id\":\"t1\"", text, StringComparison.Ordinal);
        Assert.Contains("\"is_error\":true", text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Bash\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextBlockIsCutAtTwoKilobytes()
    {
        var (tail, _) = Make("{\"type\":\"user\",\"message\":{\"content\":\"" + new string('z', 5000) + "\"}}\n");

        _ = tail.Advance(Path);

        var block = Assert.Single(Assert.Single(tail.Entries).Blocks);
        Assert.Equal(2048, block.Excerpt!.Length);
    }

    [Fact]
    public void TurnUsageSumsAssistantEntriesAtOrAfterTheTurnStart()
    {
        var (tail, _) = Make(
            Assistant(100, 100, "2026-01-01T00:00:00Z") + Assistant(1, 2, "2026-01-01T00:00:10Z") + Assistant(10, 20, "2026-01-01T00:00:20Z") + User("u", "2026-01-01T00:00:30Z"));
        _ = tail.Advance(Path);
        var turn = DateTimeOffset.Parse("2026-01-01T00:00:10Z").ToUnixTimeMilliseconds();

        var sum = tail.UsageSince(turn);

        Assert.Equal(new TranscriptUsage(11, 22, 6, 8), sum);
        Assert.Equal(default, tail.UsageSince(0));
    }

    [Fact]
    public void AHundredMegabyteFileIsNeverReadWholeAndEveryReadIsAtMostOneWindow()
    {
        var files = new TranscriptFiles();
        var lines = Encoding.UTF8.GetBytes(User("tail one") + User("tail two"));
        files.PutHuge(Path, 100L * 1024 * 1024, lines);
        var tail = new TranscriptTail(files, new TestUsage());

        _ = tail.Advance(Path);

        Assert.Equal(0, files.WholeReads);
        Assert.True(files.BytesRead <= TranscriptTail.Window);
        Assert.True(files.MaxRequest <= TranscriptTail.Window);
        Assert.Equal(100L * 1024 * 1024, tail.NewestOffset);
    }

    [Fact]
    public void ManyShortLinesAppendedAtOnceCostOneReadPerWindowNotOnePerLine()
    {
        var (tail, files) = Make(User("one"));
        _ = tail.Advance(Path);
        var grown = string.Concat(Enumerable.Range(0, 3000).Select(i => User("line " + i + new string('x', 250))));
        files.Append(Path, grown);
        var before = tail.Reads;

        _ = tail.Advance(Path);

        Assert.Equal(TranscriptTail.RingSize, tail.Entries.Count);
        Assert.Equal(files.Stat(Path)!.Value.Size, tail.NewestOffset);
        Assert.True(tail.Reads - before <= (int)Math.Ceiling(grown.Length / (double)TranscriptTail.Window) + 1, $"{tail.Reads - before} reads for {grown.Length} bytes");
    }

    [Fact]
    public void ALineEndingPastTheCapIsOversizeAndTheNextLineSurvives()
    {
        var (tail, files) = Make(User("one"));
        _ = tail.Advance(Path);
        var huge = User(new string('c', 4 * 1024 * 1024 + 100));

        files.Append(Path, huge + User("after"));
        _ = tail.Advance(Path);

        Assert.Equal(3, tail.Entries.Count);
        Assert.True(tail.Entries[1].Oversize);
        Assert.True(tail.Entries[1].Bytes >= TranscriptTail.LineCap);
        Assert.Equal("after", tail.Entries[2].Blocks.Single().Excerpt);
        Assert.Equal("user", tail.Entries[2].Type);
        Assert.Equal(files.Stat(Path)!.Value.Size, tail.NewestOffset);
    }

    [Fact]
    public void AnEntriesListHandedOutDoesNotChangeWhenTheTailAdvances()
    {
        var (tail, files) = Make(User("one"));
        _ = tail.Advance(Path);
        var held = tail.Entries;

        files.Append(Path, User("two"));
        _ = tail.Advance(Path);

        Assert.Single(held);
        Assert.Equal(2, tail.Entries.Count);
    }
}
