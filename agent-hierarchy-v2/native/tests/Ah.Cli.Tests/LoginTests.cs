using Ah.Abstractions;
using Ah.Cli.Login;

namespace Ah.Cli.Tests;

public sealed class LoginTests
{
    private static string PlistPath(Sandbox sandbox) =>
        Path.Combine(sandbox.Home, "Library", "LaunchAgents", "io.github.agent-tools.ahd.plist");

    private static string Golden(Harness harness) => string.Join(
        "\n",
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
        "<plist version=\"1.0\">",
        "<dict>",
        "  <key>Label</key>",
        "  <string>io.github.agent-tools.ahd</string>",
        "  <key>ProgramArguments</key>",
        "  <array>",
        $"    <string>{Real(harness.Context.ProcessPath)}</string>",
        "    <string>login</string>",
        "    <string>run</string>",
        "  </array>",
        "  <key>RunAtLoad</key>",
        "  <true/>",
        "  <key>StandardOutPath</key>",
        $"  <string>{harness.Sandbox.AhHome}/logs/login.log</string>",
        "  <key>StandardErrorPath</key>",
        $"  <string>{harness.Sandbox.AhHome}/logs/login.log</string>",
        "  <key>EnvironmentVariables</key>",
        "  <dict>",
        "    <key>HOME</key>",
        $"    <string>{harness.Sandbox.Home}</string>",
        "    <key>PATH</key>",
        $"    <string>{harness.Sandbox.Bin}</string>",
        "  </dict>",
        "</dict>",
        "</plist>",
        string.Empty);

    private static string Real(string path) => File.ResolveLinkTarget(path, true)?.FullName ?? path;

    /// <summary>A launchctl whose <c>print</c> answers 0 once <c>bootstrap</c> has run and until <c>bootout</c> does.</summary>
    private static void LaunchctlLoadsOnBootstrap(Harness harness)
    {
        var loaded = false;
        harness.Runner.Exit = argv =>
        {
            switch (argv[1])
            {
                case "bootstrap":
                    loaded = true;
                    return 0;
                case "bootout":
                    var was = loaded;
                    loaded = false;
                    return was ? 0 : 3;
                case "print":
                    return loaded ? 0 : 113;
                default:
                    return 0;
            }
        };
    }

    private static LaunchdItem Launchd(Harness harness) => new(harness.Context);

    [Fact]
    public void OnWritesTheGoldenPlistBootstrapsAndWritesTheStayFile()
    {
        using var harness = new Harness();
        LaunchctlLoadsOnBootstrap(harness);

        var (code, stdout, stderr) = harness.Run("login", "on");

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.Equal(Golden(harness), File.ReadAllText(PlistPath(harness.Sandbox)));
        Assert.Equal(
            ["launchctl", "bootstrap", "gui/501", PlistPath(harness.Sandbox)],
            harness.Runner.Calls.Single(c => c[1] == "bootstrap"));
        var stay = File.ReadAllText(Path.Combine(harness.Sandbox.AhHome, "login.json"));
        Assert.Matches(@"^\{""stay"":true,""since"":""\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z""\}\n$", stay);
        Assert.Contains("\"login\":\"on\"", stdout);
        Assert.Contains("\"changed\":true", stdout);
    }

    [Fact]
    public void ASecondOnRecordsNoSecondBootstrapAndSaysAlreadyOn()
    {
        using var harness = new Harness();
        LaunchctlLoadsOnBootstrap(harness);
        harness.Run("login", "on");
        harness.Runner.Calls.Clear();

        var (code, stdout, _) = harness.Run("login", "on", "--plain");

        Assert.Equal(0, code);
        Assert.Contains("already on", stdout);
        Assert.DoesNotContain(harness.Runner.Calls, c => c[1] is "bootstrap" or "bootout");
    }

    [Fact]
    public void ALoadedJobWithDifferentBytesIsBootedOutThenBootstrapped()
    {
        using var harness = new Harness();
        LaunchctlLoadsOnBootstrap(harness);
        harness.Run("login", "on");
        harness.Variables["PATH"] = harness.Sandbox.Bin + ":/usr/local/bin";
        harness.Runner.Calls.Clear();

        var (code, _, _) = harness.Run("login", "on");

        Assert.Equal(0, code);
        Assert.Equal(["bootout", "bootstrap"], harness.Runner.Calls.Where(c => c[1] is "bootstrap" or "bootout").Select(c => c[1]));
    }

