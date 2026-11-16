using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Roster;

namespace Ah.TestKit;

/// <summary>
/// Puts state down for a test through the real ops, so a plant lands wherever the home's authority says: the file, or the database
/// once the domain has been cut over. A plant the op refuses throws, so a test never runs on state it does not have.
/// </summary>
public static class TestPlant
{
    /// <summary>Writes the team document <paramref name="json"/> to the team file <paramref name="path"/> with the <c>team</c> domain's <c>write</c>.</summary>
    public static void Team(IRosterCore roster, IStateWriter state, string path, string json) => Team(roster, state, path, JsonText.Parse(json));

    /// <inheritdoc cref="Team(IRosterCore, IStateWriter, string, string)"/>
    public static void Team(IRosterCore roster, IStateWriter state, string path, JsonValue team)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(state);
        var dir = PoolOf(path);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _ = Run(state, "team", "write", new JsonObject { ["path"] = path, ["dir"] = dir, ["expect"] = roster.ReadTeamWithDigest(path)["digest"], ["team"] = team }, dir);
    }

    /// <summary>Removes the team stored for <paramref name="path"/> with the <c>team</c> domain's <c>clear</c>.</summary>
    public static void ClearTeam(IRosterCore roster, IStateWriter state, string path)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(state);
        var dir = PoolOf(path);
        _ = Run(state, "team", "clear", new JsonObject { ["path"] = path, ["dir"] = dir, ["expect"] = roster.ReadTeamWithDigest(path)["digest"] }, dir);
    }

    /// <summary>Registers a session with the <c>mailbox</c> domain's <c>register</c>, each box an address and its directory.</summary>
    public static void Registration(IStateWriter state, string sessionId, string teamFile, string cwd, params (string Address, string Dir)[] boxes)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(boxes);
        var list = new JsonArray();
        foreach (var (address, dir) in boxes)
        {
            list.Add(new JsonObject { ["address"] = address, ["dir"] = dir });
        }

        var result = Run(state, "mailbox", "register", new JsonObject { ["session_id"] = sessionId, ["boxes"] = list, ["team_file"] = teamFile, ["cwd"] = cwd }, cwd);
        if (result.Member("registered") is not { IsTruthy: true })
        {
            throw new InvalidOperationException($"the plant of session {sessionId} was refused: {JsonText.Stringify(result, 0)}");
        }
    }

    /// <summary>Appends each of <paramref name="rows"/> (one JSON object each) with the <c>peer-records</c> domain's <c>append</c>.</summary>
    public static void PeerRows(IStateWriter state, string cwd, params string[] rows)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            _ = Run(state, "peer-records", "append", new JsonObject { ["rec"] = JsonText.Parse(row) }, cwd);
        }
    }

    /// <summary>Appends <paramref name="entry"/> to the box <paramref name="boxDir"/> with the <c>mailbox</c> domain's <c>append</c>.</summary>
    public static void Mail(IStateWriter state, string boxDir, JsonValue entry, string cwd)
    {
        ArgumentNullException.ThrowIfNull(state);
        _ = Run(state, "mailbox", "append", new JsonObject { ["dir"] = boxDir, ["entry"] = entry }, cwd);
    }

    /// <summary>The hierarchy directory a team file belongs to: <c>&lt;dir&gt;/teams/x.json</c> or the legacy <c>&lt;dir&gt;/team.json</c>.</summary>
    private static string PoolOf(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        return Path.GetFileName(parent) == "teams" ? Path.GetDirectoryName(parent)! : parent;
    }

    private static JsonValue Run(IStateWriter state, string domain, string op, JsonObject args, string dir)
    {
        _ = Directory.CreateDirectory(dir);
        var result = state.Invoke(domain, op, JsonText.ToUtf8(args, 0), new RequestContext(dir, dir, string.Empty, string.Empty));
        return result.Ok
            ? JsonText.Parse(result.ResultJson.Span)
            : throw new InvalidOperationException($"the plant through {domain} {op} was refused: {result.Error ?? result.ErrorCode}");
    }
}
