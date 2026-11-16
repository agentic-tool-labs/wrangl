using System.Globalization;
using System.Text.RegularExpressions;

namespace Ah.Tui.Core;

/// <summary>A key or a mouse report, as the client's input layer decoded it.</summary>
internal abstract record InputEvent;

/// <summary>A key by name: <c>up</c>, <c>enter</c>, <c>esc</c>, <c>ctrl-c</c>, or the character itself.</summary>
internal sealed record KeyEvent(string Name) : InputEvent;

/// <summary>
/// A mouse report: 0-based screen position and kind: <c>click</c> (left press), <c>right</c>, <c>move</c> (no button held), <c>drag</c>
/// (left button held), <c>wheelup</c>, <c>wheeldown</c>, <c>wheelleft</c>, <c>wheelright</c>, or <c>other</c> (a release, the middle button,
/// another button's drag).
/// </summary>
internal sealed record MouseEvent(int X, int Y, string Kind) : InputEvent;

/// <summary>What the I/O layer must do after a key: refresh or exit.</summary>
internal abstract record NavAction;

internal sealed record RefreshAction : NavAction;

internal sealed record QuitAction : NavAction;

/// <summary>The decoding of terminal input into events.</summary>
internal static partial class TuiNav
{
    [GeneratedRegex("^\u001b\\[<(\\d+);(\\d+);(\\d+)([Mm])", RegexOptions.CultureInvariant)]
    private static partial Regex SgrMouseRe();

    [GeneratedRegex("^\u001b\\[(?:1;\\d)?([ABCDHF])", RegexOptions.CultureInvariant)]
    private static partial Regex CsiArrowRe();

    [GeneratedRegex("^\u001bO([ABCDHF])", RegexOptions.CultureInvariant)]
    private static partial Regex Ss3ArrowRe();

    [GeneratedRegex("^\u001b\\[(\\d+)(?:;\\d+)?~", RegexOptions.CultureInvariant)]
    private static partial Regex TildeRe();

    [GeneratedRegex("^\u001b\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnyCsiRe();

    /// <summary>Splits a chunk of terminal input into events.</summary>
    internal static List<InputEvent> ParseInput(string chunk)
    {
        var events = new List<InputEvent>();
        var i = 0;
        while (i < chunk.Length)
        {
            var rest = chunk[i..];
            Match m;
            if ((m = SgrMouseRe().Match(rest)).Success)
            {
                var b = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var x = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1;
                var y = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) - 1;
                events.Add(new MouseEvent(x, y, MouseKind(b, m.Groups[4].Value == "M")));
                i += m.Length;
            }
            else if ((m = CsiArrowRe().Match(rest)).Success || (m = Ss3ArrowRe().Match(rest)).Success)
            {
                events.Add(new KeyEvent(ArrowName(m.Groups[1].Value[0])));
                i += m.Length;
            }
            else if ((m = TildeRe().Match(rest)).Success)
            {
                var name = m.Groups[1].Value switch
                {
                    "1" or "7" => "home",
                    "4" or "8" => "end",
                    "5" => "pageup",
                    "6" => "pagedown",
                    _ => null,
                };
                if (name is not null)
                {
                    events.Add(new KeyEvent(name));
                }

                i += m.Length;
            }
            else if ((m = AnyCsiRe().Match(rest)).Success)
            {
                i += m.Length;
            }
            else if (rest[0] == '\u001b')
            {
                events.Add(new KeyEvent("esc"));
                i += 1;
            }
            else
            {
                var ch = TuiText.CodePoints(rest)[0];
                var name = ch is "\r" or "\n" ? "enter" : ch is "\u007f" or "\b" ? "backspace" : ch == "\u0003" ? "ctrl-c" : ch == "\t" ? "tab" : ch;
                events.Add(new KeyEvent(name));
                i += ch.Length;
            }
        }

        return events;
    }

    /// <summary>The kind of an SGR report with button field <paramref name="b"/>; bits 4, 8 and 16 (shift, meta, ctrl) do not change it.</summary>
    private static string MouseKind(int b, bool press)
    {
        if ((b & 64) != 0)
        {
            return (b & 3) switch
            {
                0 => "wheelup",
                1 => "wheeldown",
                2 => "wheelleft",
                _ => "wheelright",
            };
        }

        if (!press)
        {
            return "other";
        }

        var button = b & 3;
        if ((b & 32) != 0)
        {
            return button == 3 ? "move" : button == 0 ? "drag" : "other";
        }

        return button == 0 ? "click" : button == 2 ? "right" : "other";
    }

    private static string ArrowName(char c) => c switch
    {
        'A' => "up",
        'B' => "down",
        'C' => "right",
        'D' => "left",
        'H' => "home",
        _ => "end",
    };
}