    [Fact]
    public void OffBootsOutAndRemovesThePlistAndTheStayFileThenSaysAlreadyOff()
    {
        using var harness = new Harness();
        LaunchctlLoadsOnBootstrap(harness);
        harness.Run("login", "on");
        harness.Runner.Calls.Clear();

        var (code, _, _) = harness.Run("login", "off");

        Assert.Equal(0, code);
        Assert.Contains(harness.Runner.Calls, c => c.SequenceEqual(["launchctl", "bootout", "gui/501/io.github.agent-tools.ahd"]));
        Assert.False(File.Exists(PlistPath(harness.Sandbox)));
        Assert.False(File.Exists(Path.Combine(harness.Sandbox.AhHome, "login.json")));

        harness.Runner.Calls.Clear();
        var (again, stdout, _) = harness.Run("login", "off", "--plain");

        Assert.Equal(0, again);
        Assert.Contains("already off", stdout);
        Assert.DoesNotContain(harness.Runner.Calls, c => c[1] == "bootout");
    }

    [Fact]
    public void APlistAtOurPathWithAForeignLabelIsRefusedAndUntouched()
    {
        using var harness = new Harness();
        var foreign = "<plist><dict><key>Label</key><string>com.example.other</string></dict></plist>";
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath(harness.Sandbox))!);
        File.WriteAllText(PlistPath(harness.Sandbox), foreign);

        var off = harness.Run("login", "off");
        var on = harness.Run("login", "on");

        Assert.Equal(1, off.Code);
        Assert.Equal(1, on.Code);
        Assert.Equal(foreign, File.ReadAllText(PlistPath(harness.Sandbox)));
        Assert.DoesNotContain(harness.Runner.Calls, c => c[1] is "bootstrap" or "bootout");
    }

    [Fact]
    public void ARedirectedHomeIsRefusedWithNothingWritten()
    {
        using var harness = new Harness(passwdHome: "/Users/someone-else");
        var before = harness.Sandbox.Listing();

        var (code, _, stderr) = harness.Run("login", "on");

        Assert.Equal(2, code);
        Assert.Contains("passwd home", stderr);
        Assert.Equal(before, harness.Sandbox.Listing());
        Assert.Empty(harness.Runner.Calls);
    }

    [Fact]
    public void TestModeWithARealLaunchctlIsStillRefused()
    {
        using var harness = new Harness(
            passwdHome: "/Users/someone-else",
            extra: new() { ["AH_TEST"] = "1" });
        var elsewhere = harness.Sandbox.Executable("real/launchctl");
        harness.Variables["PATH"] = Path.GetDirectoryName(elsewhere)!;
        harness.Variables["AH_TEST_FAKE_BIN"] = harness.Sandbox.Bin;

        var (code, _, _) = harness.Run("login", "on");

        Assert.Equal(2, code);
    }

    [Fact]
    public void TestModeWithTheFakeLaunchctlInTheListedDirectoryIsAllowed()
    {
        using var harness = new Harness(
            passwdHome: "/Users/someone-else",
            extra: new() { ["AH_TEST"] = "1" });
        harness.Sandbox.Executable("bin/launchctl");
        harness.Variables["AH_TEST_FAKE_BIN"] = harness.Sandbox.Bin;
        LaunchctlLoadsOnBootstrap(harness);

        var (code, _, _) = harness.Run("login", "on");

        Assert.Equal(0, code);
    }

    [Fact]
    public void AFakeBinListWithoutTestModeDoesNotLiftTheGuard()
    {
        using var harness = new Harness(passwdHome: "/Users/someone-else");
        harness.Sandbox.Executable("bin/launchctl");
        harness.Variables["AH_TEST_FAKE_BIN"] = harness.Sandbox.Bin;

        var (code, _, _) = harness.Run("login", "on");

        Assert.Equal(2, code);
    }

    [Fact]
    public void ANonDefaultAhHomeIsRefused()
    {
        using var harness = new Harness();
        var other = Path.Combine(harness.Sandbox.Root, "other-ah-home");
        using var shifted = new Harness(extra: new() { ["AH_HOME"] = other });

        var (code, _, stderr) = shifted.Run("login", "on");

        Assert.Equal(2, code);
        Assert.Contains("default AH_HOME only", stderr);
        Assert.Empty(shifted.Runner.Calls);
    }

    [Fact]
    public void DryRunPrintsThePlanAndChangesNothing()
    {
        using var harness = new Harness();
        var before = harness.Sandbox.Listing();

        var (code, stdout, _) = harness.Run("login", "on", "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("\"dry_run\":true", stdout);
        Assert.Contains("bootstrap gui/501", stdout);
        Assert.Equal(before, harness.Sandbox.Listing());
        Assert.DoesNotContain(harness.Runner.Calls, c => c[1] == "bootstrap");
    }

    [Fact]
    public void StatusIsBrokenWhenThePlistNamesAMissingBinary()
    {
        using var harness = new Harness();
        LaunchctlLoadsOnBootstrap(harness);
        harness.Run("login", "on");
        File.Delete(harness.Context.ProcessPath);

        var (code, stdout, _) = harness.Run("login", "status");

        Assert.Equal(0, code);
        Assert.Contains("\"login\":\"broken\"", stdout);
        Assert.Contains("does not exist", stdout);
    }

    [Fact]
    public void StatusIsOffWithNothingThere()
    {
        using var harness = new Harness();

        var (_, stdout, _) = harness.Run("login", "status");

        Assert.Contains("\"login\":\"off\"", stdout);
    }

    private static ModuleRegistry[] RegistryWithStart(List<string[]> calls, int exit) =>
    [
        new(
            [new VerbRegistration("start", (args, _, _) => { calls.Add(args); return exit; })],
            [], [], [], []),
    ];

    [Fact]
    public void RunStartsTheDaemonThroughTheStartVerb()
    {
        using var harness = new Harness();
        var calls = new List<string[]>();

        var (code, _, _) = harness.Run(RegistryWithStart(calls, 7), "login", "run");

        Assert.Equal(7, code);
        Assert.Equal([Array.Empty<string>()], calls);
        Assert.Empty(harness.Exec.Argv);
    }

    [Fact]
    public void RunWithNoStartVerbIsNotImplementedNatively()
    {
        using var harness = new Harness();

        var (code, _, stderr) = harness.Run("login", "run");

        Assert.Equal(70, code);
        Assert.Contains("start is not implemented natively", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyRunStartsTheDaemon()
    {
        using var harness = new Harness();
        var calls = new List<string[]>();

        harness.Run(RegistryWithStart(calls, 0), "login", "status");

        Assert.Empty(calls);
    }

    [Fact]
    public void AnUnknownLoginActionIsAUsageError()
    {
        using var harness = new Harness();

        var (code, _, stderr) = harness.Run("login", "toggle");

        Assert.Equal(2, code);
        Assert.Contains("usage:", stderr);
    }

    [Fact]
    public void DryRunIsRefusedWhereItMeansNothing()
    {
        using var harness = new Harness();

        var (code, _, _) = harness.Run("login", "status", "--dry-run");

        Assert.Equal(2, code);
    }

    [Fact]
    public void SystemdUnitCarriesTheMarkerAndStartsWithEnableNow()
    {
        using var harness = new Harness();
        var item = new SystemdItem(harness.Context);

        var unit = System.Text.Encoding.UTF8.GetString(item.Render("/opt/ah/bin/ahd"));

        Assert.StartsWith(SystemdItem.Marker + "\n[Unit]\n", unit);
        Assert.Contains("Type=oneshot\nExecStart=/opt/ah/bin/ahd login run\n", unit);
        Assert.EndsWith("[Install]\nWantedBy=default.target\n", unit);
        Assert.True(item.OwnedBy(unit));
        Assert.False(item.OwnedBy("[Unit]\nDescription=someone else's\n"));
        Assert.Equal(
            [["systemctl", "--user", "daemon-reload"], ["systemctl", "--user", "enable", "--now", "ahd-login.service"]],
            item.StartCommands(loaded: false, unitChanged: true));
        Assert.Empty(item.StartCommands(loaded: true, unitChanged: false));
    }

    [Fact]
    public void WithoutASystemdUserSessionOnIsRefusedWithTheShellProfileHint()
    {
        using var harness = new Harness();
        harness.Runner.Exit = _ => 1;
        var item = new SystemdItem(harness.Context);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = Verbs.LoginVerb.Run(harness.Context, ["on"], stdout, stderr, item);

        Assert.Equal(1, code);
        Assert.Contains("no systemd --user session; start ahd from your shell profile with: ahd start", stderr.ToString());
        Assert.False(File.Exists(item.UnitPath));
    }

    [Fact]
    public void SystemdOffRemovesOnlyAUnitWithTheMarker()
    {
        using var harness = new Harness();
        var item = new SystemdItem(harness.Context);
        Directory.CreateDirectory(Path.GetDirectoryName(item.UnitPath)!);
        File.WriteAllText(item.UnitPath, "[Unit]\nDescription=not ours\n");
        var stderr = new StringWriter();

        var code = Verbs.LoginVerb.Run(harness.Context, ["off"], new StringWriter(), stderr, item);

        Assert.Equal(1, code);
        Assert.True(File.Exists(item.UnitPath));

        File.WriteAllBytes(item.UnitPath, item.Render("/opt/ah/bin/ahd"));
        code = Verbs.LoginVerb.Run(harness.Context, ["off"], new StringWriter(), stderr, item);

        Assert.Equal(0, code);
        Assert.False(File.Exists(item.UnitPath));
    }

    [Fact]
    public void LaunchdItemKnowsItsOwnPlistByLabel()
    {
        using var harness = new Harness();
        var item = Launchd(harness);

        Assert.True(item.OwnedBy(System.Text.Encoding.UTF8.GetString(item.Render("/x/ahd"))));
        Assert.False(item.OwnedBy("<key>Label</key><string>io.github.agent-tools.ahd2</string>"));
    }
}
