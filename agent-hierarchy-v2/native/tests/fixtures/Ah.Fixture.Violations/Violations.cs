using System.Runtime.InteropServices;
using System.Text.Json;
using Ah.Abstractions;

namespace Ah.Fixture.Violations
{
    public interface IOk
    {
    }

    public sealed record OkRecord(int Value);

    public readonly record struct OkStruct(int Value);

    public enum OkKind
    {
        A,
    }

    public static class ViolationsModule
    {
    }

    // A public implementation class: breaks the public surface rule.
    public class LeakyImplementation
    {
    }

    // Compiles against Ah.Daemon: an assembly edge no domain assembly may have.
    internal static class UsesDaemon
    {
        internal static Type Edge => typeof(Ah.Daemon.DaemonModule);
    }

    internal static class UsesRestrictedWriters
    {
        internal static void Go(IRawFileWriter writer, IRecordAppender appender)
        {
            writer.Unlink("x");
            appender.Append("x", default);
        }
    }

    internal static partial class Native
    {
        [LibraryImport("libc", EntryPoint = "getpid")]
        internal static partial int GetPid();
    }

    internal static class StockJson
    {
        internal static byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(1);

        internal static void Write(Stream stream)
        {
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteNumberValue(1);
        }

        // Reading is allowed everywhere.
        internal static int Read(string json) => JsonSerializer.Deserialize<int>(json);
    }

    [StateDomain("links", "links")]
    internal sealed class LinksDomain : IStateDomain
    {
        public string Name => "links";

        public IReadOnlyList<string> StoreNames => ["links"];

        public IReadOnlyList<StateOp> Ops => [];

        public string StateDirectory(RequestContext context) => context.HierarchyDirectory;

        public void AfterWrite(OpCall operation, OpResult result)
        {
        }
    }

    internal static class MutableState
    {
        internal const int Limit = 3;

        internal static readonly IReadOnlyList<string> Names = ["a"];

        internal static readonly List<string> Collected = [];

        internal static readonly string[] Words = ["a"];

        internal static readonly long[] Cell = new long[1];

        internal static readonly List<string> Log = [];

        internal static readonly string[] Table = ["a", "b"];

        internal static readonly System.Collections.Immutable.ImmutableArray<string> Frozen = System.Collections.Immutable.ImmutableArray.Create("a");

        internal static readonly string Label = "a";

        internal static int Counter = 1;

        internal static int Permitted = 2;

        internal static long Bump() => Interlocked.Increment(ref Cell[0]);

        internal static void Remember(string line) => Log.Add(line);

        internal static string FirstOfTable() => Table[0] + Frozen[0] + Label + Log.Count;

        internal static string? Setting { get; set; }

        internal static string Fixed { get; } = "x";

        // A lambda cache is generated state, not the type's.
        internal static int Sum(IEnumerable<int> values) => values.Select(static v => v + 1).Sum();
    }

    internal static class Factory
    {
        internal static IStateDomain Make() => new LinksDomain();
    }
}

namespace Ah.Core.Json
{
    // A public interface inside Ah.Core.Json is allowed on the Ah.Core row.
    public interface IPermittedSource
    {
    }

    // Stock serialization inside Ah.Core.Json is the one permitted place.
    internal static class PermittedWriter
    {
        internal static byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(2);
    }
}
