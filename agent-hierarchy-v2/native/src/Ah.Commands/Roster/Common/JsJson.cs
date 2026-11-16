using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>JSON.parse</c> of a flag value, with the message a JavaScript engine gives for text that is not JSON: the roster verbs print
/// it as the failure, so a caller that matches on it sees the same words from either implementation.
/// </summary>
internal static class JsJson
{
    /// <summary>The parsed value; text that is not JSON fails with the engine's message.</summary>
    internal static JsonValue Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Describe(text) is { } message)
        {
            throw new RosterFailure(message);
        }

        return JsonText.Parse(text);
    }

    /// <summary>Null when <paramref name="text"/> is JSON, else the message <c>JSON.parse</c> throws for it.</summary>
    internal static string? Describe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            new Scanner(text).Run();
            return null;
        }
        catch (Stop stop)
        {
            return stop.Message;
        }
    }

    private sealed class Stop(string message) : Exception(message);

    private sealed class Scanner(string text)
    {
        private const int ContextCharacters = 10;
        private int _pos;

        internal void Run()
        {
            Value();
            SkipWhitespace();
            if (_pos < text.Length)
            {
                throw At("Unexpected non-whitespace character after JSON", _pos, inJson: false);
            }
        }

        private void SkipWhitespace()
        {
            while (_pos < text.Length && text[_pos] is ' ' or '\t' or '\n' or '\r')
            {
                _pos++;
            }
        }

        private void Value()
        {
            SkipWhitespace();
            if (_pos >= text.Length)
            {
                throw Unexpected(_pos);
            }

            switch (text[_pos])
            {
                case '{':
                    Object();
                    break;
                case '[':
                    Array();
                    break;
                case '"':
                    String();
                    break;
                case '-' or (>= '0' and <= '9'):
                    Number();
                    break;
                case 't':
                    Literal("true");
                    break;
                case 'f':
                    Literal("false");
                    break;
                case 'n':
                    Literal("null");
                    break;
                default:
                    throw Unexpected(_pos);
            }
        }

        private void Object()
        {
            _pos++;
            SkipWhitespace();
            if (_pos < text.Length && text[_pos] == '}')
            {
                _pos++;
                return;
            }

            var first = true;
            while (true)
            {
                if (_pos >= text.Length || text[_pos] != '"')
                {
                    throw At(first ? "Expected property name or '}'" : "Expected double-quoted property name", _pos);
                }

                first = false;
                String();
                SkipWhitespace();
                if (_pos >= text.Length || text[_pos] != ':')
                {
                    throw At("Expected ':' after property name", _pos);
                }

                _pos++;
                Value();
                SkipWhitespace();
                if (_pos < text.Length && text[_pos] == ',')
                {
                    _pos++;
                    SkipWhitespace();
                    continue;
                }

                if (_pos < text.Length && text[_pos] == '}')
                {
                    _pos++;
                    return;
                }

                throw At("Expected ',' or '}' after property value", _pos);
            }
        }

        private void Array()
        {
            _pos++;
            SkipWhitespace();
            if (_pos < text.Length && text[_pos] == ']')
            {
                _pos++;
                return;
            }

            while (true)
            {
                Value();
                SkipWhitespace();
                if (_pos < text.Length && text[_pos] == ',')
                {
                    _pos++;
                    continue;
                }

                if (_pos < text.Length && text[_pos] == ']')
                {
                    _pos++;
                    return;
                }

                throw At("Expected ',' or ']' after array element", _pos);
            }
        }

        private void Literal(string word)
        {
            for (var i = 1; i < word.Length; i++)
            {
                if (_pos + i >= text.Length || text[_pos + i] != word[i])
                {
                    throw Unexpected(_pos + i);
                }
            }

            _pos += word.Length;
        }

        private void String()
        {
            _pos++;
            while (true)
            {
                if (_pos >= text.Length)
                {
                    throw At("Unterminated string", text.Length);
                }

                var c = text[_pos];
                if (c == '"')
                {
                    _pos++;
                    return;
                }

                if (c < 0x20)
                {
                    throw At("Bad control character in string literal", _pos);
                }

                if (c != '\\')
                {
                    _pos++;
                    continue;
                }

                _pos++;
                if (_pos >= text.Length)
                {
                    throw At("Unterminated string", text.Length);
                }

                if (text[_pos] == 'u')
                {
                    for (var i = 1; i <= 4; i++)
                    {
                        if (_pos + i >= text.Length || !Uri.IsHexDigit(text[_pos + i]))
                        {
                            throw At("Bad Unicode escape", Math.Min(_pos + i, text.Length));
                        }
                    }

                    _pos += 5;
                    continue;
                }

                if ("\"\\/bfnrt".IndexOf(text[_pos], StringComparison.Ordinal) < 0)
                {
                    throw At("Bad escaped character", _pos);
                }

                _pos++;
            }
        }

        private void Number()
        {
            if (text[_pos] == '-')
            {
                _pos++;
                if (_pos >= text.Length || !IsDigit(text[_pos]))
                {
                    throw At("No number after minus sign", _pos);
                }
            }

            if (text[_pos] == '0')
            {
                _pos++;
                if (_pos < text.Length && IsDigit(text[_pos]))
                {
                    throw Unexpected(_pos);
                }
            }
            else
            {
                while (_pos < text.Length && IsDigit(text[_pos]))
                {
                    _pos++;
                }
            }

            if (_pos < text.Length && text[_pos] == '.')
            {
                _pos++;
                if (_pos >= text.Length || !IsDigit(text[_pos]))
                {
                    throw At("Unterminated fractional number", _pos);
                }

                while (_pos < text.Length && IsDigit(text[_pos]))
                {
                    _pos++;
                }
            }

            if (_pos < text.Length && text[_pos] is 'e' or 'E')
            {
                _pos++;
                if (_pos < text.Length && text[_pos] is '+' or '-')
                {
                    _pos++;
                }

                if (_pos >= text.Length || !IsDigit(text[_pos]))
                {
                    throw At("Exponent part is missing a number", _pos);
                }

                while (_pos < text.Length && IsDigit(text[_pos]))
                {
                    _pos++;
                }
            }
        }

        private static bool IsDigit(char c) => c is >= '0' and <= '9';

        /// <summary>A message that names a position and where it is on the line.</summary>
        private Stop At(string what, int position, bool inJson = true)
        {
            var line = 1;
            var lineStart = 0;
            for (var i = 0; i < position && i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    continue;
                }

                if (c is '\n' or '\r' or '\u2028' or '\u2029')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return new Stop($"{what}{(inJson ? " in JSON" : string.Empty)} at position {position} (line {line} column {position - lineStart + 1})");
        }

        /// <summary>The message for a character that cannot be here, where the engine has no more specific one.</summary>
        private Stop Unexpected(int position)
        {
            if (position >= text.Length)
            {
                return new Stop("Unexpected end of JSON input");
            }

            var c = text[position];
            if (c == '"')
            {
                return At("Unexpected string", position);
            }

            if (c == '-' || IsDigit(c))
            {
                return At("Unexpected number", position);
            }

            if (text is "NaN" or "Infinity" or "undefined" or "[object Object]")
            {
                return new Stop($"\"{text}\" is not valid JSON");
            }

            var length = text.Length;
            string shown;
            if (length < 2 * ContextCharacters + 1)
            {
                shown = $"\"{text}\"";
            }
            else if (position < ContextCharacters)
            {
                shown = $"\"{text[..(position + ContextCharacters)]}\"...";
            }
            else if (position < length - ContextCharacters)
            {
                shown = $"...\"{text.Substring(position - ContextCharacters, 2 * ContextCharacters)}\"...";
            }
            else
            {
                shown = $"...\"{text[(position - ContextCharacters)..]}\"";
            }

            return new Stop($"Unexpected token '{c}', {shown} is not valid JSON");
        }
    }
}
