using System.Text;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Roster;

/// <summary>
/// What Codex's own configuration says about trusting a directory, read before a launch and never written. Only
/// <c>[projects."&lt;abs path&gt;"]</c> tables and their <c>trust_level</c> are read; anything else about <c>projects</c>, or a
/// header or value this reader cannot decode, makes the answer unknown.
/// </summary>
internal sealed partial class CodexTrust(IFileReader files, IAhEnvironment environment, Func<string, string?> checkoutRoot) : ICodexTrust
{
    private const string Lt = "\\n\\r\\u2028\\u2029";
    private const string Sp = JsText.SpaceClass;

    [GeneratedRegex("^\\[projects\\.\"((?:[^\"\\\\]|\\\\[^" + Lt + "])*)\"\\][" + Sp + "]*(?:#[^" + Lt + "]*)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectHeader();

    [GeneratedRegex("^\\[\\[?[" + Sp + "]*[\"']?projects(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectsLikeHeader();

    [GeneratedRegex("^[\"']?projects[\"']?[" + Sp + "]*[.=]", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectsKey();

    [GeneratedRegex("^trust_level[" + Sp + "]*=[" + Sp + "]*([^" + Lt + "]*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TrustLevel();

    [GeneratedRegex("^\"((?:[^\"\\\\]|\\\\[^" + Lt + "])*)\"([" + Sp + "]*(?:#[^" + Lt + "]*)?)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedValue();

    /// <summary>The multi-line string delimiter a line opens and leaves open (an odd number of them), or null.</summary>
    private static string? OpensMultiline(string line)
    {
        string? open = null;
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            if (open is not null)
            {
                if (string.CompareOrdinal(line, i, open, 0, 3) == 0)
                {
                    open = null;
                    i += 2;
                }

                continue;
            }

            var c = line[i];
            if (quote is not null)
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (c == '#')
            {
                break;
            }

            if (string.CompareOrdinal(line, i, "\"\"\"", 0, 3) == 0 || string.CompareOrdinal(line, i, "'''", 0, 3) == 0)
            {
                open = line.Substring(i, 3);
                i += 2;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
        }

        return open;
    }

    /// <summary>A TOML basic string's body with its escapes decoded, or null for an escape this reader does not know.</summary>
    internal static string? TomlUnescape(string body)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\')
            {
                sb.Append(body[i]);
                continue;
            }

            i++;
            if (i >= body.Length)
            {
                return null;
            }

            var c = body[i];
            var simple = c switch
            {
                'b' => '\b',
                't' => '\t',
                'n' => '\n',
                'f' => '\f',
                'r' => '\r',
                '"' => '"',
                '\\' => '\\',
                _ => '\0',
            };
            if (simple != '\0')
            {
                sb.Append(simple);
                continue;
            }

            var len = c == 'u' ? 4 : c == 'U' ? 8 : 0;
            if (len == 0 || i + 1 + len > body.Length)
            {
                return null;
            }

            var hex = body.Substring(i + 1, len);
            if (!hex.All(Uri.IsHexDigit))
            {
                return null;
            }

            var code = long.Parse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture);
            if (code > 0x10ffff)
            {
                return null;
            }

            if (code <= 0xffff)
            {
                sb.Append((char)code);
            }
            else
            {
                sb.Append(char.ConvertFromUtf32((int)code));
            }

            i += len;
        }

        return sb.ToString();
    }

    /// <summary>The directories the config at <paramref name="configPath"/> records as trusted, or null when that is unknown.</summary>
    internal HashSet<string>? TrustedProjects(string configPath)
    {
        string text;
        try
        {
            text = files.ReadText(configPath);
        }
        catch (Exception)
        {
            return null;
        }

        var trusted = new HashSet<string>(StringComparer.Ordinal);
        string? current = null;
        string? multiline = null;
        foreach (var raw in text.TrimStart('﻿').Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l))
        {
            if (multiline is not null)
            {
                if (raw.Contains(multiline, StringComparison.Ordinal))
                {
                    multiline = null;
                }

                continue;
            }

            var line = JsText.Trim(raw);
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            multiline = OpensMultiline(line);

            if (line.StartsWith('['))
            {
                var header = ProjectHeader().Match(line);
                if (header.Success)
                {
                    current = TomlUnescape(header.Groups[1].Value);
                    if (current is null)
                    {
                        return null;
                    }

                    continue;
                }

                if (ProjectsLikeHeader().IsMatch(line))
                {
                    return null;
                }

                current = null;
                continue;
            }

            if (current is null)
            {
                if (ProjectsKey().IsMatch(line))
                {
                    return null;
                }

                continue;
            }

            var kv = TrustLevel().Match(line);
            if (!kv.Success)
            {
                continue;
            }

            var value = QuotedValue().Match(kv.Groups[1].Value);
            var decoded = value.Success ? TomlUnescape(value.Groups[1].Value) : null;
            if (decoded is null)
            {
                return null;
            }

            if (decoded == "trusted")
            {
                trusted.Add(current);
            }
        }

        return multiline is null ? trusted : null;
    }

    private string RealOr(string path)
    {
        try
        {
            return files.RealPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>
    /// What it takes to make the config trust the trust root of <paramref name="cwd"/>, as the config text would be after the edit:
    /// <c>{state, configPath, codexHome, trustRoot, key, edit, newText}</c>. The state is <c>absent</c> (no config file),
    /// <c>no-table</c>, <c>table-without-trust_level</c>, <c>untrusted</c>, <c>trusted</c> or <c>unreadable</c> (with <c>why</c>).
    /// <c>newText</c> is null when nothing is to be written. Every other byte of the file is kept.
    /// </summary>
    public JsonObject PlanTrust(string cwd)
    {
        var codexHome = environment.GetVariable("CODEX_HOME") is { Length: > 0 } h ? h : NodePath.Join(environment.HomeDirectory, ".codex");
        var configPath = NodePath.Join(codexHome, "config.toml");
        var root = TrustRoot(cwd);
        var key = RealOr(root);
        var table = $"[projects.{KindHarness.TomlString(key)}]\ntrust_level = \"trusted\"\n";

        string? sourceHash = null;

        var targetHeader = table[..table.IndexOf('\n', StringComparison.Ordinal)];

        JsonObject Result(string state, string edit, string? newText, string? why = null)
        {
            // Two tables for one root are a config Codex rejects; a header this reader skipped (inside a string it took
            // for a multi-line one) must not end up beside the one it adds.
            if (newText is not null && newText.Split('\n').Count(l => JsText.Trim(l) == targetHeader) > 1)
            {
                state = "unreadable";
                edit = "nothing";
                newText = null;
                why = "the edit would leave two tables for this root; fix the file by hand";
            }

            var o = new JsonObject
            {
                ["state"] = state,
                ["configPath"] = configPath,
                ["codexHome"] = codexHome,
                ["trustRoot"] = root,
                ["key"] = key,
                ["edit"] = edit,
                ["newText"] = newText is null ? JsonValue.Null : newText,
                ["sourceSha256"] = sourceHash is null ? JsonValue.Null : sourceHash,
            };
            if (why is not null)
            {
                o["why"] = why;
            }

            return o;
        }

        if (!files.Exists(configPath))
        {
            return Result("absent", $"create {configPath} (mode 0600) with the table for {key}", table);
        }

        string text;
        try
        {
            var bytes = files.ReadBytes(configPath);
            sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result("unreadable", "nothing", null, $"{configPath} cannot be read: {e.Message}");
        }
        catch (DecoderFallbackException)
        {
            return Result("unreadable", "nothing", null, $"{configPath} is not valid UTF-8, so it is left exactly as it is");
        }

        const string Unreadable = "this reader cannot decode the config safely; fix the file by hand";
        var bom = text.StartsWith('﻿') ? "﻿" : string.Empty;
        text = text[bom.Length..];
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').ToList();
        var headerAt = -1;
        var trustAt = -1;
        string? level = null;
        string trailing = string.Empty;
        var inTarget = false;
        string? current = null;
        string? multiline = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var rawLine = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];
            if (multiline is not null)
            {
                if (rawLine.Contains(multiline, StringComparison.Ordinal))
                {
                    multiline = null;
                }

                continue;
            }

            var line = JsText.Trim(rawLine);
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            multiline = OpensMultiline(line);

            if (line.StartsWith('['))
            {
                var header = ProjectHeader().Match(line);
                if (header.Success)
                {
                    current = TomlUnescape(header.Groups[1].Value);
                    if (current is null)
                    {
                        return Result("unreadable", "nothing", null, Unreadable);
                    }

                    inTarget = current == key || current == root || RealOr(current) == key;
                    if (inTarget)
                    {
                        if (headerAt >= 0)
                        {
                            return Result("unreadable", "nothing", null, "two tables name this root");
                        }

                        headerAt = i;
                    }

                    continue;
                }

                if (ProjectsLikeHeader().IsMatch(line))
                {
                    return Result("unreadable", "nothing", null, Unreadable);
                }

                current = null;
                inTarget = false;
                continue;
            }

            if (current is null)
            {
                if (ProjectsKey().IsMatch(line))
                {
                    return Result("unreadable", "nothing", null, Unreadable);
                }

                continue;
            }

            var kv = TrustLevel().Match(line);
            if (!kv.Success || !inTarget)
            {
                continue;
            }

            var value = QuotedValue().Match(kv.Groups[1].Value);
            level = value.Success ? TomlUnescape(value.Groups[1].Value) : null;
            if (level is null)
            {
                return Result("unreadable", "nothing", null, Unreadable);
            }

            trailing = value.Groups[2].Value;
            trustAt = i;
        }

        if (multiline is not null)
        {
            return Result("unreadable", "nothing", null, "a multi-line string is left open at the end of the file; fix the file by hand");
        }

        if (headerAt < 0)
        {
            var head = text.Length == 0 || text.EndsWith('\n') ? text : text + eol;
            return Result("no-table", $"append the table for {key} to {configPath}", bom + head + eol + table.Replace("\n", eol, StringComparison.Ordinal));
        }

        if (trustAt < 0)
        {
            lines.Insert(headerAt + 1, "trust_level = \"trusted\"" + (lines[headerAt].EndsWith('\r') ? "\r" : string.Empty));
            return Result("table-without-trust_level", $"add trust_level = \"trusted\" under the table for {key}", bom + string.Join('\n', lines));
        }

        if (level == "trusted")
        {
            return Result("trusted", "nothing: already trusted", null);
        }

        var original = lines[trustAt];
        var indent = original[..(original.Length - original.TrimStart().Length)];
        lines[trustAt] = indent + "trust_level = \"trusted\"" + trailing + (original.EndsWith('\r') ? "\r" : string.Empty);
        return Result("untrusted", $"replace trust_level = \"{level}\" with \"trusted\" under the table for {key}; this replaces your earlier distrust", bom + string.Join('\n', lines));
    }

    /// <summary>Where Codex saves a trust answer given in <paramref name="cwd"/>: the checkout's root, else <paramref name="cwd"/>.</summary>
    public string TrustRoot(string cwd) => checkoutRoot(cwd) is { Length: > 0 } root ? root : NodePath.Resolve("/", cwd);

    /// <summary>
    /// Whether Codex has been told to trust <paramref name="cwd"/>: <c>{verdict, configPath, codexHome, trustRoot}</c>, verdict
    /// <c>trusted</c>, <c>untrusted</c> or <c>unknown</c>. Only a trusted entry for the trust root itself counts, compared as given and
    /// resolved on both sides: Codex applies a trust answer to the repository root, not to the directories around it.
    /// </summary>
    public JsonObject Check(string cwd)
    {
        var codexHome = environment.GetVariable("CODEX_HOME") is { Length: > 0 } h ? h : NodePath.Join(environment.HomeDirectory, ".codex");
        var configPath = NodePath.Join(codexHome, "config.toml");
        var trustRoot = TrustRoot(cwd);
        var trusted = TrustedProjects(configPath);
        string verdict;
        if (trusted is null)
        {
            verdict = "unknown";
        }
        else
        {
            var entries = new HashSet<string>(trusted.SelectMany(p => new[] { p, RealOr(p) }), StringComparer.Ordinal);
            verdict = entries.Contains(trustRoot) || entries.Contains(RealOr(trustRoot)) ? "trusted" : "untrusted";
        }

        return new JsonObject
        {
            ["verdict"] = verdict,
            ["configPath"] = configPath,
            ["codexHome"] = codexHome,
            ["trustRoot"] = trustRoot,
        };
    }
}
