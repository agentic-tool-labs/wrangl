using Ah.Config;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// PostToolUse (Bash): records, in the ledger of each team the session owns, a <c>git merge</c> or <c>gh pr merge</c> (a <c>merge</c>
/// event) and a <c>git push</c> of a tag or a branch (a <c>ship</c> event). The command is read with the push guard's parser. It only
/// reads git, never writes it, and never fails the tool.
/// <para>
/// When the line is one command, or commands joined only by <c>&amp;&amp;</c>, the call's exit status is each command's status and the
/// event is <c>confirmed</c>. Otherwise (<c>;</c>, a pipe, <c>||</c>, a group) the status says nothing about one command: a branch push
/// or a merge is confirmed by reading git afterwards and recorded only when that read agrees, and a tag push, a push to a URL or a
/// <c>gh pr merge</c> cannot be read back, so it is recorded unconfirmed when the call exited 0.
/// </para>
/// </summary>
internal static class PostToolUseLedgerRecord
{
    public static void Record(GateContext c)
    {
        var command = c.Command() ?? string.Empty;
        if (c.ToolName() != "Bash" || !(command.Contains("git", StringComparison.Ordinal) || command.Contains("gh", StringComparison.Ordinal))
            || c.Services.Config.IsSubagent(c.Input) || c.Services.Advisory is not { } advisory)
        {
            return;
        }

        try
        {
            var cwd = c.Cwd();
            var boxes = advisory.Callers.SessionDeciderBoxes(cwd, c.Input.NonEmpty("session_id"));
            if (boxes.Count == 0)
            {
                return;
            }

            var parsed = PushShell.Parse(command).Events;
            var invs = new List<GitInvocation>();
            var ghs = new List<GhInvocation>();
            var walked = new PushState(cwd);
            PushShell.Collect(parsed, walked, cwd, invs, ghs);
            if (walked.Budget.Spent)
            {
                return;
            }

            var statusBearing = PushShell.StatusBearing(parsed);
            var callOk = Succeeded(c);
            var s = c.Services;
            var pool = ProgressPool.Of(s, cwd);
            var teams = boxes.Select(b => b.Team).Distinct(StringComparer.Ordinal).ToList();
            foreach (var inv in invs)
            {
                var scope = new GitScope(inv.Cwd, inv.GitDir, inv.WorkTree);
                if (inv.Sub == "merge" && Positional(inv.Args).FirstOrDefault() is { } merged)
                {
                    if (Confirmation(statusBearing, callOk, () => MergeLanded(c, scope, merged)) is { } confirmed)
                    {
                        Append(c, pool, teams, MergeEvent(c, scope, merged, "git merge", confirmed));
                    }
                }
                else if (inv.Sub == "push")
                {
                    foreach (var target in PushTargets(c, scope, inv.Args))
                    {
                        var check = target.Remote is null ? null : (Func<bool>)(() => PushLanded(c, scope, target));
                        if (Confirmation(statusBearing, callOk, check) is { } confirmed)
                        {
                            Append(c, pool, teams, ShipEvent(c, target, confirmed));
                        }
                    }
                }
            }

            foreach (var gh in ghs)
            {
                var words = Positional(gh.Args);
                if (words.Count >= 2 && words[0] == "pr" && words[1] == "merge"
                    && Confirmation(statusBearing, callOk, null) is { } confirmed)
                {
                    Append(c, pool, teams, MergeEvent(c, new GitScope(gh.Cwd), words.Count > 2 ? words[2] : "the current branch", "gh pr merge", confirmed));
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }

    /// <summary>
    /// Whether to record, and with what confirmation: null records nothing. A status-bearing line trusts the exit status and runs no
    /// check. Otherwise a check, when there is one, decides whatever the exit status was; with no check the event is recorded
    /// unconfirmed on exit 0.
    /// </summary>
    private static bool? Confirmation(bool statusBearing, bool callOk, Func<bool>? check)
    {
        if (statusBearing)
        {
            return callOk ? true : null;
        }

        if (check is not null)
        {
            return check() ? true : null;
        }

        return callOk ? false : null;
    }

    /// <summary>
    /// Whether the call succeeded. A failed Bash call arrives as a PostToolUseFailure, which this unit is not registered for;
    /// a response that still says it was interrupted or exited non-zero is refused as well.
    /// </summary>
    internal static bool Succeeded(GateContext c)
    {
        if (c.Input.Text("hook_event_name") == "PostToolUseFailure")
        {
            return false;
        }

        var response = c.Input.ObjectOrEmpty("tool_response");
        if (response.Get("interrupted") is { IsTruthy: true })
        {
            return false;
        }

        foreach (var key in new[] { "exit_code", "exitCode", "returncode" })
        {
            if (response.Get(key) is { IsNumber: true } code && code.AsNumber() != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static readonly string[] ValueOptions =
    [
        "-m", "--message", "-F", "--file", "-s", "--strategy", "-X", "--strategy-option", "--into-name",
        "-o", "--push-option", "--receive-pack", "--exec", "--repo",
        "-R", "-b", "--body", "-t", "--subject", "--body-file", "-A", "--author-email", "--match-head-commit",
    ];

    /// <summary>
    /// The non-flag words after the subcommand, in order. The word after an option that takes a value
    /// (<c>-m "msg"</c>, <c>-s ours</c>) is that value, not a ref; <c>--opt=value</c> is one word and already skipped.
    /// </summary>
    private static List<string> Positional(List<string> args)
    {
        var words = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (Array.IndexOf(ValueOptions, args[i]) >= 0)
                {
                    i++;
                }

                continue;
            }

            words.Add(args[i]);
        }

        return words;
    }

    private static string? Git(GateContext c, GitScope scope, params string[] args) =>
        c.Services.Conventions.TryGit(scope, args)?.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A merge took effect when no merge is left in progress and the merged ref is now part of HEAD.</summary>
    private static bool MergeLanded(GateContext c, GitScope scope, string merged) =>
        Git(c, scope, "rev-parse", "-q", "--verify", "MERGE_HEAD") is null
        && c.Services.Conventions.TryGit(scope, ["merge-base", "--is-ancestor", merged, "HEAD"]) is not null;

    private static LedgerEvent MergeEvent(GateContext c, GitScope scope, string merged, string verb, bool confirmed)
    {
        var into = Git(c, scope, "rev-parse", "--abbrev-ref", "HEAD");
        var sha = Git(c, scope, "rev-parse", "HEAD");
        return new LedgerEvent(
            "merge",
            Sha: sha,
            Ref: merged,
            Into: into,
            Text: into is not null ? $"merged {merged} into {into}" : $"merged {merged}",
            SourceType: "tool",
            SourceVerb: verb,
            ToolUseId: c.Input.NonEmpty("tool_use_id"),
            Confirmed: confirmed);
    }

    /// <summary>
    /// One ref a push sent: <paramref name="Name"/> is the remote-side ref, <paramref name="Source"/> the local rev that gives the sha,
    /// <paramref name="Remote"/> the remote's name when the push went to a configured remote (null for a URL or a tag), and
    /// <paramref name="Sha"/> the commit.
    /// </summary>
    private sealed record PushTarget(string Name, bool IsTag, string Source, string? Remote, string? Sha);

    private static LedgerEvent ShipEvent(GateContext c, PushTarget target, bool confirmed) =>
        new(
            "ship",
            Sha: target.Sha,
            Ref: target.Name,
            Text: target.IsTag ? $"pushed tag {target.Name}" : $"pushed {target.Name}",
            SourceType: "tool",
            SourceVerb: "git push",
            ToolUseId: c.Input.NonEmpty("tool_use_id"),
            Tag: target.IsTag,
            Confirmed: confirmed);

    /// <summary>A branch push reached its remote when the remote-tracking ref now names the commit that was sent.</summary>
    private static bool PushLanded(GateContext c, GitScope scope, PushTarget target) =>
        target.Sha is not null
        && Git(c, scope, "rev-parse", "--verify", "-q", "refs/remotes/" + target.Remote + "/" + target.Name + "^{commit}") == target.Sha;

    /// <summary>
    /// The refs a <c>git push</c> sends, each with its remote-side name and the commit it carries. Nothing for a deletion, a push of
    /// every branch (<c>--all</c>, <c>--mirror</c>) or of every tag (<c>--tags</c>), a detached <c>HEAD</c>, or a bare <c>git push</c>
    /// with no upstream: no single ref says what was shipped.
    /// </summary>
    private static List<PushTarget> PushTargets(GateContext c, GitScope scope, List<string> args)
    {
        var targets = new List<PushTarget>();
        if (args.Any(a => a is "--delete" or "-d" or "--all" or "--mirror" or "--tags"))
        {
            return targets;
        }

        var words = Positional(args);
        var current = Git(c, scope, "rev-parse", "--abbrev-ref", "HEAD");
        var onBranch = current is not null && current != "HEAD";
        var remotes = (Git(c, scope, "remote") ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var upstream = Git(c, scope, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        string? UpstreamBranchOn(string remote) =>
            upstream is not null && upstream.StartsWith(remote + "/", StringComparison.Ordinal) ? upstream[(remote.Length + 1)..] : null;

        PushTarget Branch(string name, string source, string remote)
        {
            var sha = Git(c, scope, "rev-parse", "--verify", "-q", source + "^{commit}");
            return new PushTarget(name, false, source, remotes.Contains(remote, StringComparer.Ordinal) ? remote : null, sha);
        }

        if (words.Count == 0)
        {
            if (onBranch && upstream is not null && remotes.FirstOrDefault(r => UpstreamBranchOn(r) is not null) is { } fromUpstream)
            {
                targets.Add(Branch(UpstreamBranchOn(fromUpstream)!, "HEAD", fromUpstream));
            }

            return targets;
        }

        var remote = words[0];
        if (words.Count == 1)
        {
            if (onBranch)
            {
                targets.Add(Branch(UpstreamBranchOn(remote) ?? current!, "HEAD", remote));
            }

            return targets;
        }

        foreach (var spec in words.Skip(1))
        {
            var refspec = spec.TrimStart('+');
            if (refspec.StartsWith(':'))
            {
                continue;
            }

            var parts = refspec.Split(':', 2);
            var src = parts[0];
            var dst = parts.Length > 1 ? parts[1] : parts[0];
            if (src == "HEAD" && parts.Length == 1)
            {
                if (!onBranch)
                {
                    continue;
                }

                dst = current!;
            }
            else if (src == "HEAD" && !onBranch)
            {
                continue;
            }

            var isTag = dst.StartsWith("refs/tags/", StringComparison.Ordinal)
                || (!dst.StartsWith("refs/heads/", StringComparison.Ordinal) && Git(c, scope, "rev-parse", "--verify", "-q", "refs/tags/" + dst) is not null);
            if (isTag)
            {
                var name = dst.StartsWith("refs/tags/", StringComparison.Ordinal) ? dst["refs/tags/".Length..] : dst;
                var source = src.StartsWith("refs/", StringComparison.Ordinal) || src == "HEAD" ? src : "refs/tags/" + name;
                targets.Add(new PushTarget(name, true, source, null, Git(c, scope, "rev-parse", "--verify", "-q", source + "^{commit}")));
            }
            else
            {
                targets.Add(Branch(PushGuard.ShortName(dst), src, remote));
            }
        }

        return targets;
    }

    /// <summary>Appends <paramref name="ev"/> for every team, for timing and history. It closes nothing: only a release ref moving does (spec ah-ui-focus §4).</summary>
    private static void Append(GateContext c, string pool, List<string> teams, LedgerEvent ev)
    {
        foreach (var team in teams)
        {
            c.Services.Hierarchy.Progress.AppendLedger(pool, team, ev);
        }
    }
}
