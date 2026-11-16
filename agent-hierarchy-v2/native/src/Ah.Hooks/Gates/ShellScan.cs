using System.Text;
using Ah.Core.Text;

namespace Ah.Hooks.Gates;

/// <summary>
/// Finds a roster close in shell text the <see cref="AhCli"/> grammar cannot read: a <c>cd … &amp;&amp;</c> or env prefix, a
/// relative script path, a <c>;</c> chain, <c>sh -c "…"</c>, a runtime option, another runtime. The text is scanned as command
/// text only. Heredoc bodies, quoted text and comments are excluded (a message that merely mentions the command runs
/// nothing), except where they are code: a heredoc whose command is a shell (or feeds one, or is a runtime reading its
/// program from stdin), the argument of <c>sh -c</c>, the arguments of <c>eval</c>, and <c>$( )</c> and backtick spans.
/// Text it cannot classify (an unterminated quote or heredoc, nesting deeper than eight levels) counts as a close.
/// </summary>
internal static partial class ShellScan
{
    private const int MaxNesting = 8;

    private static readonly string[] ShellWords = ["sh", "bash", "zsh", "dash", "ksh", "fish", "source", "."];

    private static readonly string[] Runtimes = ["node", "nodejs", "bun", "deno", "tsx"];

    private sealed class Unclassified(string message) : Exception(message);

    private sealed class Word(string text, List<string> raws)
    {
        public string Text { get; } = text;

        public List<string> Raws { get; } = raws;

        /// <summary>Source ranges of this word's quoted text that sh reads as plain text, never as code. Filled only when offsets are tracked.</summary>
        public List<(int Start, int End)> Literals { get; set; } = [];

        /// <summary>This word is the target of an output redirection, not an argument.</summary>
        public bool RedirectTarget { get; set; }

        /// <summary>This word is the file-descriptor number in front of an output redirection (the <c>2</c> of <c>2&gt;file</c>).</summary>
        public bool FdPrefix { get; set; }
    }

    private sealed class Cmd(List<Word> words, int group)
    {
        public List<Word> Words { get; } = words;

        public int Group { get; } = group;

        /// <summary>Source ranges of the heredoc bodies on this command that sh reads as plain text. Filled only when offsets are tracked.</summary>
        public List<(int Start, int End)> BodyLiterals { get; } = [];
    }

    /// <summary>
    /// A heredoc: <c>Quoted</c> when the delimiter word is wholly quoted (the body is not expanded); <c>Ambiguous</c> when the
    /// delimiter mixes quoting with other text, so this scanner and sh may disagree about where the body ends.
    /// </summary>
    private sealed record Heredoc(string Delimiter, bool Strip, int Group, Cmd Owner, bool Quoted, bool Ambiguous);

    /// <summary>Set by a scan that tracks offsets when it meets text it cannot place exactly.</summary>
    private sealed class Offsets
    {
        public bool Abandoned { get; set; }
    }

    private sealed record HereString(string Raw, int Group);

    private static string BaseOf(Word w)
    {
        var at = w.Text.LastIndexOf('/');
        return at < 0 ? w.Text : w.Text[(at + 1)..];
    }

    private static bool IsLineTerminator(char c) => c is '\n' or '\r' or '\u2028' or '\u2029';

