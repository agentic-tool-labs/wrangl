using System.Reflection;
using System.Runtime.ExceptionServices;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>Status where a message is a row and no file, as on a home past the cutover: the goldens' fixture keeps its files, so a stat passes there.</summary>
public sealed class StatusStoredMessageTests
{
    private static string Golden => File.ReadAllText(StatusEntriesFixture.GoldenPath());

    [Fact]
    public void WithNoMessageFilesTheStatusIsStillTheGoldenStatus()
    {
        var fixture = StatusEntriesFixture.Create();
        var services = fixture.Services();
        TestMsgStore.DropFiles(TestMsgStore.HomeOf(fixture.R), fixture.Root);

        Assert.Equal(Golden, fixture.Snapshot(services));
    }

    [Fact]
    public void WithNoMessageFilesARebuildStillHasEachResponsesTime()
    {
        var fixture = StatusEntriesFixture.Create();
        var lib = (HierLib)fixture.Services().Status;
        TestMsgStore.DropFiles(TestMsgStore.HomeOf(fixture.R), fixture.Root);

        var responses = fixture.StatusDirs.SelectMany(lib.RebuildStatusEntries)
            .Where(static e => e.Kind == StatusKind.Exchange && e.Json.Member("response") is { IsMap: true }).ToList();

        Assert.NotEmpty(responses);
        Assert.All(responses, static e => Assert.True(e.Json.Member("mtime") is { IsNumber: true }, e.Key));
    }

    [Fact]
    public void ARebuildsConnectionsDoNotGrowWithItsResponses()
    {
        const int Extra = 40;
        var fixture = StatusEntriesFixture.Create();
        _ = fixture.Services();
        var home = TestMsgStore.HomeOf(fixture.R);
        var route = TestMsgStore.SyncedRoute(home, fixture.Root);
        var source = DispatchProxy.Create<Ah.Core.Json.IMsgSource, CountedSource>();
        ((CountedSource)(object)source).Inner = route.Source;
        var lib = (HierLib)fixture.Services(route with { Source = source }).Status;
        var pool = StatusEntriesFixture.Pool(fixture.W);

        int Connections()
        {
            ((CountedSource)(object)source).Scopes = 0;
            _ = lib.RebuildExchangeEntries([pool]);
            return ((CountedSource)(object)source).Scopes;
        }

        var before = Connections();
        foreach (var i in Enumerable.Range(0, Extra))
        {
            var id = $"20260601-000000-x{i:d3}";
            _ = TestMsgStore.Plant(home, Path.Combine(pool, "msgs", StatusEntriesFixture.RequestName(id, "implementor", "more")), $"---\nid: {id}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: more\n---\nbrief\n");
            _ = TestMsgStore.Plant(home, Path.Combine(pool, "msgs", StatusEntriesFixture.ResponseName(id, "more")), $"---\nid: {id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: more\n---\n- DONE\n");
        }

        var after = Connections();

        Assert.Equal(before, after);
    }

    [Fact]
    public void AStatusDirRebuildOpensTheMessageStoreOnce()
    {
        var fixture = StatusEntriesFixture.Create();
        _ = fixture.Services();
        var route = TestMsgStore.SyncedRoute(TestMsgStore.HomeOf(fixture.R), fixture.Root);
        var source = DispatchProxy.Create<Ah.Core.Json.IMsgSource, CountedSource>();
        var counted = (CountedSource)(object)source;
        counted.Inner = route.Source;
        var lib = (HierLib)fixture.Services(route with { Source = source }).Status;

        Assert.All(fixture.StatusDirs, dir =>
        {
            counted.Scopes = 0;
            Assert.NotEmpty(lib.RebuildStatusEntries(dir));
            Assert.Equal(1, counted.Scopes);
        });
    }

    [Fact]
    public void AMessageWithNoCreatedAgesFromItsStoredTime()
    {
        const long SealedMs = 1_700_000_000_000;
        var fixture = StatusEntriesFixture.Create();
        var lib = (HierLib)fixture.Services().Status;
        var home = TestMsgStore.HomeOf(fixture.R);
        var path =Path.Combine(StatusEntriesFixture.Pool(fixture.W), "msgs", StatusEntriesFixture.RequestName("20260601-000000-noct", "implementor", "old"));
        File.WriteAllText(path, "---\nid: 20260601-000000-noct\ntype: request\nto: implementor\nfrom: orchestrator\nslug: old\n---\nbrief\n");
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(SealedMs).UtcDateTime);
        TestMsgStore.DropFiles(home, fixture.Root);

        var exchange = new JsonObject { ["request"] = new JsonObject { ["path"] = path } };

        Assert.Equal(5, lib.ExchangeAgeSec(exchange, SealedMs + 5000));
    }

    /// <summary>Counts the connections opened through a message source.</summary>
    public class CountedSource : DispatchProxy
    {
        internal Ah.Core.Json.IMsgSource Inner { get; set; } = null!;

        internal int Scopes { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(Ah.Core.Json.IMsgSource.Scope))
            {
                Scopes++;
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException!).Throw();
                throw;
            }
        }
    }
}
