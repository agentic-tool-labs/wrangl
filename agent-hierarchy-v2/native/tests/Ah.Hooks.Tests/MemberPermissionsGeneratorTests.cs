using Ah.Hooks.Surface;
using Ah.Roster;

namespace Ah.Hooks.Tests;

/// <summary>ah-simplify section C: one allow list for every role, no deny, ask or sandbox.</summary>
public sealed class MemberPermissionsGeneratorTests
{
    private static MemberPermissionsRequest Request(string role = "implementor", IReadOnlyList<string>? userAllow = null) =>
        new("m1", "chain", role, "/repo/.claude/worktrees/i1", "/repo", "/opt/ah/bin/ah", "/plugin", null, userAllow);

    [Fact]
    public void TheGeneratorEmitsAnAllowListOnly()
    {
        var result = MemberPermissionsGenerator.Generate(Request(userAllow: ["Bash(cargo:*)"]));

        // an allow list and the member's own directories; never a deny or an ask
        Assert.Equal(["allow", "additionalDirectories"], result.Permissions.Keys);
        var allow = result.Permissions["allow"].AsArray().Select(a => a.AsString()).ToList();
        // ah2-70: the work tree and the scratch directory are named outright, for every file tool
        var owned = result.Permissions["additionalDirectories"].AsArray().Select(a => a.AsString()).ToList();
        Assert.NotEmpty(owned);
        foreach (var dir in owned)
        {
            Assert.Contains($"Edit(/{dir}/**)", allow);
            Assert.Contains($"Write(/{dir}/**)", allow);
            Assert.Contains($"Read(/{dir}/**)", allow);
        }
        foreach (var rule in new[]
        {
            "Bash(/opt/ah/bin/ah:*)", "Bash(ah:*)", "Bash(/opt/ah/bin/ahd:*)", "Bash(ahd:*)", "Bash(/plugin/bin/ah:*)",
            "Bash(git:*)", "Bash(git -C:*)", "Bash(dotnet:*)", "Bash(grep:*)", "Bash(cd:*)", "Read", "Edit", "Grep",
        })
        {
            Assert.Contains(rule, allow);
        }

        Assert.Equal("Bash(cargo:*)", allow[^1]);
        Assert.Equal(allow.Count, allow.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheListLetsAMemberCleanUpItsOwnScratchAndProcessesOnly()
    {
        var allow = MemberPermissionsGenerator.Generate(Request()).Permissions["allow"].AsArray().Select(a => a.AsString()).ToList();
        foreach (var rule in MemberPermissionsGenerator.Cleanup.Concat(["Bash(kill:*)", "Bash(pkill:*)"]))
        {
            Assert.Contains(rule, allow);
        }

        bool Cleans(string command) => MemberPermissionsGenerator.Cleanup.Any(r => MemberPermissionsGenerator.GlobMatches(r["Bash(".Length..^1], command));
        Assert.True(Cleans("rm -rf /private/tmp/ahprobe.Ab12Cd"));
        Assert.True(Cleans("chmod -R u+w /tmp/ahprobe.Ab12Cd"));
        Assert.False(Cleans("rm -rf /private/tmp/other.Ab12Cd"));
        Assert.False(Cleans("rm -rf /Users/example/ahprobe.Ab12Cd"));
        Assert.False(Cleans("chmod -R 777 /tmp/ahprobe.Ab12Cd"));
        Assert.Equal("Bash(kill:*)", MemberPermissionsGenerator.RuleFor(Request(), "kill -TERM 4242").Rule);
        Assert.Null(MemberPermissionsGenerator.RuleFor(Request(), "killall dotnet").Rule);
    }

    [Fact]
    public void TheStopAndTestLaneStepsAreCoveredAndTheirNeighboursAreNot()
    {
        Assert.Equal("Bash(sleep:*)", MemberPermissionsGenerator.RuleFor(Request(), "sleep 2").Rule);
        Assert.Equal("Bash(ps:*)", MemberPermissionsGenerator.RuleFor(Request(), "ps -p 4242 -o pid,command").Rule);
        Assert.Equal("Bash(pgrep:*)", MemberPermissionsGenerator.RuleFor(Request(), "pgrep -fl dotnet").Rule);
        Assert.Equal("Bash(/plugin/tests/with-slot.sh:*)", MemberPermissionsGenerator.RuleFor(Request(), "/plugin/tests/with-slot.sh --eta 60 dotnet test").Rule);
        Assert.Null(MemberPermissionsGenerator.RuleFor(Request(), "/plugin/tests/other.sh --eta 60").Rule);
        Assert.Null(MemberPermissionsGenerator.RuleFor(Request(), "psql -c 'select 1'").Rule);
    }

    [Fact]
    public void EveryRoleGetsTheSameList() =>
        Assert.Equal(
            MemberPermissionsGenerator.Generate(Request("reviewer")).Permissions["allow"].AsArray().Select(a => a.AsString()),
            MemberPermissionsGenerator.Generate(Request("implementor")).Permissions["allow"].AsArray().Select(a => a.AsString()));

    [Fact]
    public void RuleForNamesTheCoveringRuleAndNoneForAnUnlistedProgram()
    {
        Assert.Equal("Bash(git:*)", MemberPermissionsGenerator.RuleFor(Request(), "git checkout -b x").Rule);
        Assert.Null(MemberPermissionsGenerator.RuleFor(Request(), "curl https://example.com").Rule);
    }
}
