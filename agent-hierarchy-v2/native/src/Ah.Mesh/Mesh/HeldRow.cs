using System.Globalization;
using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>When a member row that records a pid is held by another process, and what to do about it: one rule for mail and for <c>roster checkin</c>.</summary>
internal static class HeldRow
{
    /// <summary>Whether <paramref name="reg"/> is the process <paramref name="record"/> names: the same pid, and the same start when the record has one.</summary>
    public static bool SamePid(JsonObject record, JsonObject reg) =>
        record.Get("pid") is { IsNumber: true } rp && reg.Get("pid") is { IsNumber: true } gp && rp.AsNumber() == gp.AsNumber()
        && (record.Get("pid_start") is not { IsText: true } rs || reg.Get("pid_start") is { IsText: true } gs && rs.AsString() == gs.AsString());

    /// <summary>Whether the bare watch has marked the row <c>bare</c>: its Claude was started without ah's flags.</summary>
    public static bool MarkedBare(JsonObject row) =>
        row.Get("bare") is { IsMap: true } bare && bare.AsObject().Get("state") is { IsText: true } mark && mark.AsString() == "bare";

    /// <summary>The way out for a session refused a row held by <paramref name="row"/>'s process, which is <paramref name="running"/> or gone.</summary>
    public static string Remedy(JsonObject row, bool running, string team, string name)
    {
        var holder = row.Get("pid") is { IsNumber: true } hp ? ((long)hp.AsNumber()).ToString(CultureInfo.InvariantCulture) : "none";
        return running
            ? $"the holder (pid {holder}) is alive: close it, or the team owner runs `ah roster recover --team {team}`"
            : MarkedBare(row)
            ? $"the recorded process (pid {holder}) is gone and this member is marked bare, so `ah roster recover --team` leaves its row alone until ah relaunches it: the team owner runs `ah roster recover now --team {team} {name}`; with recovery off, only the user can switch it on (`ah roster recover enable`)"
            : $"the recorded process (pid {holder}) is gone, as after a reboot; this session's own claim does not re-stamp the row, so the team owner runs `ah roster recover --team {team}` (or, if no session is live in its pane: {string.Join(" — ", MemberHint.Lines(null, MemberHint.Respawn(row.Get("role") is { IsText: true } role ? role.AsString() : "<role>", row.Get("stream") is { IsText: true } stream ? stream.AsString() : null, team), row.Get("session_id") is { IsText: true } sid && sid.AsString().Length > 0 ? sid.AsString() : null))})";
    }
}
