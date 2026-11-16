using System.Text;
using System.Text.RegularExpressions;

namespace Ah.Core.Shell;

/// <summary>One command of a shell text: its words, where its program starts once assignments and wrapper words are stripped, and where it sits in the text.</summary>
/// <param name="Words">The words, quotes removed.</param>
/// <param name="Program">The index of the program word; equal to the word count when the command has none.</param>
/// <param name="Assigned">The names of the <c>VAR=value</c> assignments in front of the program.</param>
/// <param name="Nested">Whether the command was found inside the script of <c>sh -c</c> or <c>eval</c>.</param>
/// <param name="Wrappers">The kinds of the wrappers stepped over before the program, outermost first (<c>bucket</c> for the test bucket in any spelling).</param>
/// <param name="Start">Where the command starts in the text it was split from.</param>
/// <param name="End">Where the command ends in that text, exclusive.</param>
/// <param name="Reliable">False when a wrapper's options could not be stepped over; the command is then judged from every later word too (see <paramref name="Derived"/>).</param>
/// <param name="Derived">Whether the command is one of those later-word starts rather than a command of the text.</param>
public sealed record CommandSegment(
    IReadOnlyList<string> Words,
    int Program,
    IReadOnlyList<string> Assigned,
    bool Nested,
    IReadOnlyList<string> Wrappers,
    int Start,
    int End,
    bool Reliable,
    bool Derived)
{
    /// <summary>The program word's last path part, or empty.</summary>
    public string ProgramName => Program < Words.Count ? WrapperTable.BaseOf(Words[Program]) : string.Empty;

    /// <summary>The words after the program word.</summary>
    public IReadOnlyList<string> Rest => Program < Words.Count ? Words.Skip(Program + 1).ToList() : [];

    /// <summary>Where the program word starts in the text it was split from; -1 when unknown (no program word, or a later-word start).</summary>
    public int ProgramStart { get; init; } = -1;
}

/// <summary>
/// The one normaliser of shell text into commands. Best effort, not a shell: it splits on newlines, <c>;</c>, <c>&amp;&amp;</c>,
/// <c>||</c>, <c>|</c>, <c>&amp;</c>, parentheses and backticks outside quotes, drops comments and heredoc bodies, strips assignments,
/// shell keywords and the wrappers of <see cref="WrapperTable"/>, and reads the script of <c>sh -c</c> and <c>eval</c> again to a
/// depth of eight; text nested deeper than that is not examined.
/// </summary>
internal static partial class CommandSegments
{
    internal const int MaxDepth = 8;

    private static readonly string[] Keywords = ["if", "then", "else", "elif", "do", "while", "until", "!", "{", "}", "fi", "done"];

