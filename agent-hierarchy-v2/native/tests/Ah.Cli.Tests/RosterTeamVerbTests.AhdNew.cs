using System.Globalization;
using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary><c>ahd new --tab</c> over the herdr fake: the focused tab, the herdr run step in its root pane, and each refusal.</summary>
public sealed partial class RosterTeamVerbTests
{
    private (int Code, string Stdout, string Stderr) AhdNew(params string[] args)
    {
        ComposeServices();
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "new");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private HerdrFake InsideHerdrWithClaude()
    {
        var herdr = OnHerdr(string.Empty);
        _sandbox.Executable("bin/claude");
        return herdr;
    }

    private string[] TabCreateCall() => _runner.Calls.Single(argv => argv.Length > 2 && argv[0] == "herdr" && argv[1] == "tab" && argv[2] == "create");

    [Fact]
    public void AhdNewTabInsideHerdrOpensAFocusedTabAndStartsTheOrchestratorInItsRootPane()
    {
        InsideHerdrWithClaude();
        var team = _sandbox.TeamText(TeamPath("proj"));

        var (code, _, stderr) = AhdNew("jim", "--tab");

        Assert.True(code == 0, stderr);
        var create = TabCreateCall();
        Assert.Equal("jim", create[Array.IndexOf(create, "--label") + 1]);
        Assert.Equal(Project, create[Array.IndexOf(create, "--cwd") + 1]);
        Assert.DoesNotContain("--no-focus", create);
        var start = _runner.Calls.Single(argv => argv[0] == "/bin/sh")[^1];
        Assert.Contains("herdr agent start jim --kind claude --pane r1 -- --agent wrangl:orchestrator --name jim --permission-mode auto", start, StringComparison.Ordinal);
        Assert.DoesNotContain("--settings", start, StringComparison.Ordinal);
        Assert.Equal(team, _sandbox.TeamText(TeamPath("proj")));
    }

    [Fact]
    public void AhdNewTabCarriesTheTeamPromptLastOnTheHerdrLine()
    {
        InsideHerdrWithClaude();

        Assert.Equal(0, AhdNew("jim", "--tab", "-t", "newteam").Code);

        Assert.EndsWith("'Team newteam is new: stand it up with /wrangl:agent-team (team newteam).'", _runner.Calls.Single(argv => argv[0] == "/bin/sh")[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AhdNewTabDryRunPrintsTheTabArgvAndCreatesNoTab()
    {
        InsideHerdrWithClaude();

        var (code, stdout, stderr) = AhdNew("jim", "--tab", "--dry-run");

        Assert.True(code == 0, stderr);
        var tabArgv = JsonText.Parse(stdout).Member("tab_argv")!.Value.AsArray().Select(a => a.AsString()).ToList();
        Assert.Equal(["herdr", "tab", "create", "--label", "jim", "--cwd", Project], tabArgv);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("tab") && argv.Contains("create"));
        Assert.DoesNotContain(_runner.Calls, argv => argv[0] == "/bin/sh");
    }

    [Fact]
    public void AhdNewTabOutsideHerdrIsRefusedHerdrAbsent()
    {
        _sandbox.Executable("bin/claude");
        _extraEnv["PATH"] = _sandbox.Bin;

        var (code, stdout, _) = AhdNew("jim", "--tab", "--dry-run");

        Assert.Equal(2, code);
        Assert.Equal("herdr-absent", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
    }

    [Fact]
    public void AhdNewTabWithHerdrEnvButNoHerdrOnPathIsRefusedHerdrAbsent()
    {
        _sandbox.Executable("bin/claude");
        _extraEnv["PATH"] = _sandbox.Bin;
        _extraEnv["HERDR_ENV"] = "1";

        Assert.Equal("herdr-absent", JsonText.Parse(AhdNew("jim", "--tab", "--dry-run").Stdout).Member("refused")!.Value.AsString());
    }

    [Fact]
    public void AhdNewWithoutTabInsideHerdrCreatesNoTab()
    {
        InsideHerdrWithClaude();

        Assert.Equal(0, AhdNew("jim", "--dry-run").Code);
        Assert.Null(JsonText.Parse(AhdNew("jim", "--dry-run").Stdout).Member("tab_argv"));
    }

    [Fact]
    public void AhdNewTabWhoseNameHerdrHoldsIsRefusedNameInUseAndThePaneClosed()
    {
        var herdr = InsideHerdrWithClaude();
        herdr.NameTaken = true;

        var (code, stdout, _) = AhdNew("jim", "--tab");

        Assert.Equal(2, code);
        Assert.Equal("name-in-use", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Contains(_runner.Calls, argv => argv.Length > 3 && argv[0] == "herdr" && argv[1] == "pane" && argv[2] == "close" && argv[3] == "r1");
    }
}
