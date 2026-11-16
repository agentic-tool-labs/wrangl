using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.TestKit;

namespace Ah.Platform.Tests;

public sealed class HostProbeTests
{
    private const double Gib = 1024.0 * 1024.0 * 1024.0;

    [Fact]
    public void TheRealProbeReadsCoresLoadAndMemory()
    {
        var snapshot = PlatformModule.CreateIo(null).HostProbe.Read();
        Assert.True(snapshot.LogicalCores > 0);
        Assert.NotNull(snapshot.LoadAverage1);
        Assert.True(double.IsFinite(snapshot.LoadAverage1.Value) && snapshot.LoadAverage1.Value >= 0);
        Assert.NotNull(snapshot.AvailableMemoryGiB);
        Assert.True(snapshot.AvailableMemoryGiB.Value > 0);
    }

    [Fact]
    public void MacMemoryAgreesWithVmStat()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var probe = PlatformModule.CreateIo(null).HostProbe;
        var first = probe.Read().AvailableMemoryGiB!.Value;
        var expected = VmStatGiB();
        var second = probe.Read().AvailableMemoryGiB!.Value;
        var tolerance = Math.Max(0.5, 0.1 * expected);
        Assert.InRange(expected, Math.Min(first, second) - tolerance, Math.Max(first, second) + tolerance);
    }

    [Fact]
    public void TheFakeReturnsWhatItIsGivenAndCountsReads()
    {
        var given = new HostSnapshot(1.5, 8, 12.0);
        var fake = new FakeHostProbe(given);
        Assert.Equal(given, fake.Read());
        fake.Snapshot = given with { AvailableMemoryGiB = null };
        var second = fake.Read();
        Assert.Null(second.AvailableMemoryGiB);
        Assert.Equal(1.5, second.LoadAverage1);
        Assert.Equal(2, fake.Reads);
    }

    private static double VmStatGiB()
    {
        var info = new ProcessStartInfo("/usr/bin/vm_stat") { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(info)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        double Number(string pattern) => double.Parse(Regex.Match(text, pattern).Groups[1].Value, CultureInfo.InvariantCulture);
        var pageSize = Number(@"page size of (\d+) bytes");
        var pages = Number(@"Pages free:\s+(\d+)") + Number(@"Pages inactive:\s+(\d+)") + Number(@"Pages speculative:\s+(\d+)");
        return pages * pageSize / Gib;
    }
}
