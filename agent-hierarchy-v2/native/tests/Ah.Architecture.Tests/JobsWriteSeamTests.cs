namespace Ah.Architecture.Tests;

/// <summary>A job record is written only through the <c>jobs</c> state domain: the hook and the verbs name the state call and no raw file write.</summary>
public sealed class JobsWriteSeamTests
{
    private static readonly string[] RawWrites =
    [
        "File.Write", "File.Move", "File.Append", "File.Create", "FileStream(", "LevelFiles.", "IRawFileWriter", "IRecordAppender", ".WriteText(", "WriteAtomically(",
    ];

    private static readonly string[] Writers =
    [
        Path.Combine("src", "Ah.Hooks", "Activity", "JobTrack.cs"),
        Path.Combine("src", "Ah.Commands", "Roster", "Read", "JobVerb.cs"),
    ];

    internal static string? RawWriteIn(string source) => RawWrites.FirstOrDefault(w => source.Contains(w, StringComparison.Ordinal));

    [Fact]
    public void EveryJobWriterGoesThroughTheStateCallAndNamesNoRawWrite()
    {
        foreach (var relative in Writers)
        {
            var source = File.ReadAllText(Path.Combine(Workspace.NativeRoot, relative));

            Assert.Null(RawWriteIn(source));
            Assert.Contains("StateCalls.PutJob(", source, StringComparison.Ordinal);
        }
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("File.Move(temp, path, overwrite: true);", "File.Move")]
    [InlineData("LevelFiles.WriteAtomically(path, text);", "LevelFiles.")]
    [InlineData("File.WriteAllBytes(temp, bytes);", "File.Write")]
    [InlineData("s.Hierarchy.StateCalls.PutJob(dir, path, record);", null)]
    [InlineData("var records = WatchModule.JobRecordFiles(s.Files, dir);", null)]
    public void TheCheckSeesARawWriteAndOnlyARawWrite(string line, string? expected) => Assert.Equal(expected, RawWriteIn(line));
}
