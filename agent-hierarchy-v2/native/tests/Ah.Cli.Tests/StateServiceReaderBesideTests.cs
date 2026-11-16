using Ah.Cli.Composition;
using Ah.Core.Files;
using Ah.Core.Json;
using Ah.State;

namespace Ah.Cli.Tests;

/// <summary>
/// b459 C1: what an op hands out beside its result (status.read's witness) is kept for exactly the document the reader parsed from
/// that reply, so a document is never paired with another compute's witness.
/// </summary>
public sealed class StateServiceReaderBesideTests
{
    /// <summary>Each read answers a new document and a new object beside it.</summary>
    private sealed class State : IInternalStateRead
    {
        internal List<object> Handed { get; } = [];

        public Task<StateReply> ReadInternalAsync(string domain, string op, JsonValue? args, string hierarchyDir)
        {
            Handed.Add(new object());
            var body = JsonText.ToUtf8(new JsonObject { ["result"] = new JsonObject { ["n"] = Handed.Count } }, 0);
            return Task.FromResult(new StateReply(200, body, "ok", op == "plain" ? null : Handed[^1]));
        }
    }

    [Fact]
    public void WhatAnOpHandsOutBesideItsResultBelongsToThatOneDocument()
    {
        var state = new State();
        var reader = new StateServiceReader(state);
        var first = reader.Read("status", "read", "/d", new JsonObject { ["dir"] = "/d" });
        var second = reader.Read("status", "read", "/d", new JsonObject { ["dir"] = "/d" });

        Assert.Same(state.Handed[0], reader.Beside(first));
        Assert.Same(state.Handed[1], reader.Beside(second));
        // negative: an op that hands nothing out, a document this reader never returned, and another reader
        Assert.Null(reader.Beside(reader.Read("status", "plain", "/d", new JsonObject())));
        Assert.Null(reader.Beside(JsonText.Parse("{\"n\":1}")));
        Assert.Null(new StateServiceReader(state).Beside(first));
    }

    [Fact]
    public void NegativeALaterCallerInOneScopeParsesItsOwnCopyAndHasNothingBesideIt()
    {
        var state = new State();
        var reader = new StateServiceReader(state);
        using (ListingScope.Begin())
        {
            var ran = reader.Read("status", "read", "/d", new JsonObject { ["dir"] = "/d" });
            var copy = reader.Read("status", "read", "/d", new JsonObject { ["dir"] = "/d" });

            Assert.Single(state.Handed);
            Assert.NotSame(ran.AsObject(), copy.AsObject());
            Assert.Same(state.Handed[0], reader.Beside(ran));
            Assert.Null(reader.Beside(copy));
        }
    }
}
