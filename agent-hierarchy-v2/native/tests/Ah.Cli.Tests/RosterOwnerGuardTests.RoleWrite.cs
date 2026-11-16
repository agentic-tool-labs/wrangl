using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary>Role-writing verbs are refused to a member session and stay open to a plain one.</summary>
public sealed partial class RosterOwnerGuardTests
{
    public static TheoryData<string> RoleWrites() => ["set", "remove", "trust"];

    [Theory]
    [MemberData(nameof(RoleWrites))]
    public void ARoleWriteFromAMemberProvenByAncestryIsRefused(string sub)
    {
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);

        var (code, stdout, stderr) = Run("role", sub, "counsel", "--dry-run");

        Assert.Equal(2, code);
        Assert.Equal("member-cannot-role-" + sub, Refusal(stdout).Refused);
        Assert.Contains("nothing was changed", stdout + stderr, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RoleWrites))]
    public void ARoleWriteFromASessionCarryingTheMemberMarkerIsRefused(string sub)
    {
        _env[MeshModule.MemberMarkerEnv] = "proj-implementor";

        var (code, stdout, _) = Run("role", sub, "counsel", "--dry-run");

        Assert.Equal(2, code);
        Assert.Equal("member-cannot-role-" + sub, Refusal(stdout).Refused);
    }

    [Theory]
    [MemberData(nameof(RoleWrites))]
    public void NegativeARoleWriteFromAPlainSessionIsNotRefusedAsAMemberCall(string sub)
    {
        var (_, stdout, stderr) = Run("role", sub, "counsel", "--dry-run");

        Assert.DoesNotContain("member-cannot-role", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeRoleListFromAMemberIsNotRefused()
    {
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);

        var (code, stdout, stderr) = Run("role", "list");

        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("member-cannot-role", stdout + stderr, StringComparison.Ordinal);
    }
}
