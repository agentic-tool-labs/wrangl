using System.Diagnostics;
using Ah.Core.Time;

namespace Ah.Platform.Tests;

/// <summary>Inside one pass a process's start and ownership are asked once; the next pass asks again.</summary>
public sealed class ProcessPassTests
{
    [Fact]
    public void APassAnswersAProcessAsItFirstSawItAndNegativeTheNextPassSeesItGone()
    {
        var table = PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner());
        var inspector = new ProcessInspector();
        using var child = Process.Start("/bin/sleep", "30");
        var pid = child.Id;

        string? start;
        using (ProcessSnapshotScope.BeginPass())
        {
            start = table.StartTime(pid);
            Assert.NotNull(start);
            Assert.True(inspector.PidOwned(pid));

            child.Kill();
            child.WaitForExit();

            // asked again in the same pass: the first answers stand, no second lookup
            Assert.Equal(start, table.StartTime(pid));
            Assert.True(inspector.PidOwned(pid));
        }

        // negative: a new pass, and no pass, read the ended process as gone
        using (ProcessSnapshotScope.BeginPass())
        {
            Assert.Null(table.StartTime(pid));
            Assert.False(inspector.PidOwned(pid));
        }

        Assert.Null(table.StartTime(pid));
        Assert.False(inspector.PidOwned(pid));
    }
}
