using Ah.Cli.Verbs.Msg;

namespace Ah.Cli.Tests;

/// <summary>A daemon's error text is printed to a terminal, so it must carry no control or format characters.</summary>
public sealed class DaemonErrorTextTests
{
    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void EscapeSequencesAndControlsAreStripped() =>
        Assert.Equal("red[31m text", MsgCommand.Inert("red\u001b[31m\u0007\r\n\t text"));

    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void BidirectionalOverridesAndOtherFormatCharactersAreStripped() =>
        Assert.Equal("ab", MsgCommand.Inert("a‮b​⁦"));

    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void OrdinaryTextIncludingNonAsciiLettersIsKept() =>
        Assert.Equal("no such address: tm-é@tm", MsgCommand.Inert("no such address: tm-é@tm"));
}
