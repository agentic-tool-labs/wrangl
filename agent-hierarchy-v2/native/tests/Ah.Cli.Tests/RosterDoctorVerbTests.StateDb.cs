using Ah.Core.Json;

namespace Ah.Cli.Tests;

public sealed partial class RosterDoctorVerbTests
{
    private JsonObject StateDbRow(string level, string detail)
    {
        _doctorOverride = new FixedDoctor([], stateDb: (level, detail));
        var (_, stdout, _) = Roster(null, "doctor");
        return Row(Parse(stdout), "state-db");
    }

    [Theory]
    [InlineData("ok", "ok")]
    [InlineData("warn", "warn")]
    [InlineData("error", "red")]
    public void TheStateDbRowCarriesTheDatabaseHealthAndAnErrorIsRed(string level, string status)
    {
        var row = StateDbRow(level, "schema v1, integrity ok, 3 backup(s), newest 12m old, 40 KiB");

        Assert.Equal(status, row["status"].AsString());
        Assert.Equal("schema v1, integrity ok, 3 backup(s), newest 12m old, 40 KiB", row["detail"].AsString());
    }

    [Fact]
    public void FixWithAnUnreadableStampAddsAFailedEntryAndKeepsTheExitCode()
    {
        Directory.CreateDirectory(_sandbox.AhHome);
        File.WriteAllText(Path.Combine(_sandbox.AhHome, "install.json"), "not json");

        var (code, stdout, stderr) = Roster(null, "doctor", "--fix");

        Assert.True(code == 0, stderr);
        Assert.Contains(Parse(stdout)["fix"].AsArray(), x => x.AsString().StartsWith("ahd-build: fix failed: ", StringComparison.Ordinal));
    }
}
