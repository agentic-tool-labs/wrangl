using System.Globalization;

namespace Ah.Tui.Core;

/// <summary>How many colours the terminal is written in, decided once at start.</summary>
internal enum ColorDepth
{
    None,
    Sixteen,
    Palette256,
    TrueColor,
}

/// <summary>A colour as red, green and blue.</summary>
internal readonly record struct TuiRgb(int R, int G, int B)
{
    internal static TuiRgb Hex(string hex) => new(
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}

/// <summary>The colour rule: the depth, the one palette of roles, states and the title, and the one map from a colour to its escape.</summary>
internal static class TuiTheme
{
    /// <summary>Whether colour is used: <c>NO_COLOR</c> set and non-empty turns it off; an empty value keeps it.</summary>
    internal static bool UseColor(string? noColor) => string.IsNullOrEmpty(noColor);

    /// <summary>None for <c>NO_COLOR</c> or <c>TERM=dumb</c>; 24-bit for <c>COLORTERM</c> truecolor or 24bit; 256 for a <c>TERM</c> with 256color; else 16.</summary>
    internal static ColorDepth Depth(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var term = env("TERM") ?? string.Empty;
        return !UseColor(env("NO_COLOR")) || term == "dumb" ? ColorDepth.None
            : env("COLORTERM") is "truecolor" or "24bit" ? ColorDepth.TrueColor
            : term.Contains("256color", StringComparison.Ordinal) ? ColorDepth.Palette256
            : ColorDepth.Sixteen;
    }

    /// <summary>The product name in the title line: the mascot's orange.</summary>
    internal static readonly TuiRgb Title = TuiRgb.Hex("#d2693c");

    internal static readonly TuiRgb Working = TuiRgb.Hex("#8fc1ff");
    internal static readonly TuiRgb Blocked = TuiRgb.Hex("#f26d6d");
    internal static readonly TuiRgb Attention = TuiRgb.Hex("#f0b44c");
    internal static readonly TuiRgb Ok = TuiRgb.Hex("#45b26b");

    private static readonly TuiRgb OtherRole = TuiRgb.Hex("#a7b0ba");

    /// <summary>The web UI's role colours (<c>web/ui/ui.css</c> <c>--role-*</c>, dark values).</summary>
    private static readonly Dictionary<string, TuiRgb> Roles = new(StringComparer.Ordinal)
    {
        ["architect"] = TuiRgb.Hex("#3cc8b4"),
        ["implementor"] = TuiRgb.Hex("#56c8e8"),
        ["ux-engineer"] = TuiRgb.Hex("#fa9d68"),
        ["reviewer"] = TuiRgb.Hex("#e47bd1"),
        ["ultra-advisor"] = TuiRgb.Hex("#c9cf55"),
        ["qa"] = TuiRgb.Hex("#9ccf5a"),
        ["sub-orchestrator"] = TuiRgb.Hex("#a9a4f2"),
    };

    /// <summary>A role's colour; the orchestrator keeps the terminal's own.</summary>
    internal static TuiRgb? Role(string? role) => role == "orchestrator" ? null : role is not null && Roles.TryGetValue(role, out var c) ? c : OtherRole;

    /// <summary>A status tone's colour; idle has none.</summary>
    internal static TuiRgb? Tone(string tone) => tone switch
    {
        "bad" => Blocked,
        "warn" => Attention,
        "work" => Working,
        "ok" => Ok,
        _ => null,
    };

    /// <summary>The xterm default 16 colours, in SGR order.</summary>
    private static readonly TuiRgb[] Basic =
    [
        new(0, 0, 0), new(205, 0, 0), new(0, 205, 0), new(205, 205, 0), new(0, 0, 238), new(205, 0, 205), new(0, 205, 205), new(229, 229, 229),
        new(127, 127, 127), new(255, 0, 0), new(0, 255, 0), new(255, 255, 0), new(92, 92, 255), new(255, 0, 255), new(0, 255, 255), new(255, 255, 255),
    ];

    private static readonly int[] CubeLevels = [0, 95, 135, 175, 215, 255];

    /// <summary>The SGR parameters that paint <paramref name="c"/> as the foreground (or background) at <paramref name="depth"/>; empty with none.</summary>
    internal static string Code(TuiRgb c, ColorDepth depth, bool background = false)
    {
        switch (depth)
        {
            case ColorDepth.TrueColor:
                return string.Create(CultureInfo.InvariantCulture, $"{(background ? 48 : 38)};2;{c.R};{c.G};{c.B}");
            case ColorDepth.Palette256:
                return string.Create(CultureInfo.InvariantCulture, $"{(background ? 48 : 38)};5;{Nearest(c, 16, 256, Of256)}");
            case ColorDepth.Sixteen:
                var i = Nearest(c, 0, 16, n => Basic[n]);
                return (i < 8 ? (background ? 40 : 30) + i : (background ? 100 : 90) + i - 8).ToString(CultureInfo.InvariantCulture);
            default:
                return string.Empty;
        }
    }

    /// <summary>The colour of a 256-palette index from 16 on: the 6x6x6 cube, then the grey ramp.</summary>
    private static TuiRgb Of256(int n)
    {
        if (n >= 232)
        {
            var v = 8 + (10 * (n - 232));
            return new(v, v, v);
        }

        var k = n - 16;
        return new(CubeLevels[k / 36], CubeLevels[k / 6 % 6], CubeLevels[k % 6]);
    }

    /// <summary>The index in [from, to) whose colour is nearest <paramref name="c"/>, by plain RGB distance; the first wins a tie.</summary>
    private static int Nearest(TuiRgb c, int from, int to, Func<int, TuiRgb> colour)
    {
        var (best, bestD) = (from, int.MaxValue);
        for (var n = from; n < to; n++)
        {
            var p = colour(n);
            var d = ((p.R - c.R) * (p.R - c.R)) + ((p.G - c.G) * (p.G - c.G)) + ((p.B - c.B) * (p.B - c.B));
            if (d < bestD)
            {
                (best, bestD) = (n, d);
            }
        }

        return best;
    }
}
