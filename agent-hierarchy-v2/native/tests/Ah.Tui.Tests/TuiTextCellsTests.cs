using Ah.Tui.Core;

namespace Ah.Tui.Tests;

public sealed class TuiTextCellsTests
{
    [Theory]
    [InlineData("abc", 3)]
    [InlineData("漢字", 4)]
    [InlineData("ｱ", 1)] // negative: halfwidth katakana is narrow
    [InlineData("🙂", 2)]
    [InlineData("🇯🇵", 2)]
    [InlineData("🇯", 1)] // negative: a lone regional indicator is no flag
    [InlineData("👩‍💻", 2)]
    [InlineData("⚠️", 2)]
    [InlineData("⚠", 1)] // negative: the same sign with no emoji selector
    [InlineData("é", 1)]
    [InlineData("─│▸✓…", 5)] // negative: the client's own glyphs stay narrow
    public void WidthCountsCells(string text, int cells) => Assert.Equal(cells, TuiText.Width(text));

    [Fact]
    public void FitEndsWithinTheCellsAndNeverCutsAWideCharacter()
    {
        Assert.Equal("漢字…", TuiText.Fit("漢字漢字", 5));
        Assert.Equal("漢…", TuiText.Fit("漢字漢字", 4));
        Assert.Equal("🇯🇵…", TuiText.Fit("🇯🇵🇯🇵", 3));
        Assert.Equal("漢字", TuiText.Fit("漢字", 4)); // negative: what fits is not cut
        Assert.Equal("abcd…", TuiText.Fit("abcdefgh", 5)); // negative: ASCII as before
    }

    [Fact]
    public void PadAlwaysFillsExactlyTheCells()
    {
        Assert.Equal("漢 ", TuiText.Pad("漢字", 3));
        Assert.Equal(" 漢", TuiText.PadLeft("漢字", 3));
        Assert.Equal("漢  ", TuiText.Pad("漢", 4));
        Assert.Equal("  漢", TuiText.PadLeft("漢", 4));
        Assert.Equal("abc", TuiText.Pad("abcd", 3)); // negative: ASCII as before
    }

    [Fact]
    public void WrapBreaksByCellsAndAlwaysMovesOn()
    {
        Assert.Equal(["漢", "字a"], TuiText.Wrap("漢字a", 3));
        Assert.Equal(["漢", "字"], TuiText.Wrap("漢字", 1)); // a character wider than the line still takes one
        Assert.Equal(["abc", "d"], TuiText.Wrap("abcd", 3)); // negative: ASCII as before
        Assert.Equal([string.Empty], TuiText.Wrap(string.Empty, 3));
    }

    [Fact]
    public void TakeAndSkipSplitByCellsAndAWideCharacterOnTheEdgeLeavesASpace()
    {
        Assert.Equal("a", TuiText.Take("a漢b", 2));
        Assert.Equal(" b", TuiText.Skip("a漢b", 2));
        Assert.Equal("b", TuiText.Skip("a漢b", 3)); // negative: an edge between characters leaves no space
    }
}
