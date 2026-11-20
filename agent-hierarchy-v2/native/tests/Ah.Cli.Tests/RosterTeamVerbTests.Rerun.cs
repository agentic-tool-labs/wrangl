using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>The rerun line a refusal prints runs as printed: its placeholder filled in, the same verb takes every flag it names (ah2-299).</summary>
public sealed partial class RosterTeamVerbTests
{
    /// <summary>The printed line's words from <paramref name="verb"/> on, each placeholder replaced; the lines these tests print quote nothing.</summary>
    private static string[] RerunWords(string line, string verb, string placeholder, string value) =>
        [.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(w => w != verb).Select(w => w == placeholder ? value : w)];

    /// <summary>A role the roster lacks hands over to spawn-ad-hoc, so its rerun names no --member; one the roster defines keeps it.</summary>
    [Theory]
    [InlineData("""[{"role":"reviewer","model":"opus"}]""", false)]
    [InlineData("""[{"role":"architect"}]""", true)]
    public void TheModelRerunOfASpawnOneRunsAsPrinted(string members, bool rosterRow)
    {
        WriteGlobalRosterMembers(members);

        var (code, stdout, _) = Roster("spawn-one", "architect", "--team", "proj", "--orchestrator-pid", Self, "--dry-run");
        Assert.Equal(2, code);
        var refusal = JsonText.Parse(stdout).AsObject();
        Assert.Equal("member-model-undefined", refusal["refused"].AsString());
        Assert.Equal(rosterRow, refusal["rerun"].AsString().Contains("--member ", StringComparison.Ordinal));

        var rerun = Roster(RerunWords(refusal["rerun"].AsString(), "spawn-one", "<MODEL>", "opus"));

        Assert.True(rerun.Code == 0, refusal["rerun"].AsString() + "\n" + rerun.Stdout + rerun.Stderr);
        Assert.Contains("--model opus", rerun.Stdout, StringComparison.Ordinal);
    }
}
