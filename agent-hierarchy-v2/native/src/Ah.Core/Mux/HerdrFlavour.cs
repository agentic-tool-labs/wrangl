using Ah.Core.Json;

namespace Ah.Core.Mux;

/// <summary>
/// Which herdr app a <c>"herdr"</c> transport talks to: <see cref="Tui"/> (the terminal app, <c>herdr</c>) or <see cref="Core"/>
/// (herdr desktop, <c>hc</c> / <c>herdr-app</c>). The transport string stays <c>"herdr"</c> for both.
/// </summary>
public static class HerdrFlavour
{
    public const string Tui = "tui";
    public const string Core = "core";

    /// <summary>The team-record field; absent, <see cref="OfTeam"/> (ahd) reads <see cref="Tui"/> and <see cref="Resolve"/> the env.</summary>
    public const string Field = "herdr_flavour";

    /// <summary><c>HERDR_ENV=1</c> → tui; else a non-empty <c>HC_TERMINAL_ID</c> → core; else null (not herdr).</summary>
    public static string? FromEnv(string? herdrEnv, string? terminalId) =>
        herdrEnv == "1" ? Tui : !string.IsNullOrEmpty(terminalId) ? Core : null;

    /// <summary>This session's own flavour from <paramref name="getVariable"/>: <see cref="FromEnv"/> over <c>HERDR_ENV</c> and <c>HC_TERMINAL_ID</c>.</summary>
    public static string? SessionFlavour(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        return FromEnv(getVariable("HERDR_ENV"), getVariable("HC_TERMINAL_ID"));
    }

    /// <summary>
    /// This session's own pane id from <paramref name="getVariable"/>: the terminal address <c>HC_TERMINAL_ID</c> under core, else
    /// <c>HERDR_PANE_ID</c>; null when the one that applies is unset or empty.
    /// </summary>
    public static string? SessionPaneId(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var name = FromEnv(getVariable("HERDR_ENV"), getVariable("HC_TERMINAL_ID")) == Core ? "HC_TERMINAL_ID" : "HERDR_PANE_ID";
        return getVariable(name) is { Length: > 0 } value ? value : null;
    }

    /// <summary>The flavour a team record stores: <see cref="Core"/> only when it says so; a record without the field is <see cref="Tui"/>.</summary>
    public static string OfTeam(JsonValue? team) =>
        team is { IsMap: true } t && t.Member(Field) is { IsText: true } f && f.AsString() == Core ? Core : Tui;

    /// <summary>The flavour a team record names (any case), or null when it names none: unlike <see cref="OfTeam"/>, an untagged record is unknown, not <see cref="Tui"/>.</summary>
    public static string? RecordedOfTeam(JsonValue? team) => team is { IsMap: true } t ? Named(t.Member(Field)) : null;

    /// <summary>
    /// The flavour of the server an existing agent lives on: its registration's <see cref="Field"/> when present, else its member
    /// row's (<paramref name="member"/>, stamped at spawn), else the flavour its team records (<see cref="RecordedOfTeam"/>), else
    /// <paramref name="env"/>. ah2-145: a team record with no flavour says nothing, so it falls to the env.
    /// </summary>
    public static string? Resolve(JsonObject? registration, JsonValue? team, string? env, JsonValue? member = null) =>
        registration?.Get(Field) is { IsText: true } f && f.AsString() is Tui or Core ? f.AsString()
        : OfMember(member) ?? RecordedOfTeam(team) ?? env;

    /// <summary>The flavour a member row was stamped with at spawn (any case), or null when it carries none.</summary>
    public static string? OfMember(JsonValue? member) => member is { IsMap: true } m ? Named(m.Member(Field)) : null;

    private static string? Named(JsonValue? field) =>
        field is not { IsText: true } f ? null
        : string.Equals(f.AsString(), Tui, StringComparison.OrdinalIgnoreCase) ? Tui
        : string.Equals(f.AsString(), Core, StringComparison.OrdinalIgnoreCase) ? Core
        : null;

    /// <summary>
    /// Whether <paramref name="member"/> lives on herdr: <paramref name="transport"/> (the row's, else its team's) is <c>"herdr"</c>, or the row
    /// was stamped with a flavour at spawn (<see cref="OfMember"/>). An implicit team is written before any transport is known and keeps none.
    /// </summary>
    public static bool OnHerdr(JsonValue? member, string? transport) => transport == "herdr" || OfMember(member) is not null;
}
