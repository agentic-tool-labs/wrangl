using Ah.Commands;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary>An owner proof whose process-ancestry walk always finds a Claude session at <paramref name="pid"/>.</summary>
internal sealed class SessionAncestorProof(int pid) : IRosterOwnerProof
{
    public OwnerProofResult Check(string dir, string? team) => new(true, null, "orchestrator", false, null);

    public OwnerActor Actor() => new(4242, "Thu Oct 15 20:38:50 2026", [], "ah roster gate");

    public NearestSession NearestAncestor(string dir, IReadOnlyList<string> rowDirs) => new(NearestKind.Session, null, null, pid);
}
