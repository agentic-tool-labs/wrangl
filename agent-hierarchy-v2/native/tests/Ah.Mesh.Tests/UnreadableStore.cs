using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>A peer log in db mode whose database cannot be read, the same stand-in the Hooks and Hierarchy fail-closed tests use.</summary>
internal static class UnreadableStore
{
    internal static Ah.Hierarchy.PeerRecordsRoute Route() => new(new PeerLog(), new DbMode());

    internal sealed class DbMode : IMessageModeReader
    {
        public string Read(string domain) => "db";
    }

    internal sealed class PeerLog : IPeerRecordSource
    {
        private static InvalidOperationException Down() => new("the state database cannot be read");

        public IReadOnlyList<JsonValue> ReadAll() => throw Down();

        public IReadOnlyList<JsonValue> Rows(PeerQuery query) => throw Down();

        public IReadOnlyList<JsonValue> LatestByKeyFor(JsonValue? sessionId) => throw Down();

        public IReadOnlyList<PeerRowAt> Since(long cursor) => throw Down();

        public long Version() => throw Down();
    }
}
