namespace Ah.Cli.Tests;

/// <summary>A process table for the stop and quit tests.</summary>
public sealed partial class RosterTeamVerbTests
{
    /// <summary>A table that knows no process: nothing is an ancestor, nothing has a start, so the stop finds no job to signal.</summary>
    private sealed class NoAncestry : Ah.Abstractions.IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => null;
    }
}
