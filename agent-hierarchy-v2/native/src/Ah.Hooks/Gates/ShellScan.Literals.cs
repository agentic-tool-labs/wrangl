namespace Ah.Hooks.Gates;

internal static partial class ShellScan
{
    /// <summary>
    /// Whether quoted text holds a <c>$(</c>, <c>${</c> or backtick. Such text can run a command, and the scan does not follow where
    /// the expansion ends, so all of the text counts as command text.
    /// </summary>
    private static bool HasExpansion(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            // A backslash in double quotes or an unquoted heredoc body makes the next character literal, so an escaped backtick or dollar expands nothing.
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '`' || ExpansionOpener(text, i) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The index of the <c>(</c> or <c>{</c> that opens an expansion started by the <c>$</c> at <paramref name="dollar"/>, or -1.
    /// The scan has already replaced each backslash-newline with blanks, which sh joins away, so <c>$</c> + blanks + <c>(</c> can be
    /// a continued <c>$(</c>; blanks and any backslash-newline still present are skipped, which also counts a plain <c>$ (</c> as one.
    /// </summary>
    private static int ExpansionOpener(ReadOnlySpan<char> text, int dollar)
    {
        if (text[dollar] != '$')
        {
            return -1;
        }

        var next = dollar + 1;
        while (next < text.Length && (text[next] == ' ' || (next + 1 < text.Length && text[next] == '\\' && text[next + 1] == '\n')))
        {
            next += text[next] == ' ' ? 1 : 2;
        }

        return next < text.Length && text[next] is '(' or '{' ? next : -1;
    }
}
