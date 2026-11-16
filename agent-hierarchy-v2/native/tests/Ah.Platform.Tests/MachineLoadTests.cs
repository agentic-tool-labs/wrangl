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
