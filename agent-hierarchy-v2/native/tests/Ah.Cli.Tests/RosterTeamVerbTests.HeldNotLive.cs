using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>The disband close summary splits held members into those found live (still_live) and those not found live (held_not_live).</summary>
public sealed partial class RosterTeamVerbTests
{
    private string TreeDir(string name)
    {
        var path = Path.Combine(Project, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private static string MemberAt(string name, string pane, string? root) =>
        $"{{\"role\":\"implementor\",\"name\":\"{name}\",\"route\":\"peer\",\"transport_id\":\"{pane}\",\"model\":\"sonnet\"" +
        (root is null ? string.Empty : $",\"expected_root\":\"{root}\"") + "}";

    /// <summary>Git reports a dirty tree only for the work trees in <paramref name="dirty"/>.</summary>
    private void DirtyOnly(params string[] dirty)
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        _runner.Output = argv => argv.Contains("--show-toplevel") ? Project + "\n"
            : argv.Contains("--abbrev-ref") ? "HEAD\n"
            : argv.Contains("rev-parse") && argv.Contains("HEAD") ? _head + "\n"
            : argv.Contains("status") && dirty.Any(d => argv.Contains(d)) ? " M x\n"
            : string.Empty;
    }

    private (int Code, JsonValue Result) CloseProj()
    {
        var token = TokenOf(Roster("disband", "--team", "proj").Stdout);
        var (code, stdout, _) = Roster("disband", "--close", "--confirm", "--plan-token", token, "--team", "proj");
        return (code, JsonText.Parse(stdout));
    }

    private static List<string> StillLive(JsonValue result) => result.Member("still_live")!.Value.AsArray().Select(m => m.AsString()).ToList();

    private static List<(string Member, string Status)> HeldNotLive(JsonValue result) =>
        result.Member("held_not_live")!.Value.AsArray().Select(m => (m.Member("member")!.Value.AsString(), m.Member("status")!.Value.AsString())).ToList();

    private static void AssertSummaryAddsUp(JsonValue result) =>
        Assert.Equal(
            (int)result.Member("expected")!.Value.AsNumber(),
            (int)result.Member("closed_count")!.Value.AsNumber() + StillLive(result).Count + HeldNotLive(result).Count);

    private const string Closer = "proj-closer";
    private const string DeadDirty = "proj-dirty";
    private const string LiveAsked = "proj-live";
    private const string NoRow = "proj-norow";

    /// <summary>A dead member with a clean tree, which closes; the members in <paramref name="more"/> are listed beside it.</summary>
    private void ArrangeCloserAnd(string dirtyTree, params string[] more)
    {
        var closerTree = TreeDir("t-closer");
        WriteTeam("proj", LiveTeamJson("tmux", string.Join(",", [MemberAt(Closer, "%1", closerTree), .. more])));
        AppendGoneRow(Closer, "proj");
        DirtyOnly(dirtyTree);
    }

    [Fact]
    public void AMemberHeldAsWorkingIsStillLiveAndNotInHeldNotLive()
    {
        ArrangeCloserAnd("none", MemberAt(LiveAsked, "%2", TreeDir("t-live")));
        PeerUp(LiveAsked, "%2", session: "sess-live");
        Activity("sess-live.json", "working", DateTimeOffset.UtcNow);

        var (code, result) = CloseProj();

        Assert.Equal(3, code);
        Assert.Equal([LiveAsked], StillLive(result));
        Assert.Empty(HeldNotLive(result));
        Assert.Equal(2, (int)result.Member("expected")!.Value.AsNumber());
        Assert.Equal(1, (int)result.Member("closed_count")!.Value.AsNumber());
        AssertSummaryAddsUp(result);
    }

    [Fact]
    public void ADeadDirtyMemberClosesAndWarnsAndIsNotHeld()
    {
        var dirty = TreeDir("t-dirty");
        ArrangeCloserAnd(dirty, MemberAt(DeadDirty, "%2", dirty));
        AppendGoneRow(DeadDirty, "proj");

        var (_, result) = CloseProj();

        Assert.Empty(StillLive(result));
        Assert.Empty(HeldNotLive(result));
        Assert.Contains(DeadDirty + " not-running-dirty", result.Member("release")!.Value.Member("summary")!.Value.AsString(), StringComparison.Ordinal);
        AssertSummaryAddsUp(result);
    }

    [Fact]
    public void AMemberWhoseLivenessCannotBeDecidedIsNotHeld()
    {
        ArrangeCloserAnd("none", MemberAt(NoRow, "%2", TreeDir("t-norow")));
        _sandbox.PlantPeers(
            Hierarchy,
            $"{{\"type\":\"peer\",\"status\":\"seen\",\"name\":\"{NoRow}\",\"role\":\"implementor\",\"session_id\":\"s-norow\",\"team\":\"proj\",\"ts\":\"{DateTimeOffset.UtcNow.AddHours(-2):yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\"}}\n");

        var (_, result) = CloseProj();

        Assert.Empty(HeldNotLive(result));
        Assert.Contains(NoRow + " liveness-unknown", result.Member("release")!.Value.Member("summary")!.Value.AsString(), StringComparison.Ordinal);
        AssertSummaryAddsUp(result);
    }

    [Fact]
    public void LiveMembersHeldAsWorkingAreStillLive()
    {
        ArrangeCloserAnd("none", MemberAt("proj-unans", "%2", TreeDir("t-unans")), MemberAt("proj-notready", "%3", TreeDir("t-notready")));
        PeerUp("proj-unans", "%2", session: "sess-unans");
        PeerUp("proj-notready", "%3", session: "sess-notready");
        Activity("sess-unans.json", "working", DateTimeOffset.UtcNow);
        Activity("sess-notready.json", "blocked", DateTimeOffset.UtcNow);

        var (_, result) = CloseProj();

        Assert.Equal(["proj-unans", "proj-notready"], StillLive(result).Order().Reverse().ToList());
        Assert.Empty(HeldNotLive(result));
        AssertSummaryAddsUp(result);
    }

    [Fact]
    public void ADeadDirtyMemberClosesWhileALiveWorkingMemberStaysStillLive()
    {
        var dirty = TreeDir("t-dirty");
        ArrangeCloserAnd(dirty, MemberAt(DeadDirty, "%2", dirty), MemberAt(LiveAsked, "%3", TreeDir("t-live")));
        AppendGoneRow(DeadDirty, "proj");
        PeerUp(LiveAsked, "%3", session: "sess-live");
        Activity("sess-live.json", "working", DateTimeOffset.UtcNow);

        var (_, result) = CloseProj();

        Assert.Equal([LiveAsked], StillLive(result));
        Assert.Empty(HeldNotLive(result));
        Assert.Equal(3, (int)result.Member("expected")!.Value.AsNumber());
        AssertSummaryAddsUp(result);
    }

    [Fact]
    public void ADeadMemberWithNoWorkTreeClosesAndIsNotHeld()
    {
        ArrangeCloserAnd("none", MemberAt("proj-notree", "%2", null));
        AppendGoneRow("proj-notree", "proj");

        var (_, result) = CloseProj();

        Assert.Empty(StillLive(result));
        Assert.Empty(HeldNotLive(result));
        AssertSummaryAddsUp(result);
    }

    [Fact]
    public void NegativeACloseWithNothingHeldHasAnEmptyHeldNotLiveArray()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        AppendGoneRow(Impl, "proj");
        ScriptGit();

        var (code, result) = CloseProj();

        Assert.Equal(0, code);
        Assert.Empty(HeldNotLive(result));
        Assert.Empty(StillLive(result));
    }
}
