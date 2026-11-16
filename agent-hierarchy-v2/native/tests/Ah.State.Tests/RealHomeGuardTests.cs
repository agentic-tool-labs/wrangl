using Ah.TestProcess;

namespace Ah.State.Tests;

/// <summary>
/// The snapshot runs in every test process's module initializer, over a home live sessions are writing to. An exception there aborts the
/// host (exit 134) before it finds a single test, so an entry that goes away during the walk must be an entry that reads as absent.
/// </summary>
public sealed class RealHomeGuardTests
{
    private static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "guard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(home, ".claude", "ah2", "msgs"));
        return home;
    }

    [Fact]
    public void AFileRemovedDuringTheWalkDoesNotThrow()
    {
        var home = NewHome();
        var msgs = Path.Combine(home, ".claude", "ah2", "msgs");
        var names = Enumerable.Range(0, 300).Select(i => Path.Combine(msgs, "m" + i)).ToArray();
        foreach (var name in names)
        {
            File.WriteAllText(name, "x");
        }

        using var stop = new CancellationTokenSource();
        var churn = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var name in names)
                {
                    File.Delete(name);
                    File.WriteAllText(name, "x");
                }
            }
        });
        churn.Start();

        try
        {
            for (var i = 0; i < 400; i++)
            {
                _ = RealHomeGuard.Snapshot(home);
            }
        }
        finally
        {
            stop.Cancel();
            churn.Join();
        }
    }

    [Fact]
    public void NegativeAChangedFileStillShowsInTheDifference()
    {
        var home = NewHome();
        var file = Path.Combine(home, ".claude", "ah2", "msgs", "m");
        File.WriteAllText(file, "x");
        var before = RealHomeGuard.Snapshot(home);

        Assert.Null(RealHomeGuard.Difference(before, RealHomeGuard.Snapshot(home)));

        File.WriteAllText(file, "longer");

        Assert.Contains("|file|6|", RealHomeGuard.Difference(before, RealHomeGuard.Snapshot(home)), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeARemovedFileStillShowsInTheDifference()
    {
        var home = NewHome();
        var file = Path.Combine(home, ".claude", "ah2", "msgs", "m");
        File.WriteAllText(file, "x");
        var before = RealHomeGuard.Snapshot(home);

        File.Delete(file);

        Assert.Contains("was " + file + "|file|1|", RealHomeGuard.Difference(before, RealHomeGuard.Snapshot(home)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyOrMissingHomeReadsAsAbsent()
    {
        var home = Path.Combine(Path.GetTempPath(), "guard-none-" + Guid.NewGuid().ToString("N")[..8]);

        Assert.EndsWith("|absent", RealHomeGuard.Snapshot(home), StringComparison.Ordinal);
    }
}
