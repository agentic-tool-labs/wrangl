using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>Where git runs: a directory, or a directory plus the <c>--git-dir</c> and <c>--work-tree</c> a command named.</summary>
public sealed record GitScope(string Cwd, string? GitDir = null, string? WorkTree = null)
{
    public static implicit operator GitScope(string directory) => new(directory);
}

/// <summary>Origin's default branch: the remote-tracking ref and the branch's short name.</summary>
public sealed record OriginDefaultRef(string Ref, string Branch);

/// <summary>The repo's conventions as committed on origin's default branch.</summary>
/// <param name="OptedIn">Whether the file exists on the default branch; an invalid file still opts the repo in.</param>
/// <param name="Default">Origin's default branch, or null when none resolves.</param>
/// <param name="Status"><c>ok</c>, <c>absent</c> or <c>invalid: &lt;why&gt;</c>.</param>
/// <param name="Conventions">The parsed file when valid, else null.</param>
/// <param name="Path">The repo-relative path the file belongs at (where to commit it), whichever path it was read from.</param>
/// <param name="FromV1Path">Whether the file was read from ah v1's path because none is committed at <paramref name="Path"/>.</param>
public sealed record LoadedConventions(
    bool OptedIn,
    OriginDefaultRef? Default,
    string Status,
    JsonObject? Conventions,
    string Path = ".claude/" + AhPaths.Dir + "/" + AhPaths.ConventionsFile,
    bool FromV1Path = false);

/// <summary>
/// Team conventions: <c>.claude/ah2/ah-conventions.json</c> (<c>.claude/ah2/project/ah-conventions.json</c> when the repository is the
/// user's home, falling back to <c>.claude/ah2/ah-conventions.json</c>), read from the committed blob on origin's default branch,
/// never from the working tree. Only when no blob exists at those paths is ah v1's <c>.claude/ah-conventions.json</c> read,
/// read-only and never merged: a repository that opted in under v1 keeps its push rules. A file that exists but is invalid is the
/// answer; it never falls back. The one parser for the file; the push guard and the intake command both read conventions through
/// here.
/// </summary>
public interface IConventions
{
    /// <summary>The repo-relative path of the conventions file.</summary>
    string ConventionsPath { get; }

    /// <summary>stdout of git run in <paramref name="at"/>. Throws when git exits non-zero, is missing or writes more than 1 MiB.</summary>
    string Git(GitScope at, IReadOnlyList<string> args);

    /// <summary>Like <see cref="Git"/>, but null instead of throwing.</summary>
    string? TryGit(GitScope at, IReadOnlyList<string> args);

    /// <summary>Whether <paramref name="reference"/> names a commit in the repo.</summary>
    bool RefExists(GitScope repo, string reference);

    /// <summary>Origin's default branch by <c>refs/remotes/origin/HEAD</c>, else <c>main</c>, else <c>master</c>; null when none resolves. Never fetches.</summary>
    OriginDefaultRef? OriginDefault(GitScope repo);

    /// <summary>Why <paramref name="value"/> breaks schema version 1, or null when it is valid. Unknown keys at any level are violations.</summary>
    string? ValidateConventions(JsonValue value);

    /// <summary>The conventions as committed on origin's default branch.</summary>
    LoadedConventions LoadConventions(GitScope repo);

    /// <summary>A conventions glob, compiled for matching repo-relative POSIX paths or branch names.</summary>
    IGlobPattern CompileGlob(string pattern);

    /// <summary>Whether <paramref name="text"/> matches the glob.</summary>
    bool GlobMatch(string pattern, string text);
}

internal sealed partial class Conventions(IProcessRunner runner, IFileReader? files = null, Func<string?>? userHome = null) : IConventions
{
    private const string ConventionsFile = ".claude/" + AhPaths.Dir + "/" + AhPaths.ConventionsFile;

    private const string HomeConventionsFile = ".claude/" + AhPaths.Dir + "/" + AhPaths.ProjectUnderHome + "/" + AhPaths.ConventionsFile;

    private const string V1ConventionsFile = ".claude/ah-conventions.json";

    private const string RemotePrefix = "refs/remotes/origin/";

    private static readonly string[] BranchRefs = [RemotePrefix + "main", RemotePrefix + "master"];

    public string ConventionsPath => ConventionsFile;

    public string Git(GitScope at, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(at);
        var argv = new List<string> { "git", "-C", at.Cwd };
        if (!string.IsNullOrEmpty(at.GitDir))
        {
            argv.Add("--git-dir=" + at.GitDir);
        }

        if (!string.IsNullOrEmpty(at.WorkTree))
        {
            argv.Add("--work-tree=" + at.WorkTree);
        }

        argv.AddRange(args);
        var result = runner.Run(new ProcessRequest(argv));
        if (result.ExitCode != 0 || result.Truncated || result.TimedOut)
        {
            throw new InvalidOperationException("Command failed: " + string.Join(' ', argv));
        }

        return Encoding.UTF8.GetString(result.Stdout.Span);
    }

