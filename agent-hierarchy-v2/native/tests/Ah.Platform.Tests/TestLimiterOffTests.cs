namespace Ah.Platform.Tests;

/// <summary>The test limiter's off switch is read outside test mode too: <c>AH_TEST_SLOTS=0</c> or <c>AH_TEST_TOKENS=0</c>.</summary>
public sealed class TestLimiterOffTests
{
    [Theory]
    [InlineData("AH_TEST_SLOTS", "0", true)]
    [InlineData("AH_TEST_TOKENS", "0", true)]
    [InlineData("AH_TEST_SLOTS", "1", false)]
    [InlineData("AH_TEST_TOKENS", "4", false)]
    [InlineData("AH_TEST_SLOTS", "", false)]
    [InlineData("AH_SLOTS_DIR", "0", false)]
    public void TheLimiterIsOffOnlyForAZeroSlotsOrTokensOutsideTestMode(string name, string value, bool off)
    {
        var env = PlatformModule.CreateEnvironment(n => n == name ? value : null, null, new FileReader(), 501, () => "/nonexistent-home");

        Assert.False(env.TestMode);
        Assert.Equal(off, env.TestLimiterOff);
    }
}
