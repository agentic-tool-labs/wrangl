using System.Text;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

public sealed class ListEndpointTests
{
    private sealed class Spy(ListResult answer) : IListPager
    {
        public string Prefix => "list:spy:";

        public IReadOnlyCollection<string> Filters => ["member"];

        public List<ListRequest> Seen { get; } = [];

        public ValueTask<ListResult> PageAsync(ListRequest request, CancellationToken cancellation)
        {
            Seen.Add(request);
            return ValueTask.FromResult(answer);
        }
    }

    private static Dictionary<string, string> Q(params (string, string)[] pairs) => pairs.ToDictionary(static p => p.Item1, static p => p.Item2);

    private static (int Status, string Body, Spy Pager) Serve(string id, Dictionary<string, string> query, ListResult? answer = null)
    {
        var spy = new Spy(answer ?? new ListResult.Found(new ListPage(3, 1, null, null, [], null, null)));
        var registry = new ListPagerRegistry();
        registry.Register(spy);
        var (status, body) = ListEndpoint.ServeAsync(registry, id, query, CancellationToken.None).Result;
        return (status, Encoding.UTF8.GetString(body), spy);
    }

    [Fact]
    public void AKnownPrefixAnswersTheEnvelopeAndPassesTheParsedRequest()
    {
        var (status, body, pager) = Serve("list:spy:k", Q(("limit", "7"), ("before", "c"), ("member", "m")));

        Assert.Equal(200, status);
        Assert.Equal("{\"version\":3,\"total\":1,\"head_cursor\":null,\"tail_cursor\":null,\"rows\":[],\"next\":null,\"prev\":null}", body);
        var seen = Assert.Single(pager.Seen);
        Assert.Equal((7, "c", null, "m"), (seen.Limit, seen.Before, seen.After, seen.Param("member")));
    }

    [Fact]
    public void AnUnknownPrefixIs404AndNoPagerIsAsked()
    {
        var (status, body, pager) = Serve("list:other:k", Q());

        Assert.Equal(404, status);
        Assert.Equal("{\"error\":\"not_found\"}", body);
        Assert.Empty(pager.Seen);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("201")]
    public void ALimitOutOfRangeIs400AndNoPagerIsAsked(string limit)
    {
        var (status, body, pager) = Serve("list:spy:k", Q(("limit", limit)));

        Assert.Equal(400, status);
        Assert.Equal("{\"error\":\"limit\"}", body);
        Assert.Empty(pager.Seen);
    }

    [Fact]
    public void AfterTogetherWithBeforeIs400()
    {
        var (status, body, _) = Serve("list:spy:k", Q(("after", "a"), ("before", "b")));

        Assert.Equal(400, status);
        Assert.Equal("{\"error\":\"bad_cursor\"}", body);
    }

    [Theory]
    [InlineData("cache_buster")]
    [InlineData("item")]
    [InlineData("expand")]
    [InlineData("from_ms")]
    [InlineData("Member")]
    public void AnyKeyThePagerDoesNotAcceptIsBadFilterNamingTheKeyWithNoRead(string key)
    {
        var (status, body, pager) = Serve("list:spy:k", Q(("limit", "5"), (key, "x")));

        Assert.Equal(400, status);
        Assert.Equal("{\"error\":\"bad_filter\",\"key\":\"" + key + "\"}", body);
        Assert.Empty(pager.Seen);
    }

    [Fact]
    public void EveryKeyThePagerAcceptsAndTheSharedKeysGiveAPage()
    {
        var (status, _, pager) = Serve("list:spy:k", Q(("after", "a"), ("limit", "5"), ("member", "m")));

        Assert.Equal(200, status);
        _ = Assert.Single(pager.Seen);
        Assert.Equal(200, Serve("list:spy:k", Q(("before", "b"))).Status);
    }

    [Fact]
    public void APagerThatFindsNothingIs404AndOneThatRejectsIs400WithItsReason()
    {
        Assert.Equal(404, Serve("list:spy:k", Q(), new ListResult.NotFound()).Status);
        var (status, body, _) = Serve("list:spy:k", Q(), new ListResult.Invalid("bad_offset"));

        Assert.Equal(400, status);
        Assert.Equal("bad_offset", JsonText.Parse(Encoding.UTF8.GetBytes(body)).Member("error")!.Value.AsString());
    }
}