    public string? TryGit(GitScope at, IReadOnlyList<string> args)
    {
        try
        {
            return Git(at, args);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool RefExists(GitScope repo, string reference) =>
        TryGit(repo, ["rev-parse", "--verify", "-q", reference + "^{commit}"]) is not null;

    public OriginDefaultRef? OriginDefault(GitScope repo)
    {
        var target = TryGit(repo, ["symbolic-ref", "-q", RemotePrefix + "HEAD"]);
        var candidates = new List<string?> { target is null ? null : JsText.Trim(target), BranchRefs[0], BranchRefs[1] };
        foreach (var reference in candidates)
        {
            if (!string.IsNullOrEmpty(reference) && reference.StartsWith(RemotePrefix, StringComparison.Ordinal) && RefExists(repo, reference))
            {
                return new OriginDefaultRef(reference, reference[RemotePrefix.Length..]);
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- schema

    private abstract record Rule;

    private sealed record Leaf(Func<JsonValue, string?> Check) : Rule;

    private sealed record Branch(Dictionary<string, Rule> Children) : Rule;

    private static bool NonEmptyString(JsonValue v) => v.IsText && v.AsString().Length > 0;

    private static bool StringArray(JsonValue v) => v.IsArray && v.AsArray().All(NonEmptyString);

    private static Leaf Check(Func<JsonValue, bool> ok, string message) => new(v => ok(v) ? null : message);

    private static readonly Dictionary<string, Rule> Schema = new(StringComparer.Ordinal)
    {
        ["version"] = Check(v => v.IsNumber && v.AsNumber() == 1, "`version` must be the integer 1"),
        ["issues"] = new Branch(new(StringComparer.Ordinal)
        {
            ["trigger_label"] = Check(NonEmptyString, "`issues.trigger_label` must be a non-empty string"),
            ["trusted_actors"] = Check(StringArray, "`issues.trusted_actors` must be an array of logins"),
        }),
        ["pr"] = new Branch(new(StringComparer.Ordinal)
        {
            ["draft"] = Check(v => v.IsBool, "`pr.draft` must be a boolean"),
            ["issue_link"] = Check(v => v.IsText && v.AsString() is "refs" or "closes", "`pr.issue_link` must be \"refs\" or \"closes\""),
            ["reviewers"] = Check(StringArray, "`pr.reviewers` must be an array of logins or org/team names"),
            ["merge_method"] = Check(v => v.IsText && v.AsString() is "merge" or "squash" or "rebase", "`pr.merge_method` must be \"merge\", \"squash\" or \"rebase\""),
        }),
        ["ci_secrets"] = Check(v => v.IsText && v.AsString() is "none-on-branches" or "skip-ci", "`ci_secrets` must be \"none-on-branches\" or \"skip-ci\""),
        ["protected_paths"] = Check(StringArray, "`protected_paths` must be an array of globs"),
        ["protected_branches"] = Check(StringArray, "`protected_branches` must be an array of globs"),
        ["on_item_done"] = Check(NonEmptyString, "`on_item_done` must be a skill name"),
    };

    private static string? Violation(JsonValue value, Dictionary<string, Rule> schema, string prefix)
    {
        if (!value.IsMap)
        {
            return prefix.Length > 0 ? $"`{prefix[..^1]}` must be an object" : "the file must hold a JSON object";
        }

        var obj = value.AsObject();
        foreach (var key in obj.Keys)
        {
            if (!schema.TryGetValue(key, out var rule))
            {
                return $"unknown key `{prefix}{key}`";
            }

            var why = rule switch
            {
                Leaf leaf => leaf.Check(obj[key]),
                Branch branch => Violation(obj[key], branch.Children, $"{prefix}{key}."),
                _ => null,
            };
            if (why is not null)
            {
                return why;
            }
        }

        return null;
    }

    public string? ValidateConventions(JsonValue value)
    {
        if (value.IsMap && value.AsObject().Get("version") is null)
        {
            return "`version` is required";
        }

        return Violation(value, Schema, string.Empty);
    }

    /// <summary>
    /// Whether the repository is the user's home, where the project directory is <c>~/.claude/ah2/project</c>. A working directory
    /// outside the home answers without running git.
    /// </summary>
    private bool RepoIsHome(GitScope repo)
    {
        if (files is null || userHome is null)
        {
            return false;
        }

        try
        {
            if (userHome() is not { Length: > 0 } home)
            {
                return false;
            }

            var homeReal = files.RealPath(home).TrimEnd('/');
            var dirReal = files.RealPath(string.IsNullOrEmpty(repo.WorkTree) ? repo.Cwd : repo.WorkTree);
            if (dirReal != homeReal && !dirReal.StartsWith(homeReal + "/", StringComparison.Ordinal))
            {
                return false;
            }

            var top = TryGit(repo, ["rev-parse", "--show-toplevel"]);
            return top is not null && files.RealPath(JsText.Trim(top)) == files.RealPath(home);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    public LoadedConventions LoadConventions(GitScope repo)
    {
        var def = OriginDefault(repo);
        var own = RepoIsHome(repo) ? HomeConventionsFile : ConventionsFile;
        var absent = new LoadedConventions(false, def, "absent", null, own);
        if (def is null)
        {
            return absent;
        }

        var candidates = own == HomeConventionsFile ? new[] { own, ConventionsFile, V1ConventionsFile } : [own, V1ConventionsFile];
        var path = candidates.FirstOrDefault(c => TryGit(repo, ["cat-file", "-e", $"{def.Ref}:{c}"]) is not null);
        if (path is null)
        {
            return absent;
        }

        var fromV1 = path == V1ConventionsFile;

        JsonValue parsed;
        try
        {
            parsed = JsonText.Parse(Git(repo, ["cat-file", "blob", $"{def.Ref}:{path}"]));
        }
        catch (Exception e)
        {
            return new LoadedConventions(true, def, "invalid: " + e.Message, null, own, fromV1);
        }

        return ValidateConventions(parsed) is { } why
            ? new LoadedConventions(true, def, "invalid: " + why, null, own, fromV1)
            : new LoadedConventions(true, def, "ok", parsed.AsObject(), own, fromV1);
    }

    // ---------------------------------------------------------------- globs

    public IGlobPattern CompileGlob(string pattern) => GlobPattern.Parse(pattern);

    public bool GlobMatch(string pattern, string text) => GlobPattern.Parse(pattern).IsMatch(text);
}
