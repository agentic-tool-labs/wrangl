using Ah.Commands;

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

    private static SpawnAdmissionReply Refused => new(false, "2026-10-25T01:40:00.000Z", ["load1_per_cpu=1.9 (>= 1.5)"], null);

    [Fact]
    public void ASpawnTheDaemonPutsOffExitsSeventyFiveWithTheReasonAndLaunchesNothing()
    {
        OnTmux();
        var admission = new FakeAdmission(Refused);
        _spawnAdmission = admission;

        var (code, stdout, stderr) = Roster(["spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self]);

        Assert.Equal(75, code);
        Assert.Contains("machine overloaded (load1_per_cpu=1.9 (>= 1.5)); next spawn allowed after 2026-10-25T01:40:00.000Z; nothing was launched", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"spawned\"", stdout, StringComparison.Ordinal);
        Assert.Single(admission.Asked);
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
