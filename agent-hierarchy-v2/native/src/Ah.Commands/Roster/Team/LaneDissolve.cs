using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// What a lane's closing does to the members that answered to its sub-orchestrator: each goes back to the manager team's
/// Orchestrator and is told by one <c>member-transfer</c> event. Members that live in the lane's own team file keep that file and
/// answer to the manager team's Orchestrator; the file is kept, marked dissolved.
/// </summary>
internal static class LaneDissolve
{
    private const string Orchestrator = "orchestrator";

    /// <summary>
    /// Returns every member that answered to the lane <paramref name="stream"/> of <paramref name="manager"/> to the manager's
    /// Orchestrator: a manager row whose lead is the lane loses it, and a member that lives in the lane's own team file moves into
    /// the manager's team file with no lead. Each is announced before its row changes; an event that did not go out is a line in
    /// <paramref name="warnings"/>. The lane's own team file is touched only when it records <paramref name="manager"/> and
    /// <paramref name="stream"/> as its parent. <paramref name="managerFile"/> is the manager's team file argument (null for the
    /// default team). Returns the names of the members returned.
    /// </summary>
    internal static List<string> Revert(RosterContext c, string dir, string manager, string? managerFile, string stream, string? subOrchestrator, List<string> warnings)
    {
        var child = HierarchyModule.LaneChildName(manager, stream);
        var owner = new LeadRef(child, subOrchestrator is { Length: > 0 } so ? so : Orchestrator);
        var toManager = new LeadRef(manager, Orchestrator);
        var reverted = new List<string>();

        var planned = new List<string>();
        foreach (var row in c.Services.Roster.ReadTeamRecord(dir, manager).Member("members") is { IsArray: true } list ? list.AsArray() : [])
        {
            if (row.IsMap && HierarchyModule.LeadOf(row) is { } lead && lead.Team == child && MemberPlanning.Text(row.Member("name")) is { Length: > 0 } name)
            {
                planned.Add(name);
            }
        }

        foreach (var name in planned)
        {
            Tell(c, dir, name, manager, owner, toManager, warnings);
        }

        if (planned.Count > 0)
        {
            c.Services.Hierarchy.StateCalls.UpdateTeam(
                dir,
                managerFile,
                doc =>
                {
                    var rows = new JsonArray();
                    var changed = false;
                    foreach (var row in doc.Member("members") is { IsArray: true } list ? list.AsArray() : [])
                    {
                        if (row.IsMap && HierarchyModule.LeadOf(row) is { } lead && lead.Team == child)
                        {
                            var next = row.AsObject().Clone();
                            _ = next.Remove("lead");
                            rows.Add(next);
                            changed = true;
                        }
                        else
                        {
                            rows.Add(row);
                        }
                    }

                    if (!changed)
                    {
                        return null;
                    }

                    var updated = doc.AsObject().Clone();
                    updated["members"] = rows;
                    return TeamFileOutcome.Write(updated);
                });
            reverted.AddRange(planned);
        }

        if (IsLaneOf(c.Services.Roster.ReadTeamRecord(dir, child), manager, stream))
        {
            reverted.AddRange(Rehome(c, dir, manager, managerFile, child, owner, toManager, warnings));
            MarkDissolved(c, dir, manager, child);
        }

        return reverted;
    }

    /// <summary>Whether <paramref name="record"/> is a team file that names the lane <paramref name="stream"/> of <paramref name="manager"/> as its parent.</summary>
    internal static bool IsLaneOf(JsonValue record, string manager, string stream) => HierarchyModule.IsLaneOf(record, manager, stream);

    private static void Tell(RosterContext c, string dir, string name, string manager, LeadRef from, LeadRef to, List<string> warnings)
    {
        var told = TransferVerb.Announce(c, dir, c.Services.Hierarchy.Core.NewId(), name, manager, from, to, null);
        if (told.Member("warning") is { IsText: true } warning)
        {
            warnings.Add($"{name}: {warning.AsString()}");
        }
        else if (told.Member("reached") is not { IsArray: true } reached || reached.AsArray().Count == 0)
        {
            warnings.Add($"{name}: the member-transfer event reached nobody");
        }
    }

    private static readonly string[] LiveKeys = ["session_id", "pid", "transport_id"];

    private static readonly string[] IdentityKeys = ["name", "session_id", "pid", "pid_start", "stream", "transport_id"];

    private static string IdentityOf(JsonValue row) =>
        string.Join('|', IdentityKeys.Select(k => JsonText.Stringify(row.Member(k) ?? JsonValue.Null, 0)));

    private static bool SameMember(JsonValue a, JsonValue b) =>
        LiveKeys.Any(k => a.Member(k) is { IsNull: false } && b.Member(k) is { IsNull: false }) && IdentityOf(a) == IdentityOf(b);

