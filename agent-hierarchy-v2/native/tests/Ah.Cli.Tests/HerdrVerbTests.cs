using Ah.Abstractions;
using Ah.Cli.Verbs;

namespace Ah.Cli.Tests;

/// <summary><c>ah herdr install [--uninstall]</c>: copies into <c>$HERDR_APP_HOME</c>, marks ownership, never touches an unowned target.</summary>
public sealed class HerdrVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private const string DeskLayout = """{"id":"root","children":[{"id":"main"},{"id":"ah2-panel","children":[{"id":"ah2-panel-body"}]}]}""";
    private const string StaleLayout = """{"id":"root","children":[{"id":"main"}]}""";
    private const string Missing = "missing the Desk column (ah2-panel); run 'ah herdr install --replace-layout'";

    private string _shippedLayout = "{}";

    private string HerdrHome => Path.Combine(_sandbox.Home, "herdr-home");

    private string Layout => Path.Combine(HerdrHome, "layouts", "wrangl.json");

    private (int Code, string Err) Run(params string[] args) => Run(null, args);

    private (int Code, string Err) Run(Func<IAhEnvironment, IAhEnvironment>? wrap, params string[] args)
    {
        var source = Path.Combine(_sandbox.PluginRoot, "herdr-desktop");
        Directory.CreateDirectory(Path.Combine(source, "mods", "ah2", "lib"));
        File.WriteAllText(Path.Combine(source, "mods", "ah2", "main.js"), "// test placeholder");
        File.WriteAllText(Path.Combine(source, "mods", "ah2", "lib", "a.js"), "// test placeholder");
        Directory.CreateDirectory(Path.Combine(source, "layouts"));
        File.WriteAllText(Path.Combine(source, "layouts", "wrangl.json"), _shippedLayout);
        var variables = new Dictionary<string, string?> { ["HOME"] = _sandbox.Home, ["HERDR_APP_HOME"] = HerdrHome };
        IAhEnvironment env = wrap is null
            ? new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "9.9.9", "1"))
            : wrap(new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, null));
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501);
        var stderr = new StringWriter();
        return (HerdrVerb.Run(context, args, new StringWriter(), stderr), stderr.ToString());
    }

    [Fact]
    public void AnEnvironmentWithNoPluginFindsTheInstalledPluginThroughTheCliHost()
    {
        Assert.NotEqual(0, Run(Ah.Cli.Composition.CliHost.WithPlugin, "install").Code);

        _sandbox.Write("plugin/bin/ah-hook", "//\n");
        _sandbox.Write("home/.claude/plugins/installed_plugins.json", $"{{\"plugins\":{{\"wrangl@m\":[{{\"installPath\":\"{_sandbox.PluginRoot}\",\"version\":\"1.0.0\",\"scope\":\"user\"}}]}}}}");

        Assert.Equal(0, Run(Ah.Cli.Composition.CliHost.WithPlugin, "install").Code);
        Assert.True(File.Exists(Path.Combine(HerdrHome, "layouts", "wrangl.json")));
    }

    [Fact]
    public void InstallCopiesAndMarksThenReinstallUpgradesThenUninstallRemovesBoth()
    {
        Assert.Equal(0, Run("install").Code);
        var marker = File.ReadAllText(Path.Combine(HerdrHome, "mods", "ah2", HerdrVerb.Marker));
        Assert.StartsWith("9.9.9\n", marker, StringComparison.Ordinal);
        Assert.Contains("mods/ah2/lib/a.js sha256 ", marker, StringComparison.Ordinal);
        Assert.Contains("layouts/wrangl.json sha256 ", marker, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(HerdrHome, "mods", "ah2", "lib", "a.js")));
        Assert.True(File.Exists(Path.Combine(HerdrHome, "layouts", "wrangl.json")));

        Assert.Equal(0, Run("install").Code);

        Assert.Equal(0, Run("install", "--uninstall").Code);
        Assert.False(Directory.Exists(Path.Combine(HerdrHome, "mods", "ah2")));
        Assert.False(File.Exists(Path.Combine(HerdrHome, "layouts", "wrangl.json")));
    }

    [Fact]
    public void AnEditedOwnedLayoutSurvivesReinstallAndUninstallWhileTheModIsStillHandled()
    {
        Assert.Equal(0, Run("install").Code);
        var layout = Path.Combine(HerdrHome, "layouts", "wrangl.json");
        File.WriteAllText(layout, "jim's edit");

        var reinstall = Run("install");
        Assert.Equal(0, reinstall.Code);
        Assert.Contains("modified after install and was kept", reinstall.Err, StringComparison.Ordinal);
        Assert.Equal("jim's edit", File.ReadAllText(layout));

        Assert.Equal(0, Run("install", "--uninstall").Code);
        Assert.Equal("jim's edit", File.ReadAllText(layout));
        Assert.False(Directory.Exists(Path.Combine(HerdrHome, "mods", "ah2")));
    }

    [Fact]
    public void AnEditedModFileAndAUserFileSurviveReinstallAndUninstall()
    {
        Assert.Equal(0, Run("install").Code);
        var edited = Path.Combine(HerdrHome, "mods", "ah2", "main.js");
        var added = Path.Combine(HerdrHome, "mods", "ah2", "mine.js");
        File.WriteAllText(edited, "jim's edit");
        File.WriteAllText(added, "jim's file");

        var reinstall = Run("install");
        Assert.Equal(0, reinstall.Code);
        Assert.Contains(edited, reinstall.Err, StringComparison.Ordinal);
        Assert.Contains(added, reinstall.Err, StringComparison.Ordinal);

        Assert.Equal(0, Run("install", "--uninstall").Code);
        Assert.Equal("jim's edit", File.ReadAllText(edited));
        Assert.Equal("jim's file", File.ReadAllText(added));
        Assert.False(File.Exists(Path.Combine(HerdrHome, "mods", "ah2", "lib", "a.js")));
        Assert.False(File.Exists(Path.Combine(HerdrHome, "layouts", "wrangl.json")));
    }

    /// <summary>The layout was layouts/ah2.json before the rename: an install removes a recorded, unedited one and keeps an edited one.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInstallRemovesTheOldNameLayoutUnlessItWasEdited(bool edited)
    {
        Assert.Equal(0, Run("install").Code);
        var marker = Path.Combine(HerdrHome, "mods", "ah2", HerdrVerb.Marker);
        var old = Path.Combine(HerdrHome, "layouts", "ah2.json");
        File.Move(Layout, old); // a home installed before the rename: the layout under its old name, recorded under it
        File.WriteAllText(marker, File.ReadAllText(marker).Replace("layouts/wrangl.json sha256", "layouts/ah2.json sha256", StringComparison.Ordinal));
        if (edited)
        {
            File.WriteAllText(old, "jim's edit");
        }

        var install = Run("install");

        Assert.Equal(0, install.Code);
        Assert.True(File.Exists(Layout));
        Assert.Equal(edited, File.Exists(old));
        Assert.Equal(edited, install.Err.Contains("your edited ah2.json stays", StringComparison.Ordinal));
        Assert.Equal(edited, File.ReadAllText(marker).Contains("layouts/ah2.json sha256", StringComparison.Ordinal));

        Assert.Equal(0, Run("install", "--uninstall").Code);
        Assert.False(File.Exists(Layout));
        Assert.Equal(edited, File.Exists(old));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("9.9.9\n")]
    [InlineData("9.9.9\nmods/ah2/main.js sha256 abc\n")]
    [InlineData("9.9.9\nmods/ah2/a\\b.js sha256 0000000000000000000000000000000000000000000000000000000000000000\n")]
    [InlineData("9.9.9\nmods/ah2/c:.js sha256 0000000000000000000000000000000000000000000000000000000000000000\n")]
    public void AMarkerThatDoesNotParseChangesNothing(string content)
    {
        Assert.Equal(0, Run("install").Code);
        var marker = Path.Combine(HerdrHome, "mods", "ah2", HerdrVerb.Marker);
        File.WriteAllText(marker, content);
        File.WriteAllText(Path.Combine(HerdrHome, "mods", "ah2", "main.js"), "old");
        string Snapshot() => string.Join("|", Directory.GetFiles(HerdrHome, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => f + "=" + File.ReadAllText(f)));
        var before = Snapshot();

        foreach (var args in new[] { new[] { "install" }, ["install", "--uninstall"] })
        {
            var run = Run(args);
            Assert.Equal(1, run.Code);
            Assert.Contains("is not one ah wrote; nothing changed", run.Err, StringComparison.Ordinal);
            Assert.Equal(before, Snapshot());
        }
    }

    [Fact]
    public void ALeftoverMarkerTmpIsNeitherReportedNorKeptByUninstall()
    {
        Assert.Equal(0, Run("install").Code);
        File.WriteAllText(Path.Combine(HerdrHome, "mods", "ah2", HerdrVerb.MarkerTmp), "half-written");

        var reinstall = Run("install");
        Assert.Equal(0, reinstall.Code);
        Assert.DoesNotContain("kept", reinstall.Err, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(HerdrHome, "mods", "ah2", HerdrVerb.MarkerTmp), "half-written");
        var uninstall = Run("install", "--uninstall");
        Assert.DoesNotContain("kept", uninstall.Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(HerdrHome, "mods", "ah2")));
    }

    [Fact]
    public void ATargetEqualToTheShippedFileIsOursEvenWhenTheMarkerRecordsAnotherHash()
    {
        Assert.Equal(0, Run("install").Code);
        var marker = Path.Combine(HerdrHome, "mods", "ah2", HerdrVerb.Marker);
        File.WriteAllText(marker, "9.9.9\nmods/ah2/main.js sha256 " + new string('0', 64) + "\n");

        var reinstall = Run("install");
        Assert.Equal(0, reinstall.Code);
        Assert.DoesNotContain("main.js", reinstall.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('0', 64), File.ReadAllText(marker), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnownedLayoutIsNeitherOverwrittenNorUninstalled()
    {
        Directory.CreateDirectory(Path.Combine(HerdrHome, "layouts"));
        var layout = Path.Combine(HerdrHome, "layouts", "wrangl.json");
        File.WriteAllText(layout, "jim's");

        var install = Run("install");
        Assert.Equal(1, install.Code);
        Assert.Contains("not installed by ah", install.Err, StringComparison.Ordinal);
        Assert.Equal(1, Run("install", "--uninstall").Code);
        Assert.Equal("jim's", File.ReadAllText(layout));
        Assert.False(Directory.Exists(Path.Combine(HerdrHome, "mods", "ah2")));
    }

    [Fact]
    public void AStaleEditedLayoutIsKeptByteForByteWithTheDeskWarning()
    {
        _shippedLayout = DeskLayout;
        Assert.Equal(0, Run("install").Code);
        File.WriteAllText(Layout, StaleLayout);

        var reinstall = Run("install");
        Assert.Equal(0, reinstall.Code);
        Assert.Contains(Missing, reinstall.Err, StringComparison.Ordinal);
        Assert.Equal(StaleLayout, File.ReadAllText(Layout));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Layout)!, "wrangl.json.bak-*"));
    }

    [Fact]
    public void ReplaceLayoutKeepsABackupWritesTheShippedLayoutAndRecordsItsHash()
    {
        _shippedLayout = DeskLayout;
        Assert.Equal(0, Run("install").Code);
        File.WriteAllText(Layout, StaleLayout);

        var replace = Run("install", "--replace-layout");
        Assert.Equal(0, replace.Code);
        Assert.DoesNotContain("Desk column", replace.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("wrangl.json was modified", replace.Err, StringComparison.Ordinal);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Layout)!, "wrangl.json.bak-*"));
        Assert.Equal(StaleLayout, File.ReadAllText(backup));
        Assert.Contains("\"ah2-panel\"", File.ReadAllText(Layout), StringComparison.Ordinal);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Layout)));
        Assert.Contains($"layouts/wrangl.json sha256 {hash}\n", File.ReadAllText(Path.Combine(HerdrHome, "mods", "ah2", HerdrVerb.Marker)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditedLayoutThatHasTheDeskIdsGetsNoWarning()
    {
        _shippedLayout = DeskLayout;
        Assert.Equal(0, Run("install").Code);
        const string Edited = """{"id":"root","children":[{"id":"ah2-panel","hidden":true,"children":[{"id":"ah2-panel-body"}]}]}""";
        File.WriteAllText(Layout, Edited);

        var reinstall = Run("install");
        Assert.Equal(0, reinstall.Code);
        Assert.Contains("was kept", reinstall.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("Desk column", reinstall.Err, StringComparison.Ordinal);
        Assert.Equal(Edited, File.ReadAllText(Layout));
    }
}
