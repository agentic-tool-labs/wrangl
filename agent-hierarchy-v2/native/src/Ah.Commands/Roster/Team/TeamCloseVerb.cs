using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// Close and reopen a team: a marker on the team record (<c>closed: {at, by}</c>), on each confirmed lane child too. Nothing is deleted.
/// Live members end only when the caller confirmed exactly who: the list it was shown is re-read here, and anyone new answers with the
/// fresh table and no marker. An Orchestrator session is never ended and is named as left running; it does not hold a closed team on show.
/// <c>hold</c> pauses first: it runs <c>quit</c> for the team (members save their work, the hold is sealed with its resume plan) and
/// closes nothing; the caller sends it again while it answers <c>winding-down</c>, then closes.
/// </summary>
internal static class TeamCloseVerb
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    private sealed record Scope(RosterContext C, string Team, DisbandVerb.TeamPlan Plan, List<JsonValue> Live, List<JsonValue> Unknown, List<string> LeftRunning, List<string> Warnings, HashSet<string> Strays);

    /// <summary>One <c>hold</c> call's wait for answers: inside herdr's 30 s fetch, with room for the quit's own reads and mail.</summary>
    private const int HoldCallSeconds = 10;

    /// <summary>The core both routes run: <c>{dir, team, by, reopen?, preview?, hold?, confirm_live?}</c>, <c>dir</c> already resolved.</summary>
    internal static JsonObject Apply(IRosterCliServices services, JsonObject input)
    {
        var dir = Text(input, "dir")!;
        var team = Text(input, "team")!;
        var record = services.Roster.ReadTeamRecord(dir, team);
        if (record.IsNull)
        {
            return Refused("unknown-team", $"no team {team}");
        }

        // a lane can be retired on its own, so it can be reopened on its own
        if (!Flag(input, "reopen") && OwnerGuard.ValidLaneChild(Context(services, dir, team), team, record) is { } lane)
        {
            return Refused("lane", $"{team} is a lane of {lane.Manager}: close that team");
        }

        var lanes = LaneChildren(services, dir, team, record);
        string[] names = [team, .. lanes];
        if (Flag(input, "reopen") && Flag(input, "preview"))
        {
            return Refused("bad-request", "a reopen has no preview: it deletes nothing and asks nothing");
        }

        if (Flag(input, "reopen"))
        {
            var reopened = Stamp(services, Text(input, "by") ?? "user");
            foreach (var name in names)
            {
                _ = SetMarker(services, dir, name, null, reopened);
            }

            return new JsonObject { ["ok"] = true, ["reopened"] = true, ["team"] = team, ["lanes"] = Strings(lanes) };
        }

        if (Flag(input, "hold"))
        {
            return Flag(input, "preview") ? Refused("bad-request", "a hold has no preview: ask for the preview first") : Hold(services, dir, team, Text(input, "by") ?? "user");
        }

        var scopes = names.Select(name => Read(services, dir, name)).ToList();
        var preview = Preview(team, record, scopes);
        if (Flag(input, "preview"))
        {
            return preview;
        }

        var confirmed = input.Get("confirm_live") is { IsArray: true } list ? list.AsArray().Where(static v => v.IsText).Select(static v => v.AsString()).ToHashSet(StringComparer.Ordinal) : [];
        var unconfirmed = scopes.SelectMany(static s => s.Live).Select(NameOf).Where(n => !confirmed.Contains(n)).ToList();
        if (unconfirmed.Count > 0)
        {
            var refused = Refused("live-changed", $"running and not confirmed: {string.Join(", ", unconfirmed)}");
            refused["preview"] = preview;
            return refused;
        }

        var ended = new List<string>();
        var stillLive = new List<string>();
        var warnings = scopes.SelectMany(static s => s.Warnings).ToList();
        foreach (var scope in scopes.Where(static s => s.Live.Count > 0))
        {
            // the team record is not reconciled, as disband does: its rows stay, so a reopen finds the team as it was
            var verified = TeardownSupport.CloseAndVerify(scope.C, scope.Live, scope.Plan.Transport, warnings);
            stillLive.AddRange(verified.StillLive);
            ended.AddRange(scope.Live.Select(NameOf).Where(n => !verified.StillLive.Contains(n)));
        }

        var marker = Stamp(services, Text(input, "by") ?? "user");
        foreach (var name in names)
        {
            var written = SetMarker(services, dir, name, marker).Result;
            if (!written.Ok)
            {
                warnings.Add($"{name}: the closed marker was not written: {written.Error ?? written.ErrorCode ?? "the write failed"}");
            }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["closed"] = true,
            ["team"] = team,
            ["at"] = marker["at"],
            ["ended"] = Strings(ended),
            ["still_live"] = Strings(stillLive),
            ["left_running"] = Strings(scopes.SelectMany(static s => s.LeftRunning)),
            ["lanes"] = Strings(lanes),
            ["warnings"] = Strings(warnings),
        };
    }

    /// <summary>
    /// Pause before a close: <c>quit</c> for the team and its lanes, run here for the user (the route's guard is the proof), one short
    /// call at a time. <c>{ok, hold, team, phase, sealed, waiting_on, unanswered}</c>; a refusal of the quit (a stopped team, a lane)
    /// answers as a refusal with the quit's own words. Nothing is closed and no session ends.
    /// </summary>
    private static JsonObject Hold(IRosterCliServices services, string dir, string team, string by)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(dir)) ?? dir;
        using var stdout = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var c = new RosterContext(services, RosterArgs.Parse(["--team", team, "--note", $"paused from the UI by the {by}, before a close"]), "quit", root, stdout, TextWriter.Null)
        {
            DaemonPrincipal = true,
            QuitCallBudgetSeconds = HoldCallSeconds,
            TrapRefusals = true,
            TeamArg = team,
            TeamFile = team,
        };
        c.PinDir(dir);
        int exit;
        try
        {
            exit = QuitVerb.Run(c);
        }
        catch (RosterFailure refusal)
        {
            return Refused(refusal.Refused ?? "hold-refused", refusal.Message);
        }

        var report = JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(stdout.ToString()), out var parsed) && parsed.Kind == JsonKind.Map ? parsed.AsObject() : new JsonObject();
        if (report.Get("refused") is { IsText: true } why)
        {
            return Refused("hold-refused", why.AsString());
        }

        var isSealed = Text(report, "phase") == "held";
        return new JsonObject
        {
            ["ok"] = true,
            ["hold"] = true,
            ["team"] = team,
            ["phase"] = isSealed ? "held" : "winding-down",
            ["sealed"] = isSealed,
            ["waiting_on"] = report.Get("waiting_on") ?? new JsonArray(),
            // exit 3: sealed when the window ended with a member that never answered; exit 1: a member's mail failed
            ["unanswered"] = exit == 3,
            ["mail_failed"] = report.Get("mail_failed") ?? false,
            ["plan"] = report.Get("plan") ?? JsonValue.Null,
        };
    }

    /// <summary>
    /// The daemon's retire: the same marker on this one record, by <c>retired</c>, its lanes left to their own pass. Nothing is ended. A reopen newer than
    /// <paramref name="lastWorkMs"/> wins, read here since the caller's reading can be behind the file. Returns why it could not write, or null.
    /// </summary>
    internal static string? Retire(IRosterCliServices services, string dir, string team, long lastWorkMs)
    {
        var record = services.Roster.ReadTeamRecord(dir, team);
        if (record.IsNull)
        {
            return "the team record does not read";
        }

        if (record.Member("reopened") is { IsMap: true } reopened && Text(reopened.AsObject(), "at") is { } at
            && DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, out var when) && when.ToUnixTimeMilliseconds() >= lastWorkMs)
        {
            return null;
        }

        var result = SetMarker(services, dir, team, Stamp(services, RetiredBy));
        return result.Result.Ok ? null : result.Result.Error ?? result.Result.ErrorCode ?? "the write failed";
    }

    /// <summary>The verbs: thin clients of <c>POST /v1/team/close</c>, which answers 403 to anyone but the user.</summary>
    internal static int Client(RosterContext c, bool reopen)
    {
        if (c.Opts.Keys.FirstOrDefault(static key => key is not ("preview" or "hold" or "confirm-live" or "cwd")) is { } unknown)
        {
            throw new RosterFailure($"{c.Command}: unrecognized flag --{unknown}");
        }

        if (c.Opts.Positionals.Count != 1)
        {
            throw new RosterFailure($"{c.Command}: name one team");
        }

        if (c.Services.Daemon is not { } daemon)
        {
            c.Stderr.WriteLine($"ah roster {c.Command}: the daemon client is not composed");
            return 70;
        }

        var body = new JsonObject { ["dir"] = c.Dir, ["team"] = c.Opts.Positionals[0], ["reopen"] = reopen };
        if (c.Opts.IsTrue("preview"))
        {
            body["preview"] = true;
        }

        if (c.Opts.IsTrue("hold"))
        {
            body["hold"] = true;
        }

        if (c.Opts.All("confirm-live") is { Count: > 0 } live)
        {
            body["confirm_live"] = Strings(live);
        }

        var reply = daemon.RequestAsync(new DaemonRequest("POST", "/v1/team/close", System.Text.Encoding.UTF8.GetBytes(JsonText.Stringify(body, 0)), Wait, StartIfDown: true)).GetAwaiter().GetResult();
        var text = System.Text.Encoding.UTF8.GetString(reply.Body.Span);
        if (reply.Status == 200)
        {
            c.Stdout.WriteLine(text);
            return 0;
        }

        c.Stderr.WriteLine(reply.Status == 0 ? "ah roster: the daemon did not answer" : text);
        return reply.Status == 409 ? 3 : 2;
    }

    private static RosterContext Context(IRosterCliServices services, string dir, string team)
    {
        // <root>/.claude/ah2: the commands a close runs start in the project
        var root = Path.GetDirectoryName(Path.GetDirectoryName(dir)) ?? dir;
        var c = new RosterContext(services, RosterArgs.Parse([]), "close", root, TextWriter.Null, TextWriter.Null) { TeamFile = team };
        c.PinDir(dir);
        return c;
    }

    private static Scope Read(IRosterCliServices services, string dir, string team)
    {
        var c = Context(services, dir, team);
        var warnings = new List<string>();
        var read = TeardownSupport.TeardownTeam(c, dir, warnings);
        var plan = DisbandVerb.PlanTeam(c, dir, read, warnings);
        var teardown = services.Launch!.Teardown;
        var live = new List<JsonValue>();
        var unknown = new List<JsonValue>();
        // A stray is a herdr agent named for the checkout that no team file claims: it is outside the team, so it is neither offered nor ended.
        var strays = plan.Set.Closable.Where(static m => m.Member("stray") is { IsTruthy: true }).Select(NameOf).ToHashSet(StringComparer.Ordinal);
        foreach (var m in plan.Set.Closable.Where(m => m.IsMap && !strays.Contains(NameOf(m))))
        {
            var state = teardown.MemberLiveness(dir, m, team);
            if (state.Get("live") is { IsTruthy: true })
            {
                live.Add(m);
            }
            else if (state.Get("indeterminate") is { IsTruthy: true })
            {
                unknown.Add(m);
            }
        }

        var closable = plan.Set.Closable.Select(NameOf).ToHashSet(StringComparer.Ordinal);
        var left = new List<string>();
        if (read.Team.Member("orchestrator") is { IsMap: true } && !services.Roster.TeamIsOrphaned(read.Team))
        {
            left.Add("orchestrator");
        }

        if (plan.Eff.Member("members") is { IsArray: true } rows)
        {
            left.AddRange(rows.AsArray().Where(m => m.IsMap && !closable.Contains(NameOf(m)) && teardown.MemberLiveness(dir, m, team).Get("live") is { IsTruthy: true }).Select(NameOf));
        }

        return new Scope(c, team, plan, live, unknown, [.. left.Distinct(StringComparer.Ordinal)], warnings, strays);
    }

    private static JsonObject Preview(string team, JsonValue record, List<Scope> scopes)
    {
        JsonArray Rows(Func<Scope, IEnumerable<JsonValue>> rows)
        {
            var array = new JsonArray();
            foreach (var scope in scopes)
            {
                // two rows can name one member (an older split copy of the record), so the first verdict stands
                var verdicts = scope.Plan.Release.Verdicts.GroupBy(static v => v.Member, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
                array.AddRange(rows(scope).Select(m =>
                {
                    var row = new JsonObject { ["team"] = scope.Team, ["member"] = NameOf(m), ["role"] = m.Member("role") ?? JsonValue.Null };
                    if (verdicts.TryGetValue(NameOf(m), out var v))
                    {
                        row["status"] = v.Status;
                        row["detail"] = v.Detail;
                    }

                    return (JsonValue)row;
                }));
            }

            return array;
        }

        var held = new JsonArray();
        foreach (var scope in scopes)
        {
            var live = scope.Live.Select(NameOf).ToHashSet(StringComparer.Ordinal);
            held.AddRange(scope.Plan.Release.Held.Where(v => !live.Contains(v.Member) && !scope.Strays.Contains(v.Member)).Select(v => (JsonValue)new JsonObject { ["team"] = scope.Team, ["member"] = v.Member, ["status"] = v.Status, ["detail"] = v.Detail }));
        }

        var preview = new JsonObject
        {
            ["ok"] = true,
            ["preview"] = true,
            ["team"] = team,
            ["live"] = Rows(static s => s.Live),
            ["unknown"] = Rows(static s => s.Unknown),
            ["left_running"] = Strings(scopes.SelectMany(static s => s.LeftRunning)),
            ["held"] = held,
            ["warnings"] = Strings(scopes.SelectMany(static s => s.Warnings)),
        };
        if (record.Member("closed") is { IsMap: true } closed)
        {
            preview["closed_at"] = closed.Member("at") ?? JsonValue.Null;
        }

        return preview;
    }

    private static List<string> LaneChildren(IRosterCliServices services, string dir, string team, JsonValue record)
    {
        var children = new List<string>();
        if (record.Member("streams") is not { IsMap: true } streams)
        {
            return children;
        }

        foreach (var stream in streams.AsObject().Keys)
        {
            var child = HierarchyModule.LaneChildName(team, stream);
            var childRecord = services.Roster.ReadTeamRecord(dir, child);
            if (!childRecord.IsNull && HierarchyModule.ConfirmedLaneStream(team, record, child, childRecord) is not null)
            {
                children.Add(child);
            }
        }

        return children;
    }

    /// <summary>The record's marker was written by the daemon's retire, not by a user's close.</summary>
    internal static bool Retired(JsonValue record) =>
        record.Member("closed") is { IsMap: true } marker && marker.Member("by") is { IsText: true } by && by.AsString() == RetiredBy;

    /// <summary>The record carries a marker a user chose, which <c>reap</c> never deletes behind. A marker with no readable <c>by</c> counts as one.</summary>
    internal static bool UserClosed(JsonValue record) => record.Member("closed") is { IsMap: true } && !Retired(record);

    private const string RetiredBy = "retired";

    private static JsonObject Stamp(IRosterCliServices services, string by) =>
        new() { ["at"] = services.Clock.UtcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture), ["by"] = by };

    /// <summary>
    /// Writes or removes the marker, the record copied whole. A close keeps the first marker, except that a user's close replaces a retired one, since
    /// <c>reap</c> clears a retired team and keeps a closed one. Removing none writes nothing. A removal leaves <c>reopened</c>, which holds off the daemon's
    /// retire until the team has done new work.
    /// </summary>
    private static StateUpdateResult SetMarker(IRosterCliServices services, string dir, string team, JsonObject? marker, JsonObject? reopened = null) =>
        services.Hierarchy.StateCalls.UpdateTeam(dir, team, doc =>
        {
            var takesOver = marker is not null && Retired(doc) && Text(marker, "by") != RetiredBy;
            if (!doc.IsMap || (doc.Member("closed") is { IsMap: true } == (marker is not null) && !takesOver))
            {
                return null;
            }

            var copy = doc.AsObject().Clone();
            if (marker is null)
            {
                copy.Remove("closed");
                copy["reopened"] = reopened!;
            }
            else
            {
                copy["closed"] = marker;
            }

            return TeamFileOutcome.Write(copy);
        });

    private static string NameOf(JsonValue m) => m.IsText ? m.AsString() : MemberPlanning.JsConvertText(m.Member("name"));

    private static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : null;

    private static bool Flag(JsonObject o, string key) => o.Get(key) is { IsBool: true } v && v.AsBool();

    private static JsonObject Refused(string reason, string detail) => new() { ["refused"] = new JsonObject { ["reason"] = reason, ["detail"] = detail } };

    private static JsonArray Strings(IEnumerable<string> values) => TeardownSupport.Strings([.. values]);
}
