using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>
/// Whether the entry a <c>relay-needed</c> event asks to have relayed still needs it. The one implementation behind every surface
/// that presents the obligation: the plugin prompt, the compact prompt, the full-text quote and the Stop hook.
/// </summary>
internal static class RelayVoid
{
    /// <summary>
    /// Why entry <paramref name="entry"/> of the box <paramref name="dir"/> needs no relay, as a marker (<c>kind</c> and the fields that
    /// kind names), or null when it is still owed. An entry whose ladder ended in <c>superseded</c>, <c>declined</c> or <c>withdrawn</c> is void whatever
    /// its ack; then an ack; then a <c>delivered</c> row; then a claim released to the ladder (its later rungs deliver it, and the mail-claim gate
    /// refuses the relay); then a claim that is no longer pending (a relay already holds it).
    /// </summary>
    internal static JsonObject? Check(IMailbox mailbox, string dir, string entry)
    {
        var rows = mailbox.LadderRowsOf(dir, entry);
        var last = rows.Count > 0 ? rows[^1] : null;
        var state = last is not null && last.Get("state") is { IsText: true } s ? s.AsString() : null;
        if (state == "superseded")
        {
            return new JsonObject { ["kind"] = "superseded", ["by"] = Text(last!, "by") };
        }

        if (state == "withdrawn")
        {
            return new JsonObject { ["kind"] = "withdrawn", ["by"] = Text(last!, "by"), ["reason"] = Text(last!, "reason") };
        }

        if (state == "declined")
        {
            return new JsonObject { ["kind"] = "declined", ["by"] = Text(last!, "by"), ["reason"] = Text(last!, "reason") };
        }

        if (mailbox.ReadAck(dir, entry) is { IsMap: true } ack)
        {
            var o = ack.AsObject();
            return new JsonObject { ["kind"] = "acked", ["at"] = Text(o, "at"), ["by"] = Text(o, "by") };
        }

        if (state == "delivered")
        {
            return new JsonObject { ["kind"] = "delivered" };
        }

        var record = mailbox.ReadClaim(dir, entry);
        if (ClaimMachine.StateOf(record) == ClaimStates.Released)
        {
            return new JsonObject { ["kind"] = "released" };
        }

        if (ClaimMachine.Live(record) is { } claim && claim.Get("state") is { IsText: true } claimState && claimState.AsString() != ClaimStates.Pending)
        {
            return new JsonObject { ["kind"] = "claimed", ["state"] = claimState.AsString() };
        }

        return null;
    }

    /// <summary>
    /// <paramref name="entry"/> as it is when it is not a trusted <c>relay-needed</c> event whose entry is void; else a copy that carries
    /// the marker under <c>void</c>, so the prompt builder can print the "no longer needed" line without reading anything.
    /// </summary>
    internal static JsonValue Mark(IMailbox mailbox, JsonValue entry)
    {
        if (!entry.IsMap || RelayOf(entry) is not { } relay)
        {
            return entry;
        }

        var marker = Check(mailbox, relay.Dir, relay.Entry);
        if (marker is null)
        {
            return entry;
        }

        var copy = new JsonObject();
        foreach (var key in entry.AsObject().Keys)
        {
            copy[key] = entry.AsObject()[key];
        }

        copy["void"] = marker;
        return JsonValue.FromObject(copy);
    }

    /// <summary>The marker as a short note for a peer record: <c>acked &lt;at&gt; &lt;by&gt;</c>, <c>superseded &lt;N&gt;</c>, <c>declined &lt;reason&gt;</c>, <c>delivered</c>, <c>released</c> or <c>claimed &lt;state&gt;</c>.</summary>
    internal static string Note(JsonObject marker) => Kind(marker) switch
    {
        "acked" => $"acked {Field(marker, "at")} {Field(marker, "by")}",
        "superseded" => $"superseded {Field(marker, "by")}",
        "withdrawn" => $"withdrawn {Field(marker, "by")}",
        "declined" => $"declined {Field(marker, "reason")}",
        "claimed" => $"claimed {Field(marker, "state")}",
        "released" => "released",
        _ => "delivered",
    };

    internal static string Kind(JsonObject marker) => Field(marker, "kind");

    internal static string Field(JsonObject marker, string key) => marker.Get(key) is { IsText: true } t ? t.AsString() : string.Empty;

    private static JsonValue Text(JsonObject row, string key) => row.Get(key) is { IsText: true } t ? t : JsonValue.FromString(string.Empty);

    /// <summary>The recipient box and entry a <c>relay-needed</c> event from <c>ahd</c> names; null for anything else.</summary>
    internal static (string Dir, string Entry)? RelayOf(JsonValue e)
    {
        if (e.Member("from") is not { IsText: true } from || from.AsString() != "ahd"
            || e.Member("event") is not { IsText: true } name || name.AsString() != "relay-needed"
            || e.Member("data") is not { IsMap: true } data
            || data.Member("relay") is not { IsMap: true } relay
            || relay.Member("dir") is not { IsText: true } dir || dir.AsString().Length == 0
            || relay.Member("entry") is not { IsText: true } entry || entry.AsString().Length == 0)
        {
            return null;
        }

        return (dir.AsString(), entry.AsString());
    }
}
