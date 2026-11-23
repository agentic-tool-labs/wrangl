#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Net;

namespace Ah.Daemon.Tests;

/// <summary><c>GET /ui/v1/evidence/&lt;id&gt;/e&lt;k&gt;</c> (adr-39): a stored file is served with its type and nosniff; any other shape is 404.</summary>
public sealed class UiEvidenceRouteTests
{
    private const string Id = "20261016-120000-ab12";

    [Fact]
    public async Task AStoredFileIsServedWithItsTypeAndOtherShapesAre404()
    {
        await using var rig = UiRig.Create();
        var dir = Path.Combine(rig.Box.AhHome, "evidence", Id);
        _ = Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "e1-shot.png"), [1, 2, 3]);
        var grant = await rig.LeaseAsync();

        using var ok = await rig.SendAsync(grant.Port, HttpMethod.Get, $"/ui/v1/evidence/{Id}/e1");
        using var traversal = await rig.SendAsync(grant.Port, HttpMethod.Get, $"/ui/v1/evidence/{Id}/../x");
        using var badKey = await rig.SendAsync(grant.Port, HttpMethod.Get, $"/ui/v1/evidence/{Id}/e1x");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/png", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(ok.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal([1, 2, 3], await ok.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, badKey.StatusCode);

        // the client folds `..` away before sending, so the finder itself must refuse it
        Assert.Null(Ah.Hierarchy.HierarchyModule.EvidenceFind(rig.Box.AhHome, Id, ".."));
        Assert.Null(Ah.Hierarchy.HierarchyModule.EvidenceFind(rig.Box.AhHome, "..", "e1"));
    }
}
