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

    [Fact]
    public async Task AMidFileRangeIs206AndAnOutOfBoundsRangeIs416()
    {
        await using var rig = UiRig.Create();
        var dir = Path.Combine(rig.Box.AhHome, "evidence", Id);
        _ = Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "e1-log.bin"), [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var grant = await rig.LeaseAsync();

        Task<HttpResponseMessage> Get(string? range) => rig.SendAsync(
            grant.Port, HttpMethod.Get, $"/ui/v1/evidence/{Id}/e1", r => { if (range is not null) { r.Headers.TryAddWithoutValidation("Range", range); } });

        using var mid = await Get("bytes=2-4");
        using var suffix = await Get("bytes=-3");
        using var past = await Get("bytes=10-");
        using var full = await Get(null);
        using var multi = await Get("bytes=0-1,4-5");
        using var lastByte = await Get("bytes=9-");

        Assert.Equal(HttpStatusCode.PartialContent, mid.StatusCode);
        Assert.Equal([2, 3, 4], await mid.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 2-4/10", mid.Content.Headers.ContentRange?.ToString());
        Assert.Equal("bytes", Assert.Single(mid.Headers.AcceptRanges));
        Assert.Equal([7, 8, 9], await suffix.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, past.StatusCode);
        Assert.Equal("bytes */10", past.Content.Headers.ContentRange?.ToString());

        // nearest non-matches: no header, a multi-range, and the last byte (one short of out of bounds)
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("bytes", Assert.Single(full.Headers.AcceptRanges));
        Assert.Equal(10, (await full.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(HttpStatusCode.OK, multi.StatusCode);
        Assert.Equal(10, (await multi.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(HttpStatusCode.PartialContent, lastByte.StatusCode);
        Assert.Equal([9], await lastByte.Content.ReadAsByteArrayAsync());
    }
}