    /// <summary><c>/^["']?(.*\/)?(node|nodejs|bun|deno|tsx)$/</c>.</summary>
    private static bool IsRuntimeWord(string text)
    {
        var rest = text.Length > 0 && text[0] is '"' or '\'' ? text[1..] : text;
        foreach (var name in Runtimes)
        {
            if (rest == name)
            {
                return true;
            }

            if (rest.EndsWith("/" + name, StringComparison.Ordinal) && !rest[..^name.Length].Any(IsLineTerminator))
            {
                return true;
            }
        }

        // the optional quote may instead be part of `.*`
        if (text.Length > 0 && text[0] is '"' or '\'')
        {
            foreach (var name in Runtimes)
            {
                if (text.EndsWith("/" + name, StringComparison.Ordinal) && !text[..^name.Length].Any(IsLineTerminator))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAssignment(string text)
    {
        if (text.Length == 0 || !(text[0] is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '=')
            {
                return true;
            }

            if (!(c is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>The index of the word that is the command proper: past <c>VAR=x</c> assignments and wrapper words (with their options).</summary>
    private static int HeadIndex(List<Word> words)
    {
        var i = 0;
        while (i < words.Count && IsAssignment(words[i].Text))
        {
            i++;
        }

        var texts = words.Select(static x => x.Text).ToList();
        while (i < words.Count)
        {
            if (IsAssignment(words[i].Text))
            {
                i++;
                continue;
            }

            if (Ah.Core.CoreModule.StepWrapper(texts, i) is not { } step)
            {
                return i;
            }

            if (step.Reliable)
            {
                i = step.Next;
                continue;
            }

            for (var j = i + 1; j < words.Count; j++)
            {
                if (ShellWords.Contains(BaseOf(words[j])) || BaseOf(words[j]) == "eval" || IsRuntimeWord(words[j].Text))
                {
                    return j;
                }
            }

            return i + 1;
        }

        return i;
    }

    /// <summary>Whether a command in this group hands its standard input to a shell, or to a runtime reading its program from stdin.</summary>
    private static bool ReadsStdinAsCode(IEnumerable<Cmd> members) =>
        members.Any(x =>
        {
            var k = HeadIndex(x.Words);
            if (k >= x.Words.Count)
            {
                return false;
            }

            var head = x.Words[k];
            return ShellWords.Contains(BaseOf(head))
                || (IsRuntimeWord(head.Text) && x.Words.Skip(k + 1).All(w => w.Text.StartsWith('-')));
        });

    private static bool IsPlainPiece(string raw)
    {
        // /^[^\s;&|()<>`'"\\]+$/
        if (raw.Length == 0)
        {
            return false;
        }

        foreach (var c in raw)
        {
            if (JsText.IsSpace(c) || c is ';' or '&' or '|' or '(' or ')' or '<' or '>' or '`' or '\'' or '"' or '\\')
            {
                return false;
            }
        }

        return true;
    }

    private static bool SeparatesWord(char c) => JsText.IsSpace(c) || c is ';' or '&' or '|' or '(' or ')' or '<' or '>';

    private static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    /// <param name="text">The shell text.</param>
    /// <param name="depth">The nesting level of <paramref name="text"/>.</param>
    /// <param name="track">When set, source offsets are kept (a continuation line becomes two spaces so they stay exact) and the literal ranges are recorded.</param>
    private static List<Cmd> CommandsOf(string text, int depth, Offsets? track = null)
    {
        if (depth > MaxNesting)
        {
            throw new Unclassified("nested too deep");
        }

        var src = text.Replace("\\\n", track is null ? " " : "  ", StringComparison.Ordinal);
        var cmds = new List<Cmd>();
        var pending = new List<Heredoc>();
        var hereStrings = new List<HereString>();
        var group = 0;
        // Commands found inside a nested span get ids of their own, so a pipeline's group id never moves on their account.
        var nested = 0;
        var cur = new Cmd([], group);
        var buf = new StringBuilder();
        var raws = new List<string>();
        var literals = new List<(int Start, int End)>();
        var hasWord = false;
        var redirectPending = false;
        var fdPending = false;
        var dupPending = false;
        var dupInput = false;

        void EndWord()
        {
            if (hasWord)
            {
                var dupWord = dupPending && (dupInput || IsFdWord(buf.ToString()));
                cur.Words.Add(new Word(buf.ToString(), raws) { RedirectTarget = redirectPending || (dupPending && !dupWord), FdPrefix = fdPending || dupWord, Literals = literals });
                redirectPending = false;
                dupPending = false;
                dupInput = false;
            }

            fdPending = false;

            buf.Clear();
            raws = [];
            literals = [];
            hasWord = false;
        }

        void EndCmd(bool newGroup)
        {
            EndWord();
            redirectPending = false;
            fdPending = false;
            dupPending = false;
            dupInput = false;
            if (cur.Words.Count > 0)
            {
                cmds.Add(cur);
            }

            if (newGroup)
            {
                group++;
            }

            cur = new Cmd([], group);
        }

        // A quoted piece joins the word's text only when it is one plain token, so a message with spaces never does.
        void Piece(string raw)
        {
            raws.Add(raw);
            if (IsPlainPiece(raw))
            {
                buf.Append(raw);
            }

            hasWord = true;
        }

        int CloseParen(int from)
        {
            for (int d = 1, k = from; k < src.Length; k++)
            {
                if (src[k] == '\\')
                {
                    k++;
                }
                else if (src[k] == '(')
                {
                    d++;
                }
                else if (src[k] == ')' && --d == 0)
                {
                    return k;
                }
            }

            throw new Unclassified("unterminated $(");
        }

        void AddNested(string code, int by)
        {
            foreach (var inner in CommandsOf(code, by))
            {
                cmds.Add(new Cmd(inner.Words, --nested));
            }
        }

        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            if (c == '\\')
            {
                if (i + 1 < src.Length)
                {
                    buf.Append(src[++i]);
                    hasWord = true;
                }
            }
            else if (c == '\'')
            {
                var e = src.IndexOf('\'', i + 1);
                if (e < 0)
                {
                    throw new Unclassified("unterminated quote");
                }

                Piece(src.Substring(i + 1, e - i - 1));
                // `$'…'` reads backslash escapes, so a quote can hide inside it; only a backslash-free one is certainly plain text.
                if (track is not null && !(At(src, i - 1) == '$' && src.AsSpan(i + 1, e - i - 1).Contains('\\')))
                {
                    literals.Add((i + 1, e));
                }

                i = e;
            }
            else if (c == '"')
            {
                var raw = new StringBuilder();
                var k = i + 1;
                for (; k < src.Length && src[k] != '"'; k++)
                {
                    if (src[k] == '\\' && k + 1 < src.Length)
                    {
                        k++;
                        if (src[k] is '\\' or '"' or '$' or '`')
                        {
                            raw.Append(src[k]);
                        }
                        else
                        {
                            raw.Append('\\').Append(src[k]);
                        }
                    }
                    else if (src[k] == '$' && ExpansionOpener(src, k) is var open and >= 0 && src[open] == '(')
                    {
                        var e = CloseParen(open + 1);
                        AddNested(src.Substring(open + 1, e - open - 1), depth + 1);
                        raw.Append(src, k, e + 1 - k);
                        k = e;
                    }
                    else if (src[k] == '`')
                    {
                        var e = src.IndexOf('`', k + 1);
                        if (e < 0)
                        {
                            throw new Unclassified("unterminated backtick");
                        }

                        AddNested(src.Substring(k + 1, e - k - 1), depth + 1);
                        raw.Append(src, k, e + 1 - k);
                        k = e;
                    }
                    else
                    {
                        raw.Append(src[k]);
                    }
                }

                if (k >= src.Length)
                {
                    throw new Unclassified("unterminated quote");
                }

                Piece(raw.ToString());
                if (track is not null && !HasExpansion(src.AsSpan(i + 1, k - i - 1)))
                {
                    literals.Add((i + 1, k));
                }

                i = k;
            }
            else if (c == '#' && !hasWord && (i == 0 || SeparatesComment(src[i - 1])))
            {
                while (i + 1 < src.Length && src[i + 1] != '\n')
                {
                    i++;
                }
            }
            else if (c is ' ' or '\t' or '\r')
            {
                EndWord();
            }
            else if (c == '\n')
            {
                EndCmd(true);
                var heredocs = pending.ToList();
                pending.Clear();
                foreach (var h in heredocs)
                {
                    var lines = new List<string>();
                    var closed = false;
                    var at = i + 1;
                    var bodyStart = at;
                    var terminatorAt = at;
                    while (at <= src.Length && !closed)
                    {
                        terminatorAt = at;
                        var eol = src.IndexOf('\n', at);
                        var line = src[at..(eol < 0 ? src.Length : eol)];
                        var compare = h.Strip ? line.TrimStart('\t') : line;
                        if (compare == h.Delimiter)
                        {
                            closed = true;
                        }
                        else
                        {
                            lines.Add(line);
                        }

                        if (eol < 0)
                        {
                            at = src.Length + 1;
                            break;
                        }

                        at = eol + 1;
                    }

                    if (!closed)
                    {
                        throw new Unclassified("unterminated heredoc");
                    }

                    i = at - 1;
                    var readsAsCode = ReadsStdinAsCode(cmds.Where(x => x.Group == h.Group));
                    if (readsAsCode)
                    {
                        AddNested(string.Join("\n", lines), depth);
                    }

                    if (track is not null)
                    {
                        if (h.Ambiguous)
                        {
                            track.Abandoned = true;
                        }
                        else if (!readsAsCode && (h.Quoted || !HasExpansion(src.AsSpan(bodyStart, terminatorAt - bodyStart))))
                        {
                            h.Owner.BodyLiterals.Add((bodyStart, terminatorAt));
                        }
                    }
                }
            }
            else if (c is '<' or '>')
            {
                if (c == '<' && At(src, i + 1) == '<' && At(src, i + 2) == '<')
                {
                    // A here-string: the word after it is the command's standard input.
                    EndWord();
                    i += 3;
                    while (At(src, i) is ' ' or '\t')
                    {
                        i++;
                    }

                    var raw = new StringBuilder();
                    if (At(src, i) is '\'' or '"')
                    {
                        var q = src[i];
                        var k = i + 1;
                        for (; k < src.Length && src[k] != q; k++)
                        {
                            if (q == '"' && src[k] == '\\' && k + 1 < src.Length)
                            {
                                k++;
                                if (src[k] is '\\' or '"' or '$' or '`')
                                {
                                    raw.Append(src[k]);
                                }
                                else
                                {
                                    raw.Append('\\').Append(src[k]);
                                }
                            }
                            else
                            {
                                raw.Append(src[k]);
                            }
                        }

                        if (k >= src.Length)
                        {
                            throw new Unclassified("unterminated here-string");
                        }

                        i = k;
                    }
                    else
                    {
                        while (i < src.Length && !SeparatesWord(src[i]))
                        {
                            raw.Append(src[i++]);
                        }

                        i--;
                    }

                    hereStrings.Add(new HereString(raw.ToString(), cur.Group));
                }
                else if (c == '<' && At(src, i + 1) == '<')
                {
                    EndWord();
                    i += 2;
                    var strip = At(src, i) == '-';
                    if (strip)
                    {
                        i++;
                    }

                    while (At(src, i) is ' ' or '\t')
                    {
                        i++;
                    }

                    var delimiter = new StringBuilder();
                    var quotedDelimiter = false;
                    var ambiguousDelimiter = false;
                    if (At(src, i) is '\'' or '"')
                    {
                        var e = src.IndexOf(src[i], i + 1);
                        if (e < 0)
                        {
                            throw new Unclassified("unterminated heredoc word");
                        }

                        delimiter.Append(src, i + 1, e - i - 1);
                        i = e;
                        quotedDelimiter = true;
                        ambiguousDelimiter = e + 1 < src.Length && !SeparatesWord(src[e + 1]);
                    }
                    else
                    {
                        while (i < src.Length && !SeparatesWord(src[i]))
                        {
                            delimiter.Append(src[i++]);
                        }

                        i--;
                        ambiguousDelimiter = delimiter.ToString().AsSpan().IndexOfAny('\'', '"', '\\') >= 0;
                    }

                    if (delimiter.Length == 0)
                    {
                        throw new Unclassified("heredoc without a word");
                    }

                    pending.Add(new Heredoc(delimiter.ToString(), strip, cur.Group, cur, quotedDelimiter, ambiguousDelimiter));
                }
                else
                {
                    var duplicate = At(src, i + 1) == '&';
                    var output = c == '>' && !duplicate;
                    fdPending = (output || duplicate) && hasWord && buf.ToString().All(char.IsAsciiDigit);
                    EndWord();
                    redirectPending = output;
                    dupPending = duplicate;
                    dupInput = duplicate && c == '<';
                }
            }
            else if (c is ';' or '(' or ')' or '`')
            {
                EndCmd(true);
            }
            else if (c is '{' or '}' && !hasWord && (i + 1 >= src.Length || SeparatesWord(src[i + 1])))
            {
                // A brace group's `{` and `}` stand alone; inside a word (`${VAR}`, `a{b,c}`) they are part of it.
                EndCmd(true);
            }
            else if (c == '&')
            {
                // `2>&1`, `>&2` and `&>` are redirections and `|&` is a pipe, not the end of a command.
                if (At(src, i - 1) is '>' or '<' or '|' || At(src, i + 1) == '>')
                {
                    continue;
                }

                if (At(src, i + 1) == '&')
                {
                    i++;
                }

                EndCmd(true);
            }
            else if (c == '|')
            {
                if (At(src, i + 1) == '|')
                {
                    i++;
                    EndCmd(true);
                }
                else
                {
                    var clobber = At(src, i - 1) == '>';
                    EndCmd(false);
                    redirectPending = clobber;
                }
            }
            else
            {
                buf.Append(c);
                hasWord = true;
            }
        }

        EndCmd(true);
        if (pending.Count > 0)
        {
            throw new Unclassified("heredoc without a body");
        }

        foreach (var hs in hereStrings)
        {
            if (ReadsStdinAsCode(cmds.Where(x => x.Group == hs.Group)))
            {
                AddNested(hs.Raw, depth + 1);
            }
        }

        // The argument of `sh -c` and the arguments of `eval` are code, so their quoted text is scanned.
        foreach (var cmd in cmds.ToList())
        {
            var w = cmd.Words;
            var at = HeadIndex(w);
            var code = new List<string>();
            var texts = w.Select(static x => x.Text).ToList();
            texts.Add(string.Empty);
            if (Ah.Core.CoreModule.WrapperScriptWord(texts, w.Count) is var (scriptAt, prefix))
            {
                if (scriptAt >= w.Count)
                {
                    code.AddRange(w[^1].Raws);
                }
                else if (prefix > 0)
                {
                    code.Add(texts[scriptAt][prefix..]);
                }
                else
                {
                    code.AddRange(w[scriptAt].Raws);
                    if (scriptAt > 0)
                    {
                        code.AddRange(w[scriptAt - 1].Raws);
                    }
                }
            }

            if (at >= w.Count)
            {
                foreach (var raw in code)
                {
                    AddNested(raw, depth + 1);
                }

                continue;
            }

            var head = BaseOf(w[at]);
            if (ShellWords.Contains(head))
            {
                for (var k = at + 1; k + 1 < w.Count; k++)
                {
                    if (IsCombinedC(w[k].Text))
                    {
                        code.AddRange(w[k + 1].Raws);
                    }
                }
            }
            else if (head == "eval")
            {
                foreach (var word in w.Skip(at + 1))
                {
                    code.AddRange(word.Raws);
                }
            }

            foreach (var raw in code)
            {
                AddNested(raw, depth + 1);
            }
        }

        return cmds;
    }

    // /[\s;&|()]/ on the character before a `#`
    private static bool SeparatesComment(char c) => JsText.IsSpace(c) || c is ';' or '&' or '|' or '(' or ')';

    /// <summary><c>/^-[A-Za-z]*c[A-Za-z]*$/</c>: an option group that contains <c>c</c>.</summary>
    private static bool IsCombinedC(string text)
    {
        if (text.Length < 2 || text[0] != '-')
        {
            return false;
        }

        var hasC = false;
        for (var i = 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
            {
                return false;
            }

            hasC |= ch == 'c';
        }

        return hasC;
    }
}
