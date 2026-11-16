using Ah.Abstractions;
using Ah.Core.Time;

namespace Ah.Core.Tests;

/// <summary>The observer check that starts from the process that proved it last time (b8 #1: every liveness judgement read every registered pid).</summary>
public sealed class ObserverReferenceTests
{
    private const string Start = "Fri Nov  6 12:00:00 2026";
    private const int Reference = 900;

    private sealed class Table : IProcessTable
    {
        public Dictionary<long, string> Live { get; } = new() { [Reference] = Start };

        public int Reads { get; private set; }

        public IReadOnlyList<int>? Ancestors(int pid) => [1]; // a daemon: its parent is launchd, so only a registered process can prove it sees others

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid)
        {
            Reads++;
            return Live.GetValueOrDefault(pid);
        }
    }

    // the one live registration comes last, after a hundred whose processes are gone
    private static readonly (long Pid, string? Start)[] Registered = [.. Enumerable.Range(100, 100).Select(static p => ((long)p, (string?)Start)), (Reference, Start)];

    [Fact]
    public void ASecondCheckReadsOnlyTheProcessThatProvedTheFirst()
    {
        var table = new Table();
        var reference = new ObserverReference();
        Assert.True(reference.Sees(table, 50, Registered));
        var first = table.Reads;

        Assert.True(reference.Sees(table, 50, Registered));

        Assert.Equal(101, first);
        Assert.Equal(first + 1, table.Reads);
    }

    [Theory]
    [InlineData(true, false)] // the reference's pid now has another start: it is not the process that proved it
    [InlineData(false, true)] // the subject is the reference: the full check never counts the subject itself
    public void AReferenceThatNoLongerProvesItIsNotTakenAndTheFullCheckAnswers(bool reused, bool subjectIsReference)
    {
        var table = new Table();
        var reference = new ObserverReference();
        Assert.True(reference.Sees(table, 50, Registered));
        if (reused)
        {
            table.Live[Reference] = "Fri Nov  6 13:00:00 2026";
        }

        Assert.False(reference.Sees(table, 50, Registered, subjectIsReference ? Reference : null));
        Assert.False(LivenessRules.ObserverSeesAReference(table, 50, Registered, subjectIsReference ? Reference : null));
    }
}
