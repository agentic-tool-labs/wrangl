using Ah.Core.Json;

namespace Ah.Watch.Tests;

public sealed class InspectTests
{
    private static WatchRegistration Reg(string sid, params string[] boxes) =>
        new(sid, "claude", 1, "2026-03-01T11:00:00.000Z", [.. boxes.Select(static a => new WatchBox(a, $"/boxes/{a}"))], "/h");

    private static string Text(JsonObject o) => JsonText.Stringify(JsonValue.FromObject(o), 0);

    private static JsonObject Entry(string id, string kind = "event", string from = "ahd", string? created = "2026-03-01T10:00:00.000Z", JsonObject? data = null)
    {
        var e = new JsonObject { ["id"] = id, ["kind"] = kind, ["from"] = from, ["event"] = "check-in-due" };
        if (created is not null)
        {
            e["created"] = created;
        }

        if (data is not null)
        {
            e["data"] = JsonValue.FromObject(data);
        }

        return e;
    }

    [Fact]
    public void TimersListEachOrchestratorSessionOnceSortedWithItsBoxesAndOwnership()
    {
        var host = new FakeHost();
        host.Registrations.AddRange([Reg("s-b", "orchestrator@b"), Reg("s-a", "orchestrator@a", "implementor@a"), Reg("s-a", "orchestrator@dup"), Reg("s-none", "implementor@x")]);
        host.Fresh.Add("/boxes/orchestrator@a");

        var timers = Inspect.Timers(host, 0, 15000, null)["timers"].AsArray().Select(static t => t.AsObject()).ToList();

        Assert.Equal(["s-a", "s-b"], timers.Select(static t => t["session"].AsString()));
        Assert.Equal(["orchestrator@a"], timers[0]["addresses"].AsArray().Select(static a => a.AsString()));
        Assert.True(timers[0]["owned"].AsBool());
        Assert.False(timers[1]["owned"].AsBool());
    }

    [Fact]
    public void TimersOfOneSessionAreOnlyThatSession()
    {
        var host = new FakeHost();
        host.Registrations.AddRange([Reg("s-a", "orchestrator@a"), Reg("s-b", "orchestrator@b")]);

        var only = Inspect.Timers(host, 0, 15000, "s-b")["timers"].AsArray().Select(static t => t.AsObject()).ToList();

        Assert.Equal("s-b", Assert.Single(only)["session"].AsString());
        Assert.Empty(Inspect.Timers(host, 0, 15000, "s-zzz")["timers"].AsArray().ToList());
    }

    [Fact]
    public void TimersReportWhatTheClockWouldFireWithoutWritingAnything()
    {
        var host = new FakeHost();
        host.Registrations.Add(Reg("s1", "orchestrator@alpha"));
        host.Candidates["s1"] =
        [
            new DispatchCandidate("req-1", "implementor", "slug", "impl@alpha", "impl", "small", "2026-03-01T11:00:00.000Z", false, "/r.md", true, string.Empty, "/h"),
        ];

        var timers = Inspect.Timers(host, 0, 15000, null)["timers"].AsArray().Select(static t => t.AsObject()).ToList();

        var due = Assert.Single(timers)["due"].AsArray().Select(static d => d.AsObject()).ToList();
        Assert.Equal("report-landed", Assert.Single(due)["event"].AsString());
        Assert.Equal("impl@alpha", due[0]["member"].AsString());
        Assert.Empty(host.Gates);
        Assert.Empty(host.Surfaced);
    }

    [Fact]
    public void EventsAreTheUnackedEventsFromTheDaemonOldestFirst()
    {
        var boxes = new[] { new WatchBox("orchestrator@a", "/boxes/a"), new WatchBox("orchestrator@b", "/boxes/b") };
        var entries = new Dictionary<string, IReadOnlyList<JsonObject>>
        {
            ["/boxes/a"] =
            [
                Entry("e2", created: "2026-03-01T10:05:00.000Z", data: new JsonObject { ["request_id"] = "r9" }),
                Entry("e-note", kind: "note"),
                Entry("e-peer", from: "bob@a"),
                Entry("e-acked"),
            ],
            ["/boxes/b"] = [Entry("e1", created: "2026-03-01T10:01:00.000Z")],
        };

        var result = Inspect.Events(boxes, d => entries[d], (d, id) => id == "e-acked", null)["events"].AsArray().Select(static e => e.AsObject()).ToList();

        Assert.Equal(["e1", "e2"], result.Select(static e => e["id"].AsString()));
        Assert.Equal(JsonValue.Null.Kind, result[0]["request_id"].Kind);
        Assert.Equal("r9", result[1]["request_id"].AsString());
        Assert.Equal("orchestrator@b", result[0]["address"].AsString());
    }

    [Fact]
    public void EventsOfOneAddressAreOnlyThatBoxAndAnEntryWithoutATimeKeepsNoCreatedKey()
    {
        var boxes = new[] { new WatchBox("orchestrator@a", "/boxes/a"), new WatchBox("orchestrator@b", "/boxes/b") };
        var entries = new Dictionary<string, IReadOnlyList<JsonObject>>
        {
            ["/boxes/a"] = [Entry("ea", created: null)],
            ["/boxes/b"] = [Entry("eb")],
        };

        var onlyA = Inspect.Events(boxes, d => entries[d], static (_, _) => false, "orchestrator@a")["events"].AsArray().Select(static e => e.AsObject()).ToList();

        var row = Assert.Single(onlyA);
        Assert.Equal("ea", row["id"].AsString());
        Assert.False(row.ContainsKey("created"));
        Assert.Equal(
            "{\"events\":[{\"address\":\"orchestrator@a\",\"id\":\"ea\",\"event\":\"check-in-due\",\"to_session\":null,\"request_id\":null}]}",
            Text(Inspect.Events(boxes, d => entries[d], static (_, _) => false, "orchestrator@a")));
    }
}
