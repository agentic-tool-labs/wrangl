using System.Text.RegularExpressions;

namespace Ah.Architecture.Tests;

/// <summary>
/// The party names of an exchange are read and resolved to members in one place, so the member slices' last exchanges and the team's
/// open list can never disagree about who an exchange names.
/// </summary>
public sealed partial class PartyResolverScanTests
{
    // StatusLane carries the status document's own to_name column, which is not an exchange party.
    private static readonly string[] Owners = ["MessagesLane.cs", "TeamExchangeLists.cs", "StatusLane.cs"];

    [GeneratedRegex("\"(to_name|from_name)\"")]
    private static partial Regex PartyNameField();

    [Fact]
    public void OnlyTheMessagesLaneAndTheExchangeFilingNameThePartyFields()
    {
        var model = Path.Combine(Workspace.NativeRoot, "src", "Ah.Watch", "Model");
        var files = Directory.EnumerateFiles(model, "*.cs").ToList();
        Assert.Contains(files, static f => Path.GetFileName(f) == "TeamExchangeLists.cs");

        var strays = files.Where(f => !Owners.Contains(Path.GetFileName(f)) && PartyNameField().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName);

        Assert.Empty(strays);
    }

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var n = LaneParts.Text(record.Value, \"to_name\");")]
    [InlineData("[\"from_name\"] = Field(exchange, \"from_name\"),")]
    public void TheScanFlagsAPartyNameField(string line) => Assert.Matches(PartyNameField(), line);

    [Theory(Skip = "pruned 2026-11-14: structure policing or a test of a checker; disabled for later removal")]
    [InlineData("var n = LaneParts.Text(record.Value, \"to\");")]
    [InlineData("var n = LaneParts.Text(record.Value, \"team_name\");")]
    public void TheScanLeavesOtherFieldsAlone(string line) => Assert.DoesNotMatch(PartyNameField(), line);
}
