using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A reconcile of the registrations, status and git lanes reads only what can have moved.</summary>
public sealed class LaneQuietKeysTests
{
    private const string Dir = "/h/.ah";

    private sealed class Reader : IViewStateReader
    {
        public JsonObject Stamps { get; set; } = new() { ["s1"] = "a", ["s2"] = "b" };

        public JsonValue StatusStamp { get; set; } = "1:10";

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) => (domain + "." + op) switch
        {
            "mailbox.keys" => new JsonObject { ["sessions"] = JsonValue.FromArray(["s1", "s2"]), ["stamps"] = Stamps },
            "status.stamp" => StatusStamp.IsNull ? throw new InvalidOperationException("no") : new JsonObject { ["stamp"] = StatusStamp },
            "status.read" => new JsonObject { ["schema"] = 1 },
            "worktree-grants.keys" => new JsonObject { ["teams"] = JsonValue.FromArray(["t1"]) },
            "worktree-grants.status" => new JsonObject { ["present"] = true, ["members"] = new JsonObject { ["m1"] = new JsonObject { ["work_root"] = "/w/a" }, ["m2"] = new JsonObject { ["work_root"] = "/w/b" } } },
            _ => JsonValue.Null,
        };
    }

    [Fact]
    public void ARegistrationIsQuietOnlyWhileItsStampIsTheOneItWasReadAt()
    {
        var reader = new Reader();
        var lane = new RegistrationLane(reader, "/h");
        _ = lane.Keys();
        Assert.Empty(lane.QuietKeys()); // negative: nothing read yet

        _ = lane.Reread("s1", 0);
        Assert.Equal(["s1"], lane.QuietKeys()); // negative: s2 was never read

        reader.Stamps = new JsonObject { ["s1"] = "moved", ["s2"] = "b" };
        _ = lane.Keys();
        Assert.Empty(lane.QuietKeys()); // negative: a moved stamp
    }

    [Fact]
    public void NegativeAListingWithNoStampsNamesNoRegistrationQuiet()
    {
        var reader = new Reader();
        var lane = new RegistrationLane(reader, "/h");
        _ = lane.Keys();
        _ = lane.Reread("s1", 0);

        reader.Stamps = new JsonObject();
        _ = lane.Keys();

        Assert.Empty(lane.QuietKeys());
    }

    [Fact]
    public void AStatusDirIsQuietOnlyWhileItsFileHasTheStampItWasReadAt()
    {
        var reader = new Reader();
        var lane = new StatusLane(reader, () => [Dir]);
        Assert.Empty(lane.QuietKeys()); // negative: nothing read yet

        _ = lane.Reread(Dir, 0);
        Assert.Equal([Dir], lane.QuietKeys());

        reader.StatusStamp = "2:10";
        Assert.Empty(lane.QuietKeys()); // negative: the file moved

        reader.StatusStamp = JsonValue.Null;
        Assert.Empty(lane.QuietKeys()); // negative: a stat that cannot answer
    }

    private sealed class NoGit : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request) => new(1, null, false, false, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
    }

    [Fact]
    public void AGitKeyIsQuietUnlessAClientHoldsItsSliceThroughTheLazyWrapperToo()
    {
        var held = GitLane.SliceIdOf(Dir, "t1", "m1");
        var git = new GitLane(new Reader(), () => [Dir], new NoGit(), () => 0, _ => { }, "/h", id => id == held);
        _ = git.Keys();

        var lazy = new LazyLane(git, () => true);

        Assert.Equal([GitLane.KeyOf(Dir, "t1", "m2")], lazy.QuietKeys()); // negative: the held m1 is not named
    }

    [Fact]
    public void NegativeAGitLaneWithNoLeaseAnswerNamesNothingQuiet()
    {
        var git = new GitLane(new Reader(), () => [Dir], new NoGit(), () => 0, _ => { }, "/h");
        _ = git.Keys();

        Assert.Empty(git.QuietKeys());
    }
}
