using System.Text.Json;

namespace Ah.Cli.Tests;

public sealed class WhereTests
{
    [Fact]
    public void WithNothingInstalledItExitsZeroReportsNullsAndWritesNothing()
    {
        using var harness = new Harness();
        var before = harness.Sandbox.Listing();

        var (code, stdout, stderr) = harness.Run("where");

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("plugin").ValueKind);
        Assert.False(root.GetProperty("compatible").GetBoolean());
        Assert.Equal(before, harness.Sandbox.Listing());
        Assert.False(harness.Exec.Called);
        Assert.Empty(harness.Runner.Calls);
    }

    [Fact]
    public void ItReportsTheBinaryThePluginAndTheLoginState()
    {
        using var harness = new Harness();
        harness.Sandbox.InstallPlugin(version: "1.2.3", contract: "0");
        harness.Variables["AH_PLUGIN_ROOT"] = harness.Sandbox.PluginRoot;
        harness.Variables["AH_TEST"] = "1";

        var (code, stdout, _) = harness.Run("where");

        Assert.Equal(0, code);
        Assert.EndsWith("\n", stdout);
        Assert.Single(stdout.TrimEnd('\n').Split('\n'));
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal(["binary", "plugin", "compatible", "login", "ui", "home"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["path", "version", "contract", "plugin_name"],
            root.GetProperty("binary").EnumerateObject().Select(p => p.Name));
        Assert.Equal("0.0.0-dev", root.GetProperty("binary").GetProperty("version").GetString());
        Assert.Equal(0, root.GetProperty("binary").GetProperty("contract").GetInt32());
        Assert.Equal("wrangl", root.GetProperty("binary").GetProperty("plugin_name").GetString());
        Assert.Equal("1.2.3", root.GetProperty("plugin").GetProperty("version").GetString());
        Assert.Equal("env", root.GetProperty("plugin").GetProperty("source").GetString());
        Assert.True(root.GetProperty("compatible").GetBoolean());
    }

    [Fact]
    public void ADifferentContractIsNotCompatible()
    {
        using var harness = new Harness();
        harness.Sandbox.InstallPlugin(contract: "7");
        harness.Variables["AH_PLUGIN_ROOT"] = harness.Sandbox.PluginRoot;
        harness.Variables["AH_TEST"] = "1";

        var (code, stdout, _) = harness.Run("where");

        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(stdout);
        Assert.False(document.RootElement.GetProperty("compatible").GetBoolean());
    }

    [Fact]
    public void ThePluginWithoutAContractManifestIsNotCompatible()
    {
        using var harness = new Harness();
        harness.Sandbox.InstallPlugin();
        File.Delete(Path.Combine(harness.Sandbox.PluginRoot, "native", "cutover.json"));
        harness.Variables["AH_PLUGIN_ROOT"] = harness.Sandbox.PluginRoot;
        harness.Variables["AH_TEST"] = "1";

        var (_, stdout, _) = harness.Run("where");

        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("plugin").GetProperty("contract").ValueKind);
        Assert.False(document.RootElement.GetProperty("compatible").GetBoolean());
    }

    [Fact]
    public void UiIsLiveOnlyWhileUiJsonNamesAPortAndFallsBackToTheRememberedPort()
    {
        using var harness = new Harness();
        var run = Path.Combine(harness.Sandbox.AhHome, "run");
        Directory.CreateDirectory(run);
        (int? Port, bool Live) Ui()
        {
            var (code, stdout, _) = harness.Run("where");
            Assert.Equal(0, code);
            using var document = JsonDocument.Parse(stdout);
            var ui = document.RootElement.GetProperty("ui");
            return (ui.GetProperty("port").ValueKind == JsonValueKind.Null ? null : ui.GetProperty("port").GetInt32(), ui.GetProperty("live").GetBoolean());
        }

        Assert.Equal((null, false), Ui());
        File.WriteAllText(Path.Combine(run, "ui-port"), "41234\n");
        Assert.Equal((41234, false), Ui());
        File.WriteAllText(Path.Combine(run, "ui.json"), """{"v":1,"port":41999,"lease_pid":7,"started":"x"}""");
        Assert.Equal((41999, true), Ui());

        // the nearest inputs that must not read live: a ui.json with no usable port
        File.WriteAllText(Path.Combine(run, "ui.json"), """{"v":1,"port":0}""");
        Assert.Equal((41234, false), Ui());
        File.WriteAllText(Path.Combine(run, "ui.json"), "{not json");
        Assert.Equal((41234, false), Ui());
        File.WriteAllText(Path.Combine(run, "ui-port"), "garbage");
        Assert.Equal((null, false), Ui());
    }

    [Fact]
    public void ItNamesAhsHomeAsAhResolvesIt()
    {
        using var harness = new Harness();

        var (code, stdout, _) = harness.Run("where");
        var (plainCode, plain, _) = harness.Run("where", "--plain");

        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(harness.Sandbox.AhHome, document.RootElement.GetProperty("home").GetString());
        Assert.Equal(0, plainCode);
        Assert.Contains($"home: {harness.Sandbox.AhHome}\n", plain);
    }

    [Fact]
    public void AhWhereIsAhdWhere()
    {
        // ah2-122 D2: the herdr mod asks `ah where`; it was an unknown command
        using var harness = new Harness();
        var before = harness.Sandbox.Listing();

        var (code, stdout, stderr) = harness.Run("ah", "where");
        var (ahdCode, ahdStdout, _) = harness.Run("where");

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(harness.Sandbox.AhHome, document.RootElement.GetProperty("home").GetString());
        Assert.Equal((0, ahdStdout), (ahdCode, stdout));
        Assert.Equal(before, harness.Sandbox.Listing());

        // the nearest verb that must not match stays unknown
        var (nearCode, _, nearStderr) = harness.Run("ah", "wher");
        Assert.Equal(2, nearCode);
        Assert.Contains("ah: unknown command 'wher'", nearStderr, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainIsText()
    {
        using var harness = new Harness();

        var (code, stdout, _) = harness.Run("where", "--plain");

        Assert.Equal(0, code);
        Assert.StartsWith("binary.path: ", stdout);
        Assert.Contains("plugin: none\n", stdout);
        Assert.Contains("login: ", stdout);
    }

    [Fact]
    public void JsonIsAnUnknownFlag()
    {
        using var harness = new Harness();

        var (code, stdout, stderr) = harness.Run("where", "--json");

        Assert.Equal(2, code);
        Assert.Empty(stdout);
        Assert.Contains("--json", stderr);
    }
}
