using Ah.Core.Json;

namespace Ah.Tui.Core;

/// <summary>A list row on screen: its 0-based screen line and its index among the node's children.</summary>
internal sealed record HitRow(int Y, int Index);

/// <summary>A breadcrumb segment on screen row 0: the columns it covers (end exclusive) and the path it jumps to.</summary>
internal sealed record HitCrumb(int X0, int X1, string Path);

/// <summary>Where the things on screen are, so a mouse report can be mapped back to the model.</summary>
internal sealed record Hit(IReadOnlyList<HitRow> Rows, IReadOnlyList<HitCrumb> Crumbs)
{
    internal static Hit Empty { get; } = new([], []);
}

/// <summary>Reading a served document.</summary>
internal static class TuiRender
{
    internal static JsonValue? M(JsonValue? v, string key) => v is { IsMap: true } m ? m.Member(key) : null;

    internal static bool Truthy(JsonValue? v) => v is { IsTruthy: true };
}
