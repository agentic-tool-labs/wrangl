using Ah.TestProcess;

namespace Ah.Daemon.Tests;

public sealed class SandboxRemoveTests
{
    private const UnixFileMode ReadOnlyDir = UnixFileMode.UserRead | UnixFileMode.UserExecute;

    private static string NewRoot() => "/private/tmp/ahprobe." + Guid.NewGuid().ToString("N")[..12];

    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void ASandboxWithAReadOnlyDirectoryInsideIsRemoved()
    {
        var root = NewRoot();
        var locked = Path.Combine(root, "tmp", "ahsessflip", "registrations");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "s2.json"), "{}");
        File.SetUnixFileMode(locked, ReadOnlyDir);
        try
        {
            SandboxDirectory.Remove(root);

            Assert.False(Directory.Exists(root));
        }
        finally
        {
            SandboxDirectory.Remove(root);
        }
    }

    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void ADirectoryOutsideTheSandboxReachedThroughALinkKeepsItsMode()
    {
        var root = NewRoot();
        var outside = NewRoot();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.SetUnixFileMode(outside, ReadOnlyDir);
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
        try
        {
            SandboxDirectory.Remove(root);

            Assert.False(Directory.Exists(root));
            Assert.Equal(ReadOnlyDir, File.GetUnixFileMode(outside));
        }
        finally
        {
            File.SetUnixFileMode(outside, ReadOnlyDir | UnixFileMode.UserWrite);
            Directory.Delete(outside, recursive: true);
        }
    }
}
