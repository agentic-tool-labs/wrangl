using System.Globalization;
using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// <c>permissions diagnose &lt;member&gt;</c>: why a member was stopped by the auto-mode classifier. It reads the member's launch file,
/// the member surface and the last denial on record, and names the first cause that holds (<see cref="RosterModule.PermissionCauseOf"/>). It changes nothing.
/// </summary>
internal static class PermissionDiagnose
{
    private const double DayMs = 24 * 3_600_000;

    private sealed record Denial(string At, string Label, string Command);

    /// <summary>A member name is a plain file-name word: no path separator, never <c>.</c> or <c>..</c>.</summary>
    internal static bool IsMemberName(string name) =>
        name.Length is > 0 and <= 64 && name is not ("." or "..") && name.All(static ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-');

    internal static int Run(RosterContext c, string member)
    {
        if (!IsMemberName(member))
        {
            throw new RosterFailure("permissions diagnose: the member name must be letters, digits, '.', '_' or '-' (no path)");
        }

        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        JsonValue? row = team.Member("members") is { IsArray: true } list
            ? list.AsArray().Cast<JsonValue?>().FirstOrDefault(m => m!.Value.IsMap && m.Value.Member("name") is { IsText: true } n && n.AsString() == member)
            : null;
        var root = row?.Member("expected_root") is { IsText: true } r ? r.AsString() : null;
        var session = row?.Member("session_id") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null;
        if (row is null)
        {
            // a name the team does not list is reported and nothing about it is read
            var where = c.TeamFile is { } named ? $" (team {named})" : string.Empty;
            if (c.Opts.IsTrue("json"))
            {
                c.Out(new JsonObject { ["member"] = member, ["team"] = c.TeamFile is null ? JsonValue.Null : JsonValue.FromString(c.TeamFile), ["in_team"] = false });
            }
            else
            {
                c.Stdout.WriteLine($"member:      {member}{where} (not in this team)");
            }

            return 0;
        }

        var denial = LastDenial(c, member);
        var facts = RosterModule.ReadPermissionFacts(c.Services.Files, c.Services.Environment.Plugin?.Root, c.TeamHome, member, denial?.Command);
        var cause = denial is null
            ? null
            : RosterModule.PermissionCauseOf(facts, Ah.Config.ConfigModule.EvaluateMemberLaunchConsent(c.Services.Files, c.ConsentAsksPath, MemberAllowEnabled(c), RosterModule.MemberAllowOnAt(c.Services.Files, c.Services.Config.UserConfigPath())));

        var lastDenial = denial is null ? "none in the last 24 h" : $"{denial.At} [{denial.Label}] `{denial.Command}`";
        var causeText = cause is { } k ? $"{k.N} {k.Name}: {k.Fix}" : "none";
        if (c.Opts.IsTrue("json"))
        {
            c.Out(new JsonObject
            {
                ["member"] = member,
                ["team"] = c.TeamFile is null ? JsonValue.Null : JsonValue.FromString(c.TeamFile),
                ["root"] = root is null ? JsonValue.Null : JsonValue.FromString(root),
                ["session"] = session is null ? JsonValue.Null : JsonValue.FromString(session),
                ["launch_file"] = facts.Launch,
                ["sandbox"] = facts.Sandbox,
                ["surface"] = facts.Surface,
                ["last_denial"] = denial is null
                    ? JsonValue.Null
                    : JsonValue.FromObject(new JsonObject { ["at"] = denial.At, ["label"] = denial.Label, ["command"] = denial.Command }),
                ["cause"] = cause is { } found
                    ? JsonValue.FromObject(new JsonObject { ["n"] = (double)found.N, ["name"] = found.Name, ["fix"] = found.Fix })
                    : JsonValue.Null,
            });
            return 0;
        }

        var teamText = c.TeamFile is { } t ? $" (team {t})" : string.Empty;
        var o = c.Stdout;
        o.WriteLine($"member:      {member}{teamText}  root: {root ?? "unknown"}  session: {session ?? "unknown"}");
        o.WriteLine($"launch file: {facts.Launch}");
        o.WriteLine($"sandbox:     {facts.Sandbox}");
        o.WriteLine($"surface:     {facts.Surface}");
        o.WriteLine($"last denial: {lastDenial}");
        o.WriteLine($"cause:       {causeText}");
        return 0;
    }

    private static bool MemberAllowEnabled(RosterContext c) =>
        RosterModule.MemberAllowEnabled(c.Services.Files, [c.MemberAllowProjectConfig,c.Services.Config.UserConfigPath()]);

    /// <summary>The member's latest <c>CLASSIFIER-DENIED</c> watch-event row in the last 24 hours.</summary>
    private static Denial? LastDenial(RosterContext c, string member)
    {
        var now = c.Services.Clock.UtcNow.ToUnixTimeMilliseconds();
        Denial? best = null;
        var bestMs = double.NegativeInfinity;
        foreach (var row in c.Services.Hierarchy.Core.PeerRecordsWhere(new PeerQuery(Types: new HashSet<string>(StringComparer.Ordinal) { "watch-event" })))
        {
            if (!row.IsMap || row.Member("type") is not { IsText: true } type || type.AsString() != "watch-event"
                || row.Member("kind") is not { IsText: true } kind || kind.AsString() != "CLASSIFIER-DENIED"
                || (row.Member("verdict") is { IsText: true } verdict && verdict.AsString() == "no-verdict")
                || row.Member("member") is not { IsText: true } who || who.AsString() != member
                || row.Member("ts") is not { IsText: true } ts)
            {
                continue;
            }

            if (!DateTimeOffset.TryParse(ts.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            {
                continue;
            }

            var ms = when.ToUnixTimeMilliseconds();
            if (now - ms > DayMs || ms <= bestMs)
            {
                continue;
            }

            bestMs = ms;
            best = new Denial(
                row.Member("at") is { IsText: true, } at && at.AsString().Length > 0 ? at.AsString() : ts.AsString(),
                row.Member("label") is { IsText: true } label ? label.AsString() : "unknown",
                row.Member("command") is { IsText: true } command ? command.AsString() : "(unknown)");
        }

        return best;
    }
}