    [GeneratedRegex(@"^-[A-Za-z]*c[A-Za-z]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShellCommandFlag();

    /// <summary>Every command of <paramref name="text"/>, those inside <c>sh -c</c> and <c>eval</c> included.</summary>
    public static IReadOnlyList<CommandSegment> Of(string? text) => Of(text, out _);

    /// <summary>The same, and whether the text has more commands than the work allowed for: then the list is incomplete and a gate must not rely on it.</summary>
    public static IReadOnlyList<CommandSegment> Of(string? text, out bool unreadable)
    {
        var budget = new Budget();
        var result = Collect(text, 0, false, budget);
        unreadable = budget.Left < 0;
        return result;
    }

    private sealed class Budget
    {
        /// <summary>The commands that may still be collected from one text, scripts and later-word starts included.</summary>
        internal int Left = 4096;
    }

    /// <summary>The same normalisation for one command that is already split into words.</summary>
    public static IReadOnlyList<CommandSegment> OfWords(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var result = new List<CommandSegment>();
        if (words.Count == 0)
        {
            return result;
        }

        AddWithScript(result, words, false, -1, -1, 0, false, new Budget());
        return result;
    }

    /// <summary>The last path part of a word.</summary>
    public static string BaseOf(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return WrapperTable.BaseOf(word);
    }

    /// <summary>
    /// <paramref name="text"/> with each command that <paramref name="wrap"/> answers for (given the command and its own text) replaced by that answer; the script of a
    /// <c>sh -c</c> command is rewritten in place, with its shell kept. The text is returned as it was when nothing is replaced.
    /// </summary>
    public static string Rewrite(string text, Func<CommandSegment, string, string?> wrap, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(wrap);
        var sb = new StringBuilder();
        var last = 0;
        foreach (var (words, start, end, spans) in Split(text))
        {
            var seg = Normalise(words, depth > 0, start, end);
            var lead = 0;
            while (lead < words.Count - 1 && Keywords.Contains(words[lead]))
            {
                lead++;
            }

            var from = spans[lead].Start;
            var replacement = wrap(seg, text[from..end]);
            if (replacement is null && depth < MaxDepth && !seg.Wrappers.Contains("bucket") && Script(seg) is { } script && ShellScriptIndex(seg) is { } at)
            {
                var inner = Rewrite(script, wrap, depth + 1);
                if (inner != script)
                {
                    var word = spans[at];
                    _ = sb.Append(text, last, word.Start - last).Append(Quote(inner));
                    last = word.End;
                }
            }
            else if (replacement is not null)
            {
                _ = sb.Append(text, last, from - last).Append(replacement);
                last = end;
            }
        }

        return sb.Append(text, last, text.Length - last).ToString();
    }

    /// <summary>One word, quoted so that a shell reads it back as the same word.</summary>
    public static string Quote(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return word.Length > 0 && word.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' or ':' or ',' or '@' or '+' or '%')
            ? word
            : "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static List<CommandSegment> Collect(string? text, int depth, bool underBucket, Budget budget)
    {
        var result = new List<CommandSegment>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var (words, start, end, spans) in Split(text))
        {
            var at = result.Count;
            AddWithScript(result, words, depth > 0, start, end, depth, underBucket, budget);
            if (at < result.Count && result[at].Program < spans.Count)
            {
                result[at] = result[at] with { ProgramStart = spans[result[at].Program].Start };
            }
        }

        return result;
    }

    private static void AddWithScript(List<CommandSegment> result, IReadOnlyList<string> words, bool nested, int start, int end, int depth, bool underBucket, Budget budget)
    {
        if (budget.Left-- <= 0)
        {
            return;
        }

        var seg = Under(Normalise(words, nested, start, end), underBucket);
        result.Add(seg);
        var innerBucket = seg.Wrappers.Contains("bucket");
        if (!seg.Reliable)
        {
            for (var j = seg.Program + 1; j < words.Count; j++)
            {
                var suffix = words.Skip(j).ToList();
                if (budget.Left-- <= 0)
                {
                    return;
                }

                var derived = Under(Normalise(suffix, nested, start, end) with { Derived = true }, underBucket);
                result.Add(derived);
                if (depth < MaxDepth && Script(derived) is { } inner)
                {
                    result.AddRange(Collect(inner, depth + 1, innerBucket, budget));
                }
            }
        }

        if (depth < MaxDepth && Script(seg) is { } script)
        {
            result.AddRange(Collect(script, depth + 1, innerBucket, budget));
        }
    }

    private static CommandSegment Under(CommandSegment seg, bool underBucket) =>
        underBucket && !seg.Wrappers.Contains("bucket") ? seg with { Wrappers = [.. seg.Wrappers, "bucket"] } : seg;

    /// <summary>The index of the word holding the script a shell runs with <c>-c</c>, or null.</summary>
    private static int? ShellScriptIndex(CommandSegment seg)
    {
        if (!WrapperTable.IsShell(seg.ProgramName))
        {
            return null;
        }

        for (var i = seg.Program + 1; i + 1 < seg.Words.Count; i++)
        {
            if (ShellCommandFlag().IsMatch(seg.Words[i]))
            {
                return i + 1;
            }

            if (!seg.Words[i].StartsWith('-'))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The script a shell, <c>eval</c> or a wrapper option such as <c>env -S</c> runs, when it has one on its command line.</summary>
    private static string? Script(CommandSegment seg)
    {
        if (WrapperTable.ScriptWord(seg.Words, seg.Program) is var (at0, prefix))
        {
            return seg.Words[at0][prefix..];
        }

        if (seg.ProgramName == "eval")
        {
            var rest = seg.Rest;
            return rest.Count > 0 ? string.Join(' ', rest) : null;
        }

        return ShellScriptIndex(seg) is { } at ? seg.Words[at] : null;
    }

    private static CommandSegment Normalise(IReadOnlyList<string> words, bool nested, int start, int end)
    {
        var assigned = new List<string>();
        var wrappers = new List<string>();
        var reliable = true;
        var i = 0;
        while (i < words.Count)
        {
            var w = words[i];
            if (WrapperTable.IsAssignment(w))
            {
                assigned.Add(w[..w.IndexOf('=', StringComparison.Ordinal)]);
                i++;
                continue;
            }

            if (Keywords.Contains(w))
            {
                i++;
                continue;
            }

            if (WrapperTable.Step(words, i) is not { } step)
            {
                break;
            }

            wrappers.Add(step.Kind);
            for (var j = i + 1; j < step.Next && step.Kind == "env"; j++)
            {
                if (WrapperTable.IsAssignment(words[j]))
                {
                    assigned.Add(words[j][..words[j].IndexOf('=', StringComparison.Ordinal)]);
                }
            }

            i = step.Next;
            if (!step.Reliable)
            {
                reliable = false;
                break;
            }
        }

        return new CommandSegment(words, i, assigned, nested, wrappers, start, end, reliable, false);
    }

    /// <summary>The commands of <paramref name="t"/> as lists of words and their spans, quotes removed, comments and heredoc bodies dropped.</summary>
    private static List<(List<string> Words, int Start, int End, List<(int Start, int End)> Spans)> Split(string t)
    {
        var segments = new List<(List<string> Words, int Start, int End, List<(int Start, int End)> Spans)>();
        var words = new List<string>();
        var spans = new List<(int Start, int End)>();
        var cur = new StringBuilder();
        var has = false;
        var heredocs = new List<(string Delimiter, bool Strip)>();
        var i = 0;
        var wordStart = 0;
        var segStart = -1;
        var segEnd = 0;

        void Begin(int at)
        {
            if (!has)
            {
                wordStart = at;
                has = true;
            }
        }

        void EndWord(int at)
        {
            if (has)
            {
                words.Add(cur.ToString());
                spans.Add((wordStart, at));
                _ = cur.Clear();
                has = false;
                if (segStart < 0)
                {
                    segStart = wordStart;
                }

                segEnd = at;
            }
        }

        void EndSegment(int at)
        {
            EndWord(at);
            if (words.Count > 0)
            {
                segments.Add((words, segStart, segEnd, spans));
                words = [];
                spans = [];
            }

            segStart = -1;
        }

        while (i < t.Length)
        {
            var c = t[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < t.Length && t[i + 1] == '\n')
                    {
                        i += 2;
                        continue;
                    }

                    if (i + 1 < t.Length)
                    {
                        _ = cur.Append(t[i + 1]);
                        Begin(i);
                    }

                    i += 2;
                    continue;
                case '\'':
                    {
                        Begin(i);
                        var close = t.IndexOf('\'', i + 1);
                        var end = close < 0 ? t.Length : close;
                        _ = cur.Append(t, i + 1, end - i - 1);
                        i = end + 1;
                        continue;
                    }

                case '"':
                    Begin(i);
                    i++;
                    while (i < t.Length && t[i] != '"')
                    {
                        if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] is '"' or '\\' or '$' or '`')
                        {
                            i++;
                        }

                        _ = cur.Append(t[i]);
                        i++;
                    }

                    i++;
                    continue;
                case ' ' or '\t' or '\r':
                    EndWord(i);
                    break;
                case '\n':
                    EndSegment(i);
                    i = SkipHeredocs(t, i + 1, heredocs);
                    continue;
                case '#' when !has:
                    while (i < t.Length && t[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                case ';' or '|' or '(' or ')' or '`':
                    EndSegment(i);
                    break;
                case '&':
                    if ((i > 0 && t[i - 1] is '>' or '<') || (i + 1 < t.Length && t[i + 1] == '>'))
                    {
                        _ = cur.Append(c);
                        Begin(i);
                    }
                    else
                    {
                        EndSegment(i);
                    }

                    break;
                case '<' when i + 1 < t.Length && t[i + 1] == '<' && !(i + 2 < t.Length && t[i + 2] == '<'):
                    i = ReadHeredocDelimiter(t, i + 2, heredocs);
                    continue;
                default:
                    _ = cur.Append(c);
                    Begin(i);
                    break;
            }

            i++;
        }

        EndSegment(Math.Min(i, t.Length));
        return segments;
    }

    private static int ReadHeredocDelimiter(string t, int i, List<(string Delimiter, bool Strip)> heredocs)
    {
        var strip = i < t.Length && t[i] == '-';
        if (strip)
        {
            i++;
        }

        while (i < t.Length && t[i] is ' ' or '\t')
        {
            i++;
        }

        var d = new StringBuilder();
        while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] is not (';' or '|' or '&' or '(' or ')' or '<' or '>'))
        {
            if (t[i] is not ('\'' or '"' or '\\'))
            {
                _ = d.Append(t[i]);
            }

            i++;
        }

        if (d.Length > 0)
        {
            heredocs.Add((d.ToString(), strip));
        }

        return i;
    }

    /// <summary>The index of the first line after the bodies of the heredocs started on the line that just ended.</summary>
    private static int SkipHeredocs(string t, int i, List<(string Delimiter, bool Strip)> heredocs)
    {
        foreach (var (delimiter, strip) in heredocs)
        {
            while (i < t.Length)
            {
                var eol = t.IndexOf('\n', i);
                var line = eol < 0 ? t[i..] : t[i..eol];
                i = eol < 0 ? t.Length : eol + 1;
                if ((strip ? line.TrimStart('\t') : line).TrimEnd('\r') == delimiter)
                {
                    break;
                }
            }
        }

        heredocs.Clear();
        return i;
    }
}
