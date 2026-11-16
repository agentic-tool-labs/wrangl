using Ah.Abstractions;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>
/// A session directory whose process table shows <see cref="Pid"/> as a live Claude harness with launchd above it, and these
/// live registrations: what a SessionStart needs to see before it writes anything.
/// </summary>
internal sealed class HarnessSessions(params SessionRegistration[] live) : IGateSessions
{
    public const int Pid = 4242;

    public const string Start = "Sat Oct 17 12:00:00 2026";

    public Dictionary<int, string> Commands { get; } = new() { [Pid] = "claude --agent wrangl:implementor" };

    public Dictionary<int, string> Starts { get; } = new() { [Pid] = Start };

    /// <summary>The ancestor chain a pid answers, where it should have a parent other than pid 1.</summary>
    public Dictionary<int, IReadOnlyList<int>> Ancestry { get; } = [];

    public IProcessTable Table => new FakeTable(this);

    public IReadOnlyList<TeamOrchestrator> Teams(string hierarchyDir) => [];

    public IReadOnlyList<RegisteredSession> Registered() => [];

    IReadOnlyList<SessionRegistration> IGateSessions.Sessions() => live;

    private sealed class FakeTable(HarnessSessions owner) : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) =>
            owner.Ancestry.TryGetValue(pid, out var chain) ? chain
            : owner.Starts.ContainsKey(pid) ? [1]
            : pid == Environment.ProcessId ? [Pid, 1]
            : null;

        public bool? HasAncestor(int pid, int candidate) => Ancestors(pid)?.Contains(candidate);

        public string? StartTime(long pid) => owner.Starts.TryGetValue((int)pid, out var start) ? start : null;

        public string? Command(int pid) => owner.Commands.TryGetValue(pid, out var command) ? command : null;
    }
}
