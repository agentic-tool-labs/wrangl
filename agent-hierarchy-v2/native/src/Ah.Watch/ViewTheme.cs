using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>
/// The palette every presentation layer draws with, served as data: one row per token, with its truecolor hex, its 256-colour
/// index and what stands in for it when colour is off. A layer maps a token to its own medium and never picks a colour itself.
/// </summary>
internal static class ViewTheme
{
    internal const int Schema = 1;

    private sealed record Token(string Name, string Hex, int Index, string? Glyph, bool Bold = false, bool Dim = false, bool Reverse = false);

    private static readonly Token[] Tokens =
    [
        new("role.orchestrator", "#E5C07B", 180, "◆", Bold: true),
        new("role.sub-orchestrator", "#A9A4F2", 147, "◇", Bold: true),
        new("role.architect", "#C678DD", 176, "▲"),
        new("role.implementor", "#61AFEF", 75, "■"),
        new("role.reviewer", "#98C379", 114, "✓"),
        new("role.docs-writer", "#56B6C2", 73, "¶"),
        new("role.ux-engineer", "#FA9D68", 215, "◈"),
        new("role.ultra-advisor", "#E06C75", 168, "✦"),
        new("role.task-runner", "#ABB2BF", 145, "·"),
        new("role.custom", "#D7BA7D", 179, "◇"),
        new("state.live", "#98C379", 114, "●"),
        new("state.busy", "#E5C07B", 180, "◐"),
        new("state.idle", "#5C6370", 59, "○"),
        new("state.blocked", "#E06C75", 168, "×", Bold: true),
        new("state.orphaned", "#D19A66", 173, "⊘"),
        new("state.notlive", "#4B5263", 240, "·", Dim: true),
        new("state.failed", "#BE5046", 131, "✗", Bold: true),
        new("state.held", "#B294BB", 139, "‖"),
        new("surface", "#1E2127", 234, null),
        new("rail", "#2C313A", 236, null),
        new("border", "#3E4451", 238, "─│╭╮╰╯"),
        new("accent", "#528BFF", 69, null, Reverse: true),
        new("mail.waiting", "#D19A66", 173, "✉"),
        new("mail.fresh", "#F0C674", 222, "✉", Bold: true),
        new("mail.aged", "#8A6F45", 95, "✉", Dim: true),
        new("edge.mesh", "#7F848E", 102, "─"),
        new("edge.subagent", "#636D83", 60, "·"),
        new("edge.team", "#4B5263", 240, "─", Dim: true),
    ];

    /// <summary>The <c>theme</c> object of the root view body.</summary>
    internal static JsonObject Build()
    {
        var tokens = new JsonArray();
        foreach (var t in Tokens)
        {
            tokens.Add(new JsonObject
            {
                ["token"] = t.Name,
                ["hex"] = t.Hex,
                ["c256"] = t.Index,
                ["mono"] = new JsonObject
                {
                    ["glyph"] = t.Glyph is null ? JsonValue.Null : t.Glyph,
                    ["bold"] = t.Bold,
                    ["dim"] = t.Dim,
                    ["reverse"] = t.Reverse,
                },
            });
        }

        return new JsonObject { ["schema"] = Schema, ["tokens"] = tokens };
    }
}
