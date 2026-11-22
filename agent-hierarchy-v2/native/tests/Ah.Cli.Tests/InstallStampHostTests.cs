using Ah.Abstractions;
using Ah.Cli.Composition;

namespace Ah.Cli.Tests;

/// <summary>
/// A rebuild whose installer cannot be read throws before anything is spawned, so the caller stamps it failed.
/// The control is a readable script, which is spawned once.
/// </summary>
public sealed class InstallStampHostTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void AMissingScriptThrowsAndSpawnsNothing()
    {
        var spawner = new RecordingSpawner();
        var host = new InstallStampHost(new NullWriter(), spawner);
        var env = new Dictionary<string, string?>();
        var missing = Path.Combine(_sandbox.Home, "no-such", "install.sh");

        var thrown = Assert.ThrowsAny<IOException>(() => host.StartRebuild(missing, _sandbox.Home, env));
        Assert.Contains(missing, thrown.Message, StringComparison.Ordinal);
        Assert.Empty(spawner.Requests);

        var readable = Path.Combine(_sandbox.Home, "install.sh");
        File.WriteAllText(readable, "exit 0\n");
        host.StartRebuild(readable, _sandbox.Home, env);
        Assert.Single(spawner.Requests);
    }

    private sealed class RecordingSpawner : IDetachedSpawner
    {
        public List<DetachedSpawnRequest> Requests { get; } = [];

        public int SpawnDetached(DetachedSpawnRequest request)
        {
            Requests.Add(request);
            return 1;
        }
    }

    private sealed class NullWriter : IRawFileWriter
    {
        public void WriteAtomic(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
        {
        }

        public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) => true;

        public void Rename(string fromPath, string toPath)
        {
        }

        public bool Unlink(string path) => true;

        public void Mkdir(string path, UnixFileMode? mode = null)
        {
        }
    }
}
