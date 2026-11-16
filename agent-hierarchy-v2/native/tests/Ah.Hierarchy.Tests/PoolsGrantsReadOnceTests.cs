using Ah.Abstractions;

namespace Ah.Hierarchy.Tests;

/// <summary>b459: enumerating a dir's pools over several teams reads the dir's grants once, and lists the same pools.</summary>
public sealed class PoolsGrantsReadOnceTests
{
    /// <summary>Passes every call to <paramref name="inner"/>; counts grants reads by dir.</summary>
    private sealed class GrantCounting(IFileReader inner) : IFileReader
    {
        internal int GrantReads;

        public string ReadText(string path) => inner.ReadText(path);

        public byte[] ReadBytes(string path) => inner.ReadBytes(path);

        public FileTail ReadTail(string path, int maxBytes) => inner.ReadTail(path, maxBytes);

        public FileRange ReadRange(string path, long offset, int maxBytes) => inner.ReadRange(path, offset, maxBytes);

        public NoFollowRead ReadNoFollow(string path, int maxBytes) => inner.ReadNoFollow(path, maxBytes);

        public FileStat? Stat(string path, bool followSymlinks = true) => inner.Stat(path, followSymlinks);

        public bool Exists(string path) => inner.Exists(path);

        public string RealPath(string path) => inner.RealPath(path);

        public string? ReadLink(string path) => inner.ReadLink(path);

        public AskLog ReadAskLog(string path) => inner.ReadAskLog(path);

        public GrantsLog ReadGrants(string hierarchyDir, string auditPath)
        {
            _ = Interlocked.Increment(ref GrantReads);
            return inner.ReadGrants(hierarchyDir, auditPath);
        }

        public IReadOnlyList<string> List(string directory) => inner.List(directory);

        public bool IsExecutable(string path) => inner.IsExecutable(path);
    }

    [Fact]
    public void ThreeTeamsReadTheGrantsOnceAndListTheUnionOfTheirPools()
    {
        var fixture = StatusEntriesFixture.Create();
        var dir = StatusEntriesFixture.Pool(fixture.R);
        // t's implementor is granted Q, so the shared read still applies a team's document
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "grants")).FullName, "t.json"),
            $"{{\"version\":1,\"team\":\"t\",\"mode\":\"log\",\"members\":{{\"t-implementor\":{{\"work_root\":\"{fixture.Q}\"}}}}}}");
        string[] teams = ["t", "c", "u"];
        var files = new GrantCounting(Ah.Platform.PlatformModule.CreateIo(null).Files);
        var services = fixture.Services(files: files);

        var together = services.Messages.PoolDirs(dir, teams);
        Assert.Equal(1, files.GrantReads);

        var apart = teams.SelectMany(t => services.Messages.PoolDirs(dir, [t])).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(apart.Order(StringComparer.Ordinal), together.Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(StatusEntriesFixture.Pool(fixture.Q), together);
    }
}
