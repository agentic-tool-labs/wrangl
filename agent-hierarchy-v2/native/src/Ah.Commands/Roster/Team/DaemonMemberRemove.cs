using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// ahd's removal of one member proven dead (ah2-592): its work read first, then the close <c>ah roster dismiss &lt;member&gt; --close</c>
/// runs, in process. Reachable only from code that composes it; no command line reaches it.
/// </summary>
public interface IMemberRemoval
{
    /// <summary>
    /// Reads the work of <paramref name="member"/> of <paramref name="team"/>, then closes its pane and removes its row as dismiss does.
    /// Nothing is closed when the work could not be read. <paramref name="captured"/> is given the work as read, before anything is closed.
    /// </summary>
    MemberRemovalResult Remove(string hierarchyDir, string projectRoot, string team, string member, Action<JsonObject>? captured = null);
}

/// <summary>Whether the row left the team, the work as read before the close (null when it could not be read), and why it was not removed.</summary>
public sealed record MemberRemovalResult(bool Removed, JsonObject? Capture, string? Why)
{
    /// <summary>The ids of the open briefs the close abandoned, their checklist items left waiting on the Orchestrator.</summary>
    public IReadOnlyList<string> Abandoned { get; init; } = [];
}

internal sealed class DaemonMemberRemove(IRosterCliServices services) : IMemberRemoval
{
    public MemberRemovalResult Remove(string hierarchyDir, string projectRoot, string team, string member, Action<JsonObject>? captured = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(hierarchyDir);
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentException.ThrowIfNullOrEmpty(team);
        ArgumentException.ThrowIfNullOrEmpty(member);
        using var stdout = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var c = new RosterContext(services, RosterArgs.Parse([member, "--close", "--team", team]), "dismiss", projectRoot, stdout, stderr)
        {
            DaemonPrincipal = true,
            TeamArg = team,
            TeamFile = team,
        };
        c.PinDir(hierarchyDir);
        JsonObject capture;
        try
        {
            capture = Capture(c, member);
            captured?.Invoke(capture);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new MemberRemovalResult(false, null, $"its work could not be read: {e.Message}");
        }

        string? failed = null;
        try
        {
            // this is the daemon: any mail the close sends must go to the mailbox files, its daemon client refuses to call itself
            using var direct = Ah.Mesh.MeshModule.BeginHoldExempt();
            _ = DismissVerb.Run(c);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            failed = string.IsNullOrWhiteSpace(e.Message) ? "the close failed with no reason" : e.Message;
        }

        // the row's absence decides, read after the close whatever it returned or threw: a step after the row's removal may still fail
        var report = JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(stdout.ToString()), out var parsed) && parsed.Kind == JsonKind.Map ? parsed : JsonValue.Null;
        var abandoned = report.Member("briefs_abandoned") is { IsArray: true } ids ? ids.AsArray().Where(static id => id.IsText).Select(static id => id.AsString()).ToList() : [];
        return c.Services.Roster.TeamMemberByName(c.Dir, member, c.TeamFile).IsMap
            ? new MemberRemovalResult(false, capture, failed ?? (stderr.ToString().Trim() is { Length: > 0 } why ? why : "the close was not verified"))
            : new MemberRemovalResult(true, capture, failed) { Abandoned = abandoned };
    }

    /// <summary>The checkpoint's per-member record of <paramref name="member"/>, with its uncommitted files and commits ahead of the run's branch counted.</summary>
    private static JsonObject Capture(RosterContext c, string member)
    {
        var dir = c.Dir;
        var row = c.Services.Roster.TeamMemberByName(dir, member, c.TeamFile);
        if (!row.IsMap)
        {
            throw new RosterFailure($"{member} is not in the record of team {c.TeamFile}");
        }

        var cache = new Dictionary<string, (IReadOnlyList<string> Pools, List<JsonObject> Exchanges)>(StringComparer.Ordinal);
        var seen = CheckpointVerb.See(c, dir, row, c.Services.Hierarchy.Roster.AttributedRoster(dir), cache);
        var open = CheckpointVerb.OpenRequests(c, ReleaseGate.ExchangesFor(c, dir, seen.Tree, cache), member);
        int? uncommitted = seen.Tree is { } tree && ReleaseGate.Git(c, tree, "status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none") is { Ok: true } status
            ? status.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : null;
        var ahead = CheckpointVerb.CommitsAheadOfRun(c, seen.Tree, WorkTreeAssignment.TreeOf(c, c.Cwd)?.Root);
        var ids = new JsonArray();
        ids.AddRange(open.Select(static id => (JsonValue)id));
        return new JsonObject
        {
            ["name"] = seen.Name,
            ["role"] = seen.Role,
            ["session"] = seen.Session is null ? JsonValue.Null : seen.Session,
            ["branch"] = seen.Branch is null ? JsonValue.Null : seen.Branch,
            ["worktree"] = seen.Tree is null ? JsonValue.Null : seen.Tree,
            ["head"] = seen.Head is null ? JsonValue.Null : seen.Head,
            ["uncommitted"] = uncommitted is { } u ? u : JsonValue.Null,
            ["ahead"] = ahead is { } a ? a : JsonValue.Null,
            ["open_requests"] = ids,
        };
    }
}
