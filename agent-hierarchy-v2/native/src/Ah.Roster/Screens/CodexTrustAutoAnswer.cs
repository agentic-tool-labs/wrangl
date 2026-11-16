using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Roster;

/// <summary>
/// The one decision of whether a Codex trust screen may be answered for the user: the screen must be exactly Codex's trust screen
/// (whitespace aside), its effective root must be the trust root of the launch directory, and a recorded pre-approval must cover that
/// root. Anything else is not answered. Pre-approvals are read from the <see cref="RosterModule.CodexTrustPreapprovalKey"/> row; no
/// <paramref name="docs"/> reads as none.
/// </summary>
internal sealed class CodexTrustAutoAnswer(IFileReader files, IAhEnvironment environment, ICodexTrust trust, IClock clock, IHomeDocs? docs) : ICodexTrustAutoAnswer
{
    private const string Heading = "Folder access ";

    private const string SubdirectoryNote = " Note: You’re in a subdirectory of a Git project. Trusting will apply to the repository root: ";

    private const string Body =
        "Trust this folder? Codex can read, edit, and run files here, subject to your permission settings. Folder settings can run code automatically, even without a model request. Continue only if you trust these files. Your trust decision will be saved. › 1. Trust and continue 2. Quit enter continue · esc quit";

    public string StorePath => RosterModule.CodexTrustPreapprovalKey;

    public string LogPath => RosterModule.CodexTrustAutoAnswerLogPath(environment);

    public JsonObject? Match(string screen)
    {
        var text = string.Join(' ', screen.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var suffix = " " + Body;
        if (!text.StartsWith(Heading, StringComparison.Ordinal) || !text.EndsWith(suffix, StringComparison.Ordinal) || text.Length <= Heading.Length + suffix.Length)
        {
            return null;
        }

        var middle = text[Heading.Length..^suffix.Length];
        var split = middle.Split(SubdirectoryNote, StringSplitOptions.None);
        var path = split[0];
        var root = split.Length == 2 ? split[1] : path;
        if (split.Length > 2 || !IsAbsolute(path) || !IsAbsolute(root) || path.Contains("Note:", StringComparison.Ordinal) || root.Contains("Note:", StringComparison.Ordinal))
        {
            return null;
        }

        return new JsonObject { ["path"] = path, ["root"] = root };
    }

    private static bool IsAbsolute(string path) => path.Length > 1 && path[0] == '/';

    public List<JsonObject> ReadScopes(out string? problem)
    {
        problem = null;
        string? text = null;
        if (docs is not null && !docs.TryRead(StorePath, out text))
        {
            problem = "the state database cannot be read";
            return [];
        }

        if (text is null)
        {
            return [];
        }

        try
        {
            var root = JsonText.Parse(Encoding.UTF8.GetBytes(text));
            if (!root.IsMap || root.Member("scopes") is not { IsArray: true } scopes || scopes.AsArray().Any(s => !s.IsMap))
            {
                problem = "not a pre-approval store: it has no scopes array of objects";
                return [];
            }

            return scopes.AsArray().Select(s => s.AsObject()).ToList();
        }
        catch (FormatException e)
        {
            problem = e.Message;
            return [];
        }
    }

    /// <summary>A <c>root</c> scope covers by equality; a <c>parent</c> scope covers the folder itself and every root under it, matched at a <c>/</c> boundary.</summary>
    public JsonObject? Covering(IEnumerable<JsonObject> scopes, string root)
    {
        foreach (var scope in scopes)
        {
            var path = scope.Get("path") is { IsText: true } p ? p.AsString() : null;
            var kind = scope.Get("kind") is { IsText: true } k ? k.AsString() : null;
            if (path is null || path.Length < 2 || path[0] != '/')
            {
                continue;
            }

            var prefix = path.EndsWith('/') ? path : path + "/";
            if ((kind == "root" && path == root) || (kind == "parent" && (path == root || root.StartsWith(prefix, StringComparison.Ordinal))))
            {
                return scope;
            }
        }

        return null;
    }

    /// <summary><c>answer</c> is true only when the screen matches exactly, names the launch directory's trust root, and a scope covers it.</summary>
    public JsonObject Decide(string screen, string launchCwd)
    {
        if (Match(screen) is not { } matched)
        {
            return new JsonObject { ["answer"] = false, ["reason"] = "screen-not-exact" };
        }

        var named = Real(matched.Get("root")!.Value.AsString());
        var expected = Real(trust.TrustRoot(launchCwd));
        if (named != expected)
        {
            return new JsonObject { ["answer"] = false, ["reason"] = "root-mismatch", ["root"] = named };
        }

        var scopes = ReadScopes(out _);
        if (Covering(scopes, expected) is not { } scope)
        {
            return new JsonObject { ["answer"] = false, ["reason"] = "no-preapproval", ["root"] = expected };
        }

        return new JsonObject { ["answer"] = true, ["reason"] = "preapproved", ["root"] = expected, ["scope"] = scope.Get("path") ?? JsonValue.Null };
    }

    public JsonObject Answer(JsonObject decision, string member, JsonValue team, JsonValue screenHash, string launchCwd, Func<bool> sendEnter, Func<bool> composerUp)
    {
        var root = decision.Get("root") is { IsText: true } r ? r.AsString() : string.Empty;
        var scope = decision.Get("scope") ?? JsonValue.Null;
        var result = "answered";
        if (!sendEnter())
        {
            result = "send-failed";
        }
        else if (!composerUp() || trust.Check(launchCwd).Get("verdict") is not { IsText: true } verdict || verdict.AsString() != "trusted")
        {
            result = "verify-failed";
        }

        AppendLog(new JsonObject
        {
            ["at"] = clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["team"] = team,
            ["member"] = member,
            ["root"] = root,
            ["scope"] = scope,
            ["screen_hash"] = screenHash,
            ["result"] = result,
        });
        return new JsonObject
        {
            ["root"] = root,
            ["scope"] = scope,
            ["result"] = result,
            ["note"] = $"Codex trust auto-answered for {root} (pre-approved): the setup step missed this root.",
        };
    }

    private void AppendLog(JsonObject row)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = mode;
            }

            using var stream = new FileStream(LogPath, options);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(LogPath, mode);
            }

            stream.Write(Encoding.UTF8.GetBytes(JsonText.Stringify(row, 0) + "\n"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log is a record, not a gate: a write that fails must not undo an answer already given.
        }
    }

    private string Real(string path)
    {
        try
        {
            return files.RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }
}
