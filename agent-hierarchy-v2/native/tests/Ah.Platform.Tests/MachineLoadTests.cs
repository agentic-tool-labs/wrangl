namespace Ah.Platform.Tests;

/// <summary>The load average and memory-pressure readings the daemon's load monitor samples.</summary>
public sealed class MachineLoadTests
{
    private static readonly string?[] Levels = ["normal", "warn", "critical"];

    [Fact]
    public void TheLoadAverageIsANonNegativeNumberOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(PlatformModule.MachineLoad1() is >= 0);
    }

    [Fact]
    public void TheMemoryPressureIsOneOfTheThreeLevelsOnMacOsAndNullElsewhere()
    {
        var level = PlatformModule.MachineMemoryPressure();

        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(level, Levels);
        }
        else
        {
            Assert.Null(level);
        }
    }

    [Fact]
    public void CpuTimesAreReadOnMacOsAndLinuxWithBusyAtMostTotal()
    {
        var times = PlatformModule.MachineCpuTimes();

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            Assert.True(times is { Busy: >= 0 } t && t.Total > 0 && t.Busy <= t.Total);
        }
        else
        {
            Assert.Null(times);
        }
    }

    [Theory]
    [InlineData("cpu  100 20 30 400 50 6 7 8 9 10", 171L, 621L)]
    [InlineData("cpu 1 2 3 4", 6L, 10L)]
    [InlineData("cpu0 100 20 30 400 50 6 7 8", null, null)]
    [InlineData("intr 1 2 3 4 5", null, null)]
    [InlineData("cpu 1 2 x 4 5", null, null)]
    [InlineData("cpu 1 2 3", null, null)]
    [InlineData(null, null, null)]
    public void OnlyTheAggregateCpuLineOfProcStatIsReadWithIowaitAsIdle(string? line, long? busy, long? total) =>
        Assert.Equal(busy is null ? ((long Busy, long Total)?)null : (busy.Value, total!.Value), PlatformModule.ProcStatCpu(line));

    [Theory]
    [InlineData(1, "normal")]
    [InlineData(2, "warn")]
    [InlineData(4, "critical")]
    [InlineData(0, null)]
    [InlineData(3, null)]
    [InlineData(8, null)]
    public void OnlyTheThreeKernelLevelsHaveANameAndAnyOtherIsUnknown(int level, string? expected) =>
        Assert.Equal(expected, PlatformModule.PressureLevelName(level));
}
