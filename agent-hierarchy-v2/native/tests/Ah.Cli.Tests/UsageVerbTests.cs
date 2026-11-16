using System.Globalization;

namespace Ah.Cli.Tests;

/// <summary><c>ah usage</c>: the newest tap sample's windows, read only, exit 0.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class UsageVerbTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Window(string name, double pct, TimeSpan resetsIn) =>
        string.Create(CultureInfo.InvariantCulture, $"\"{name}\":{{\"used_percentage\":{pct},\"resets_at\":{DateTimeOffset.UtcNow.Add(resetsIn).ToUnixTimeSeconds()}}}");

    private void Tap(string session, TimeSpan age, params string[] windows)
    {
        var dir = Path.Combine(_h.Sandbox.Home, ".claude", "ah2", "statusline");
        _ = Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, session + ".json");
        var limits = windows.Length == 0 ? string.Empty : ",\"rate_limits\":{" + string.Join(",", windows) + "}";
        File.WriteAllText(path, "{\"session_id\":\"" + session + "\"" + limits + "}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
    }

    [Theory]
    [InlineData(98.9)]
    [InlineData(100.0)]
    public void AnyPercentageIsListedAndExitsZeroWithNoMode(double pct)
    {
        Tap("s1", TimeSpan.FromMinutes(1), Window("five_hour", pct, TimeSpan.FromHours(2)));

        var r = _h.Run("ah", "usage");
        var json = _h.Run("ah", "usage", "--json");

        Assert.Equal(0, r.Code);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"5h: {pct:0.#}%"), r.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("throttle", r.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, json.Code);
        Assert.DoesNotContain("mode", json.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("threshold", json.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ASampleOlderThanFifteenMinutesIsMarkedStaleAndExitsZero()
    {
        Tap("s1", TimeSpan.FromMinutes(20), Window("five_hour", 99.5, TimeSpan.FromHours(2)));

        var r = _h.Run("ah", "usage");

        Assert.Equal(0, r.Code);
        Assert.Contains("stale", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAFreshSampleIsNotMarkedStale()
    {
        Tap("s1", TimeSpan.FromMinutes(14), Window("five_hour", 50, TimeSpan.FromHours(2)));

        Assert.DoesNotContain("stale", _h.Run("ah", "usage").Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSampleAndNoRateLimitsSaySoAndExitZero()
    {
        var none = _h.Run("ah", "usage");
        Assert.Equal(0, none.Code);
        Assert.Contains("no sample", none.Stdout, StringComparison.Ordinal);
        Tap("api-key", TimeSpan.FromMinutes(1));
        Assert.Equal(0, _h.Run("ah", "usage").Code);
    }

    [Fact]
    public void AWindowThatHasResetShowsZero()
    {
        Tap("s1", TimeSpan.FromMinutes(1), Window("five_hour", 99.5, TimeSpan.FromMinutes(-5)));

        var r = _h.Run("ah", "usage");

        Assert.Equal(0, r.Code);
        Assert.Contains("reset (0%)", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewestOfThreeFilesThatCarriesRateLimitsIsTheOneRead()
    {
        Tap("old", TimeSpan.FromMinutes(9), Window("five_hour", 99.9, TimeSpan.FromHours(1)));
        Tap("newest", TimeSpan.FromMinutes(2), Window("five_hour", 40, TimeSpan.FromHours(1)), Window("seven_day", 99.2, TimeSpan.FromDays(2)));
        Tap("newer-without-limits", TimeSpan.FromMinutes(1));

        var r = _h.Run("ah", "usage", "--json");

        Assert.Equal(0, r.Code);
        Assert.Contains("\"session\":\"newest\"", r.Stdout.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownArgumentIsRefused()
    {
        var r = _h.Run("ah", "usage", "--bogus");

        Assert.Equal(2, r.Code);
        Assert.Contains("ah usage", r.Stderr, StringComparison.Ordinal);
    }
}
