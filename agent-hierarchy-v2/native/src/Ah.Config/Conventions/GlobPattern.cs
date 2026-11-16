namespace Ah.Config;

/// <summary>A compiled conventions glob.</summary>
public interface IGlobPattern
{
    /// <summary>Whether the whole of <paramref name="text"/> matches.</summary>
    bool IsMatch(string text);
}

/// <summary>
/// A conventions glob, matched over repo-relative POSIX paths or branch names without a regular-expression engine.
/// <c>*</c> is any run of characters but <c>/</c>, <c>?</c> is one character but <c>/</c>, and a whole segment <c>**</c> is any
/// number of directories: the glob alone matches anything on one line, a leading one is zero or more leading directories, a
/// trailing one is the directory itself or anything below it on one line, and a middle one is zero or more directories
/// between two slashes. Every other character is literal. A match must cover the whole text.
/// </summary>
internal sealed class GlobPattern : IGlobPattern
{
    private enum Kind : byte
    {
        Literal,
        One,
        Star,
        AnyRun,
        Directories,
        Below,
    }

    private readonly record struct Token(Kind Kind, char Char);

    private readonly Token[] _tokens;

    private GlobPattern(Token[] tokens) => _tokens = tokens;

    /// <summary>The glob compiled to its matching steps.</summary>
    internal static GlobPattern Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var segments = pattern.Split('/');
        var tokens = new List<Token>();
        for (var i = 0; i < segments.Length; i++)
        {
            var first = i == 0;
            var last = i == segments.Length - 1;
            var segment = segments[i];
            if (segment == "**")
            {
                if (first && last)
                {
                    tokens.Add(new Token(Kind.AnyRun, '\0'));
                }
                else if (first)
                {
                    tokens.Add(new Token(Kind.Directories, '\0'));
                }
                else if (last)
                {
                    tokens.Add(new Token(Kind.Below, '\0'));
                }
                else
                {
                    tokens.Add(new Token(Kind.Literal, '/'));
                    tokens.Add(new Token(Kind.Directories, '\0'));
                }

                continue;
            }

            if (!first && segments[i - 1] != "**")
            {
                tokens.Add(new Token(Kind.Literal, '/'));
            }

            foreach (var c in segment)
            {
                tokens.Add(c switch
                {
                    '*' => new Token(Kind.Star, '\0'),
                    '?' => new Token(Kind.One, '\0'),
                    _ => new Token(Kind.Literal, c),
                });
            }
        }

        return new GlobPattern([.. tokens]);
    }

    public bool IsMatch(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var seen = new sbyte[_tokens.Length * (text.Length + 1)];
        return Match(0, 0, text, seen);
    }

    private static bool LineTerminator(char c) => c is '\n' or '\r' or '\u2028' or '\u2029';

    private bool Match(int token, int at, string text, sbyte[] seen)
    {
        if (token == _tokens.Length)
        {
            return at == text.Length;
        }

        var key = (token * (text.Length + 1)) + at;
        if (seen[key] != 0)
        {
            return seen[key] > 0;
        }

        var matched = Step(token, at, text, seen);
        seen[key] = matched ? (sbyte)1 : (sbyte)-1;
        return matched;
    }

    private bool Step(int token, int at, string text, sbyte[] seen)
    {
        var t = _tokens[token];
        switch (t.Kind)
        {
            case Kind.Literal:
                return at < text.Length && text[at] == t.Char && Match(token + 1, at + 1, text, seen);
            case Kind.One:
                return at < text.Length && text[at] != '/' && Match(token + 1, at + 1, text, seen);
            case Kind.Star:
                for (var k = at; ; k++)
                {
                    if (Match(token + 1, k, text, seen))
                    {
                        return true;
                    }

                    if (k >= text.Length || text[k] == '/')
                    {
                        return false;
                    }
                }

            case Kind.AnyRun:
                return AnyRunFrom(token + 1, at, text, seen);
            case Kind.Directories:
                if (Match(token + 1, at, text, seen))
                {
                    return true;
                }

                // One more directory: a non-empty run up to the next slash, then the slash.
                var slash = text.IndexOf('/', at);
                return slash > at && Match(token, slash + 1, text, seen);
            default:
                if (Match(token + 1, at, text, seen))
                {
                    return true;
                }

                return at < text.Length && text[at] == '/' && AnyRunFrom(token + 1, at + 1, text, seen);
        }
    }

    /// <summary>Whether the rest matches after any run of characters that are not line terminators, starting at <paramref name="from"/>.</summary>
    private bool AnyRunFrom(int next, int from, string text, sbyte[] seen)
    {
        for (var k = from; ; k++)
        {
            if (Match(next, k, text, seen))
            {
                return true;
            }

            if (k >= text.Length || LineTerminator(text[k]))
            {
                return false;
            }
        }
    }
}
