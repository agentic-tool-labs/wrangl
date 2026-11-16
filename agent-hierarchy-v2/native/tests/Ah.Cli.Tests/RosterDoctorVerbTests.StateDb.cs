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
}
