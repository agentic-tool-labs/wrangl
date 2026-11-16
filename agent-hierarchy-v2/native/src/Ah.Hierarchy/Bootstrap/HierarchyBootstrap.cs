using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Hierarchy.Bootstrap;

/// <summary>
/// Creates the layout of a hierarchy directory and its self-ignoring <c>.gitignore</c>. It is not a hub operation: it takes no
/// lock and has no op id, and a second run changes nothing. It holds the raw writer because no write context exists at the
/// call sites; this namespace is the one place in the assembly that may.
/// </summary>
internal sealed class RawHierarchyBootstrap(IRawFileWriter writer) : IHierarchyBootstrap
{
    private static readonly byte[] IgnoreAll = Utf8Text.Encode("*\n");

    public string EnsureHierarchyDir(string dir)
    {
        ArgumentNullException.ThrowIfNull(dir);
        writer.Mkdir(NodePath.Join(dir, "specs"));

        // An existing .gitignore is left as it is.
        writer.CreateExclusive(NodePath.Join(dir, ".gitignore"), IgnoreAll);
        return dir;
    }
}
