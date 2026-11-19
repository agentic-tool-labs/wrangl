using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>While the machine is overloaded the daemon paces new spawns: a refused one exits 75 and launches nothing; nothing else is paced.</summary>
public sealed partial class RosterTeamVerbTests
{
    private sealed class FakeAdmission(SpawnAdmissionReply reply) : IRosterSpawnAdmission
    {
        public List<(string Team, string Member, bool DryRun)> Asked { get; } = [];

        public SpawnAdmissionReply Ask(string team, string member, bool dryRun)
        {
            Asked.Add((team, member, dryRun));
            return reply;
        }
    }

    private static SpawnAdmissionReply Refused => new(false, "2026-10-25T01:40:00.000Z", ["cpu busy 0.94 for 180 s (threshold 0.90)"], null);

    [Fact]
    public void ASpawnTheDaemonPutsOffExitsSeventyFiveWithTheReasonAndLaunchesNothing()
    {
        OnTmux();
        var admission = new FakeAdmission(Refused);
        _spawnAdmission = admission;

        var (code, stdout, stderr) = Roster(["spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self]);

        Assert.Equal(75, code);
        Assert.Contains("machine overloaded (cpu busy 0.94 for 180 s (threshold 0.90)); next spawn allowed after 2026-10-25T01:40:00.000Z; nothing was launched", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"spawned\"", stdout, StringComparison.Ordinal);
        Assert.Single(admission.Asked);
    }

    [Fact]
    public void TheOwningOrchestratorIsToldTheLoadAndMaySpawnDespiteItWhileAnyoneElseWithTheFlagIsStillPaced()
    {
        OnTmux();
        _withJournal = true;
        _spawnAdmission = new FakeAdmission(Refused with { Harnesses = 12 });
        string[] spawn = ["spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self];
        string[] LoadOverrides() => StallStore.JournalRows(_sandbox.AhHome).Where(r => r.Contains("\"load-override\"", StringComparison.Ordinal)).ToArray();

        // no team file: the caller is not a team's Orchestrator (a foreign team's owner guard refuses before pacing is asked)
        var (otherCode, otherOut, otherErr) = Roster([.. spawn, "--despite-load"]);
        Assert.True(otherCode == 75, otherOut + otherErr);
        Assert.Contains("only the team's Orchestrator can override", otherErr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"spawned\"", otherOut, StringComparison.Ordinal);
        Assert.Empty(LoadOverrides());

        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));
        var (pacedCode, pacedOut, pacedErr) = Roster(spawn);
        Assert.Equal(75, pacedCode);
        Assert.Contains("12 Claude harness processes run. To spawn anyway, assess the risk and re-run with --despite-load.", pacedErr, StringComparison.Ordinal);
        var paced = JsonText.Parse(pacedOut);
        Assert.Equal("machine-overloaded", paced.Member("refused")!.Value.AsString());
        Assert.Equal(12, paced.Member("harnesses")!.Value.AsNumber());

        var (dryCode, _, _) = Roster([.. spawn, "--despite-load", "--dry-run"]);
        Assert.Equal(0, dryCode);
        Assert.Empty(LoadOverrides());

        AgeSpawnedRows();
        var (code, stdout, stderr) = Roster([.. spawn, "--despite-load"]);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("ah roster: spawned despite machine load (cpu busy 0.94 for 180 s (threshold 0.90))", stderr, StringComparison.Ordinal);
        Assert.True(JsonText.Parse(stdout).Member("despite_load")!.Value.AsBool());
        var row = JsonText.Parse(Assert.Single(LoadOverrides()));
        Assert.Equal("proj", row.Member("team")!.Value.AsString());
        Assert.Equal(["cpu busy 0.94 for 180 s (threshold 0.90)"], row.Member("triggers")!.Value.AsArray().Select(t => t.AsString()));
    }

    [Fact]
    public void ASpawnOneThroughTheRosterIsPacedToo()
    {
        OnTmux();
        var admission = new FakeAdmission(Refused);
        _spawnAdmission = admission;
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (code, _, stderr) = Roster(["spawn-one", "sub-orchestrator", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1"]);

        Assert.Equal(75, code);
        Assert.Contains("nothing was launched", stderr, StringComparison.Ordinal);
        Assert.Single(admission.Asked);
    }

    [Fact]
    public void AnAdmittedSpawnGoesOnAndAskedWithTheDryRunFlag()
    {
        OnTmux();
        var admission = new FakeAdmission(new SpawnAdmissionReply(true, null, [], null));
        _spawnAdmission = admission;

        var (code, _) = SpawnImplementor("--dry-run");

        Assert.Equal(0, code);
        Assert.True(admission.Asked.Single().DryRun);
    }

    [Fact]
    public void ADaemonThatCannotBeAskedDoesNotPaceAndTheOutputSaysTheLoadIsUnknown()
    {
        OnTmux();
        _spawnAdmission = new FakeAdmission(new SpawnAdmissionReply(true, null, [], "daemon unavailable: down"));

        var (code, _, stderr) = Roster(["spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--dry-run"]);

        Assert.Equal(0, code);
        Assert.Contains("load unknown", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("spawn-ad-hoc", "nosuchrole")]
    [InlineData("spawn-one", "nosuchrole")]
    public void ASpawnRefusedForItsArgumentsNeverAsksTheDaemonSoItDoesNotUseUpTheGap(string verb, string role)
    {
        OnTmux();
        var admission = new FakeAdmission(new SpawnAdmissionReply(true, null, [], null));
        _spawnAdmission = admission;

        var (code, _, _) = Roster([verb, role, "--team", "proj", "--orchestrator-pid", Self]);

        Assert.NotEqual(0, code);
        Assert.Empty(admission.Asked);

        var (validCode, _) = SpawnImplementor("--dry-run");

        Assert.Equal(0, validCode);
        Assert.Single(admission.Asked);
    }

    [Fact]
    public void AVerbThatIsNotASpawnNeverAsksTheDaemonToAdmit()
    {
        OnTmux();
        var admission = new FakeAdmission(Refused);
        _spawnAdmission = admission;

        var (code, _, _) = Roster(["whoami", "--team", "proj", "--orchestrator-pid", Self]);

        Assert.NotEqual(75, code);
        Assert.Empty(admission.Asked);
    }
}
