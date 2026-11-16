using System.Globalization;

namespace Ah.Abstractions;

/// <summary>
/// A headless member runs one <c>claude -p</c> turn as a process of its own, with no pane. Its <c>transport_id</c> is
/// <c>headless:&lt;pid&gt;</c>, which names a process and never a pane, so a reader that looks panes up must skip it.
/// </summary>
public static class HeadlessTransport
{
    public const string Name = "headless";

    private const string Prefix = Name + ":";

    public static string Id(int pid) => Prefix + pid.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="transportId"/> names a headless process, as opposed to a pane.</summary>
    public static bool IsId(string? transportId) => transportId is not null && transportId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The pane <paramref name="transportId"/> names, or null when it is empty or names a headless process.</summary>
    public static string? PaneOf(string? transportId) => string.IsNullOrEmpty(transportId) || IsId(transportId) ? null : transportId;
}
