namespace Ah.Core.Text;

/// <summary>
/// The v1 plugin's state home. This is the one place the v2 sources spell a v1 path: the home refusals name it, and the
/// layout check allows no other file to.
/// </summary>
public static class V1Layout
{
    /// <summary>The v1 plugin's default state home under <paramref name="userHome"/>.</summary>
    public static string HomeUnder(string userHome) => NodePath.Join(userHome, ".claude", "agent-hierarchy");

    /// <summary>
    /// Whether <paramref name="real"/> is <paramref name="v1Real"/> or below it, by path segment: a sibling that only starts with
    /// the same name is not under it. Both arguments are real paths.
    /// </summary>
    public static bool IsAtOrUnder(string v1Real, string real) =>
        real == v1Real || real.StartsWith(v1Real.TrimEnd('/') + "/", StringComparison.Ordinal);
}