    private static JsonValue? RowNamed(JsonValue record, string name) =>
        record.Member("members") is { IsArray: true } rows
            ? rows.AsArray().Where(m => m.IsMap && MemberPlanning.Text(m.Member("name")) == name).Select(m => (JsonValue?)m).FirstOrDefault()
            : null;

    private static void RemoveFromChild(RosterContext c, string dir, string child, string name) =>
        c.Services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            child,
            doc =>
            {
                if (doc.IsNull)
                {
                    return null;
                }

                var next = new JsonArray();
                foreach (var existing in doc.Member("members") is { IsArray: true } current ? current.AsArray() : [])
                {
                    if (!(existing.IsMap && MemberPlanning.Text(existing.Member("name")) == name))
                    {
                        next.Add(existing);
                    }
                }

                var updated = doc.AsObject().Clone();
                updated["members"] = next;
                return TeamFileOutcome.Write(updated);
            });

    /// <summary>
    /// Moves each member row of the lane's own team file into the manager's team file with no lead, so it answers to the manager's
    /// Orchestrator. A member whose name the manager's team already holds with the same identity was moved by an earlier close that
    /// stopped half way: its row is removed from the lane's file. One with another identity stays in the lane's file, which routes
    /// it to the manager's Orchestrator once dissolved. Both are named in <paramref name="warnings"/>, and so is each moved member,
    /// because a live session is registered under the lane's team file until it is dismissed or registers again.
    /// </summary>
    private static List<string> Rehome(RosterContext c, string dir, string manager, string? managerFile, string child, LeadRef from, LeadRef to, List<string> warnings)
    {
        var moved = new List<string>();
        var managerRecord = c.Services.Roster.ReadTeamRecord(dir, manager);
        foreach (var row in c.Services.Roster.ReadTeamRecord(dir, child).Member("members") is { IsArray: true } list ? list.AsArray().ToList() : [])
        {
            if (!row.IsMap || MemberPlanning.Text(row.Member("name")) is not { Length: > 0 } name)
            {
                continue;
            }

            if (RowNamed(managerRecord, name) is { } held)
            {
                if (SameMember(held, row))
                {
                    RemoveFromChild(c, dir, child, name);
                    warnings.Add($"{name}: an earlier close had already moved it into {manager}; its row is removed from {child}");
                    moved.Add(name);
                }
                else
                {
                    warnings.Add($"{name}: the team {manager} already has a different member of that name, so it stays in {child} and answers to orchestrator@{manager}");
                }

                continue;
            }

            Tell(c, dir, name, manager, from, to, warnings);
            c.Services.Hierarchy.StateCalls.UpdateTeam(
                dir,
                managerFile,
                doc =>
                {
                    if (doc.IsNull || RowNamed(doc, name) is not null)
                    {
                        return null;
                    }

                    var fresh = RowNamed(c.Services.Roster.ReadTeamRecord(dir, child), name) ?? row;
                    var moving = fresh.AsObject().Clone();
                    _ = moving.Remove("lead");
                    var next = new JsonArray();
                    foreach (var existing in doc.Member("members") is { IsArray: true } current ? current.AsArray() : [])
                    {
                        next.Add(existing);
                    }

                    next.Add(moving);
                    var updated = doc.AsObject().Clone();
                    updated["members"] = next;
                    return TeamFileOutcome.Write(updated);
                });
            RemoveFromChild(c, dir, child, name);
            warnings.Add($"{name}: now listed in {manager}; a live session of it is registered under {child}, so dismiss and respawn it or let it register again before sending to {name}@{manager}");
            moved.Add(name);
        }

        return moved;
    }

    /// <summary>
    /// The lane's own team file, when it exists: its state becomes dissolved, and a member that lives in it answers to the manager
    /// team's Orchestrator.
    /// </summary>
    private static void MarkDissolved(RosterContext c, string dir, string manager, string child)
    {
        if (c.Services.Roster.ReadTeamRecord(dir, child).IsNull)
        {
            return;
        }

        c.Services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            child,
            doc =>
            {
                if (doc.IsNull)
                {
                    return null;
                }

                var updated = doc.AsObject().Clone();
                var workstream = doc.Member("workstream") is { IsMap: true } w ? w.AsObject().Clone() : new JsonObject();
                workstream["state"] = "dissolved";
                updated["workstream"] = workstream;
                var rows = new JsonArray();
                foreach (var row in doc.Member("members") is { IsArray: true } list ? list.AsArray() : [])
                {
                    if (row.IsMap && HierarchyModule.LeadOf(row) is null)
                    {
                        var next = row.AsObject().Clone();
                        next["lead"] = new JsonObject { ["team"] = manager, ["name"] = Orchestrator };
                        rows.Add(next);
                    }
                    else
                    {
                        rows.Add(row);
                    }
                }

                updated["members"] = rows;
                return TeamFileOutcome.Write(updated);
            });
    }
}
