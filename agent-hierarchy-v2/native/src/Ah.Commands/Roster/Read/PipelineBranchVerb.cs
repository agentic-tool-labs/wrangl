using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>ah roster pipeline-branch [show] | allow &lt;branch&gt; | revoke [&lt;branch&gt;]</c> (ah2-26, ah2-46): the branches the user has said a
/// pipeline run may work on directly (the repo's default or integration branch), per repository. The record is the home document
/// <see cref="Key"/> of the state database: <c>{v:1, repos:{"&lt;repo&gt;":["main", …]}}</c>, keyed by the repository's own directory,
/// which its work trees share. Before ah2-46 the answer was <c>git config ah.pipelineBranch</c>; a value found there is taken in once
/// and unset. Nothing here refuses a run: the autonomous-pipeline bootstrap reads <c>show</c> and asks the user when the branch is
/// not listed.
/// </summary>
internal static class PipelineBranchVerb
{
    internal const string Key = "home.pipeline-branches";

    private const string GitKey = "ah.pipelineBranch";

    private const string UsageText = "pipeline-branch [show] | allow <branch> | revoke [<branch>]   [--cwd <repo>]";

    [Verb("roster:pipeline-branch")]
    internal static int PipelineBranch(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (key is not ("cwd" or "team" or "json"))
            {
                throw new RosterFailure($"pipeline-branch: unrecognized flag --{key} (use {UsageText})");
            }
        }

        var words = c.Opts.Positionals;
        var action = words.Count == 0 ? "show" : words[0];
        var branch = words.Count > 1 ? words[1] : null;
        if (words.Count > 2 || action is not ("show" or "allow" or "revoke") || (action == "allow" && branch is null) || (action == "show" && branch is not null))
        {
            throw new RosterFailure($"pipeline-branch: use {UsageText}");
        }

        var docs = c.Services.HomeDocs ?? throw new RosterFailure("pipeline-branch: there is no state database to keep the answer in");
        var tree = c.Services.Config.FindGitRoot(c.Cwd) ?? throw new RosterFailure($"pipeline-branch: {c.Cwd} is not in a git repository");
        var repo = RepoOf(c, tree);
        if (!docs.TryRead(Key, out var held))
        {
            throw new RosterFailure("pipeline-branch: the state database cannot be read; nothing was changed");
        }

        var all = held is not null && JsonText.Parse(held) is { IsMap: true } doc && doc.Member("repos") is { IsMap: true } repos ? repos.AsObject() : new JsonObject();
        var branches = all.Get(repo) is { IsArray: true } list ? list.AsArray().Where(b => b.IsText).Select(b => b.AsString()).ToList() : [];
        var before = branches.Count;

        // an answer recorded by a build before ah2-46 lives in the repo's git config: taken in once, then unset there
        var legacy = ReleaseGate.Git(c, tree, "config", "--get-all", GitKey);
        var imported = legacy.Ok ? legacy.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        foreach (var name in imported)
        {
            Add(branches, name);
        }

        var changed = branches.Count != before;
        if (action == "allow")
        {
            changed |= Add(branches, branch!);
        }
        else if (action == "revoke")
        {
            changed |= branch is null ? Clear(branches) : branches.Remove(branch);
        }

        if (changed)
        {
            if (branches.Count == 0)
            {
                all.Remove(repo);
            }
            else
            {
                var array = new JsonArray();
                foreach (var name in branches)
                {
                    array.Add(name);
                }

                all[repo] = array;
            }

            try
            {
                docs.Write(Key, all.Count == 0 ? null : JsonText.Stringify(new JsonObject { ["v"] = 1, ["repos"] = all }, 0));
            }
            catch (InvalidOperationException e)
            {
                throw new RosterFailure($"pipeline-branch: {e.Message}");
            }
        }

        // unset only after the database holds the answer, so a failed write loses nothing
        if (imported.Length > 0)
        {
            _ = ReleaseGate.Git(c, tree, "config", "--unset-all", GitKey);
        }

        var shown = new JsonArray();
        foreach (var name in branches)
        {
            shown.Add(name);
        }

        c.Out(new JsonObject { ["repo"] = repo, ["branches"] = shown });
        return 0;
    }

    /// <summary>The repository's own directory, shared by its work trees: the parent of git's common dir, else the tree itself.</summary>
    private static string RepoOf(RosterContext c, string tree)
    {
        var common = ReleaseGate.Git(c, tree, "rev-parse", "--path-format=absolute", "--git-common-dir");
        var dir = common.Ok ? common.Stdout.Trim() : string.Empty;
        return dir.EndsWith("/.git", StringComparison.Ordinal) && dir.Length > "/.git".Length ? dir[..^"/.git".Length] : tree;
    }

    private static bool Add(List<string> branches, string name)
    {
        if (name.Length == 0 || branches.Contains(name, StringComparer.Ordinal))
        {
            return false;
        }

        branches.Add(name);
        return true;
    }

    private static bool Clear(List<string> branches)
    {
        var had = branches.Count > 0;
        branches.Clear();
        return had;
    }
}
