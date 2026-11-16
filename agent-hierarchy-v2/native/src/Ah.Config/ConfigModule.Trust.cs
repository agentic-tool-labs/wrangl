namespace Ah.Config;

public static partial class ConfigModule
{
    /// <summary>The names of the two trust files, <c>trusted-homes</c> then <c>trusted-hierarchy-dirs</c>.</summary>
    public static IReadOnlyList<string> TrustFileNames { get; } = ["trusted-homes", "trusted-hierarchy-dirs"];
}
