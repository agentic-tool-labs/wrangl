using Ah.Abstractions;
using Ah.Config;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// PostToolUse (Bash): a successful <c>git commit</c> records a <c>coding</c> entry on the one open item the member holds: title the
/// subject, body the sha (ah-work-items S6). Holding none or several logs nothing. Never fails the tool.
/// <para>
/// When the line is one command or commands joined by <c>&amp;&amp;</c>, the exit status speaks for the commit; a substitution in its words (a
/// message from <c>$(cat &lt;&lt;EOF …)</c>) does not change that. After <c>;</c> or in a pipe it does not, and HEAD may be an older commit when
/// this one failed, so HEAD must have been committed within <see cref="FreshFor"/>. A sha already logged is never logged again. A commit in a
/// subshell or a substitution is not looked at, nor is one naming its git directory or work tree, whose repository the working directory does not tell.
/// </para>
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class PostToolUseCommitLog
{
    internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);

    public static HookOutcome Run(GateContext c)
    {
        var command = c.Command() ?? string.Empty;
        if (c.ToolName() != "Bash" || !command.Contains("commit", StringComparison.Ordinal) || c.Services.WorkCommit is not { } log || !PostToolUseLedgerRecord.Succeeded(c))
        {
            return HookEmit.Silent;
        }

        try
        {
            var cwd = c.Cwd();
            var line = PushShell.Parse(command).Events.Where(static e => e is PushCommand).ToList();
            var invs = new List<GitInvocation>();
            var walked = new PushState(cwd);
            PushShell.Collect(line, walked, cwd, invs, []);
            if (walked.Budget.Spent)
            {
                return HookEmit.Silent;
            }

            var statusSpeaks = PushShell.StatusBearing(line);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var inv in invs.Where(static i => i.Sub == "commit" && !i.OverridesTree && !i.Args.Any(static a => a is "--dry-run" or "--help")))
            {
                var head = c.Services.Conventions.TryGit(new GitScope(inv.Cwd, inv.GitDir, inv.WorkTree), ["log", "-1", "--format=%H%n%ct%n%s"])?.Trim().Split('\n');
                if (head is not { Length: >= 2 } || (!statusSpeaks && !Fresh(c, head[1])) || !seen.Add(head[0]))
                {
                    continue;
                }

                // The commit's own directory, so a commit in another repository finds no item held there, and one in a linked worktree finds this one's.
                log(inv.Cwd, c.Input.NonEmpty("session_id"), head.Length > 2 ? head[2].Trim() : head[0], head[0]);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }

        return HookEmit.Silent;
    }

    private static bool Fresh(GateContext c, string committedAt) =>
        long.TryParse(committedAt, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
        && c.Services.Clock.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds) <= FreshFor;
}
