using System.Runtime.Versioning;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>The message-token resolver in db mode: a path is its own key, an id is looked up across every pool, and pool then recipient break a tie.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MsgTokenResolverTests
{
    private const string Id = "20261030-123044-16is";

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

    private static (Ah.Hierarchy.IHierarchyCore Core, RowMsgs Rows, string Root) Rig()
    {
        var sandbox = UnitSandbox.Create();
        var rows = new RowMsgs();
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" };
        var fx = MeshFixture.Create(vars, Start, sandbox.Root, msgsRoute: rows.Route());
        return (fx.Hierarchy.Core, rows, sandbox.Root);
    }

    [Fact]
    public void ALegacyPathIsItsOwnKeyWithNoLookup()
    {
        var (core, rows, _) = Rig();
        rows.ByIdFails = true;

        var hit = core.ResolveMsgToken("[hierarchy-msg /p/msgs/" + Id + "--imp--s--request.md]");

        Assert.Equal("/p/msgs/" + Id + "--imp--s--request.md", hit!.Key);
    }

    [Fact]
    public void AUniqueIdResolvesToItsKeyFromAnyPool()
    {
        var (core, rows, _) = Rig();
        var key = rows.Add("/p", Id + "--imp--s--request.md", "x");

        Assert.Equal(key, core.ExtractMsgToken("go: [hierarchy-msg " + Id + "] now"));
        Assert.Equal(key, core.ExtractMsgToken("[hierarchy-msg " + Id + " request]", contextDir: "/other"));
    }

    [Fact]
    public void TheTypeWordPicksTheResponse()
    {
        var (core, rows, _) = Rig();
        _ = rows.Add("/p", Id + "--imp--s--request.md", "x");
        var response = rows.Add("/p", Id + "--imp--s--response.md", "y");

        Assert.Equal(response, core.ExtractMsgToken("[hierarchy-msg " + Id + " response]"));
    }

    [Fact]
    public void NegativeAnUnknownTypeWordIsNotAToken()
    {
        var (core, rows, _) = Rig();
        _ = rows.Add("/p", Id + "--imp--s--request.md", "x");

        Assert.Null(core.ResolveMsgToken("[hierarchy-msg " + Id + " reply]"));
    }

    [Fact]
    public void TheSameIdInTwoPoolsNeedsTheContextDir()
    {
        var (core, rows, _) = Rig();
        var a = rows.Add("/a", Id + "--imp--s--request.md", "x");
        _ = rows.Add("/b", Id + "--imp--s--request.md", "x");
        var token = "[hierarchy-msg " + Id + "]";

        var without = core.ResolveMsgToken(token)!;

        Assert.Null(without.Key);
        Assert.Equal([("imp", "/a"), ("imp", "/b")], without.Candidates);
        Assert.Equal(a, core.ExtractMsgToken(token, contextDir: "/a"));
        Assert.Null(core.ExtractMsgToken(token, contextDir: "/c"));
    }

    [Fact]
    public void TheSameIdWithTwoRecipientsInOnePoolNeedsTheRecipient()
    {
        var (core, rows, _) = Rig();
        _ = rows.Add("/a", Id + "--imp--s--request.md", "x");
        var rev = rows.Add("/a", Id + "--rev--s--request.md", "x");
        var token = "[hierarchy-msg " + Id + "]";

        var withDirOnly = core.ResolveMsgToken(token, contextDir: "/a")!;

        Assert.Null(withDirOnly.Key);
        Assert.Equal(2, withDirOnly.Candidates.Count);
        Assert.Equal(rev, core.ExtractMsgToken(token, contextDir: "/a", expectedTo: "rev"));
        Assert.Null(core.ExtractMsgToken(token, contextDir: "/a", expectedTo: "arc"));
    }

    [Fact]
    public void NegativeAnUnknownIdIsUnresolvedWithNoCandidates()
    {
        var (core, rows, _) = Rig();
        _ = rows.Add("/a", "20261030-123044-zzzz--imp--s--request.md", "x");

        var hit = core.ResolveMsgToken("[hierarchy-msg " + Id + "]")!;

        Assert.Null(hit.Key);
        Assert.Empty(hit.Candidates);
        Assert.Empty(core.ExtractMsgTokens("[hierarchy-msg " + Id + "]"));
    }

    [Fact]
    public void AStoreThatFailsThrowsRatherThanReadingAsNoToken()
    {
        var (core, rows, _) = Rig();
        rows.ByIdFails = true;

        _ = Assert.Throws<Ah.Core.Json.StoreUnavailableException>(() => core.ExtractMsgToken("[hierarchy-msg " + Id + "]"));
    }
}
