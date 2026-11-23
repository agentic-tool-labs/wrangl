namespace Ah.Cli.Tests;

/// <summary>Role parameters on a brief (role-params RP-D8): a request to a role that declares params carries the block, team and item or none.</summary>
public sealed partial class WorkLogTests
{
    private void DeclareParams(string role)
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{\"version\":1,\"params\":{\"engine-profile\":\"/p/engine\"},\"roles\":{\"" + role + "\":{\"params\":[\"engine-profile\",\"asset-root\"]}}}");
    }

    /// <summary>A4 (RP-D7/D8): the set name shows its value and level, the unset one NOT SET.</summary>
    [Fact]
    public void ARequestToARoleWithParamsCarriesTheValueAndNotSet()
    {
        DeclareParams("implementor");
        Request("20261103-120000-aaaa");

        var shown = Msg("show", "20261103-120000-aaaa");

        Assert.True(shown.Code == 0, shown.Err);
        Assert.Contains("Role parameters for this repo (`ah roster param list` shows them):\n- engine-profile: \"/p/engine\" (global)\n- asset-root: NOT SET.", shown.Out, StringComparison.Ordinal);
    }

    /// <summary>Negative: a request to a role that declares none carries no block.</summary>
    [Fact]
    public void ARequestToARoleWithNoParamsCarriesNoBlock()
    {
        DeclareParams("architect");
        Request("20261103-120000-aaaa");

        Assert.DoesNotContain("Role parameters", Msg("show", "20261103-120000-aaaa").Out, StringComparison.Ordinal);
    }
}
