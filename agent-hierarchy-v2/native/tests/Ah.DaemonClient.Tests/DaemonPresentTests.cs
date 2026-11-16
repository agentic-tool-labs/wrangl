using Ah.Abstractions;
using Ah.TestKit;

namespace Ah.DaemonClient.Tests;

/// <summary>A daemon that did not answer discovery is told from one that is not there by its recorded process and its socket file.</summary>
public sealed class DaemonPresentTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ah-daemon-present-").FullName;

    public void Dispose() => Directory.Delete(_home, true);

    private sealed class Inspector(bool alive) : IProcessInspector
    {
        public bool PidAlive(int pid) => alive;

        public bool PidOwned(int pid) => alive;

        public string? CommandLine(int pid) => null;
    }

    private sealed class Files(string root) : IFileReader
    {
        public string ReadText(string path) => File.ReadAllText(path);

        public byte[] ReadBytes(string path) => File.ReadAllBytes(path);

        public FileTail ReadTail(string path, int maxBytes) => throw new NotSupportedException();

        public FileRange ReadRange(string path, long offset, int maxBytes) => throw new NotSupportedException();

        public NoFollowRead ReadNoFollow(string path, int maxBytes) => throw new NotSupportedException();

        public FileStat? Stat(string path, bool followSymlinks = true) => throw new NotSupportedException();

        public bool Exists(string path) => File.Exists(path);

        public string RealPath(string path) => throw new NotSupportedException();

        public IReadOnlyList<string> List(string directory) => throw new NotSupportedException();

        public bool IsExecutable(string path) => throw new NotSupportedException();

        internal string Root { get; } = root;
    }

    private AhdClient Client(bool pidAlive) => new(new DaemonClientDependencies(
        new Files(_home), null!, null!, null!, null!, null!, null!, new Inspector(pidAlive), null!,
        new FakeEnvironment(new Dictionary<string, string> { ["AH_HOME"] = _home }), new Dictionary<string, string?>(), null));

    private void Record(string name, string content)
    {
        Directory.CreateDirectory(Path.Combine(_home, "run"));
        File.WriteAllText(Path.Combine(_home, "run", name), content);
    }

    [Fact]
    public void ARecordedProcessThatIsAliveIsADaemon()
    {
        Record("ahd.json", "{\"pid\":4242,\"socket\":\"/x/ahd.sock\"}");

        Assert.True(Client(pidAlive: true).DaemonPresent());
    }

    [Fact]
    public void ASocketFileIsADaemonEvenWithNoRecord()
    {
        Record("ahd.sock", string.Empty);

        Assert.True(Client(pidAlive: false).DaemonPresent());
    }

    [Fact]
    public void ARecordOfAProcessThatIsGoneWithNoSocketIsNotADaemon()
    {
        Record("ahd.json", "{\"pid\":4242,\"socket\":\"/x/ahd.sock\"}");

        Assert.False(Client(pidAlive: false).DaemonPresent());
    }

    [Fact]
    public void NothingRecordedIsNotADaemon() =>
        Assert.False(Client(pidAlive: true).DaemonPresent());
}
