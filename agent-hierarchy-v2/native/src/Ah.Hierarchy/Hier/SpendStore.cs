using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// One row of the spend ledger (ah2-512, adr-37): a paid call a member counted. <paramref name="Micros"/> is the count in millionths of
/// <paramref name="Unit"/>, negative for a correction; <paramref name="Run"/> is the team's open run anchor id and <paramref name="Item"/> the work number, either null.
/// </summary>
public sealed record SpendRow(long Id, string? Team, string? Run, long? Item, string Unit, long Micros, string What, string By, string? Session, long TsMs);

/// <summary>
/// The spend ledger: append-only, summed on read, never a mutable counter (S1). Nothing here refuses or stops a call; it only counts.
/// </summary>
internal static partial class SpendRules
{
    /// <summary>The longest <c>--what</c>.</summary>
    internal const int MaxWhat = 120;

    private const long Scale = 1_000_000;

    /// <summary>The largest integer a JSON number holds exactly: no count may go past it in micro-units.</summary>
    private const long MaxExact = 9_007_199_254_740_991;

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitShape();

    /// <summary>The unit lowercased (S2: the rules key shape), or null when it does not fit.</summary>
    internal static string? UnitOf(string? raw) => raw?.ToLowerInvariant() is { } unit && UnitShape().IsMatch(unit) ? unit : null;

    /// <summary>
    /// The count in micro-units: a decimal in the invariant culture with at most 6 fractional digits, signed. Null when it does not parse, is zero,
    /// has more than 6 fractional digits or is too large to sum exactly.
    /// </summary>
    internal static long? MicrosOf(string? text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        // past the exact range the multiply could overflow decimal: refuse first
        if (Math.Abs(value) > MaxExact / (decimal)Scale)
        {
            return null;
        }

        var scaled = value * Scale;
        return scaled == 0 || scaled != decimal.Truncate(scaled) || Math.Abs(scaled) > MaxExact ? null : (long)scaled;
    }

    /// <summary>A micro-unit count as a plain decimal: <c>52</c>, <c>0.04</c>, <c>-2</c>.</summary>
    internal static string Format(long micros) => (micros / (decimal)Scale).ToString("0.######", CultureInfo.InvariantCulture);

    private const string Columns = "id, team, run, item, unit, micros, what, \"by\", session, ts_ms";

    /// <summary>Every row of <paramref name="dir"/>'s repo, oldest first.</summary>
    internal static IReadOnlyList<SpendRow> Rows(Func<string, object?[], IReadOnlyList<object?[]>> query, string dir) =>
        [.. query($"SELECT {Columns} FROM spend WHERE hier_dir = ?1 ORDER BY ts_ms, id", [Backlog.DirKey(dir)]).Select(RowOf)];

    private static SpendRow RowOf(object?[] r) =>
        new((long)r[0]!, r[1] as string, r[2] as string, r[3] as long?, (string)r[4]!, (long)r[5]!, (string)r[6]!, (string)r[7]!, r[8] as string, (long)r[9]!);
}

internal sealed partial class HierLib
{
    /// <summary>
    /// The <c>spend</c> entries of one team of the status document (ah2-512 slice 3), from the composition's reader; empty with none, and empty when
    /// it cannot be read: spend never fails the board.
    /// </summary>
    internal JsonArray SpendJson(string dir, string? team)
    {
        try
        {
            return _spend?.Invoke(dir, team) ?? [];
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// <c>spend.add</c>: appends one ledger row for <c>dir</c>'s repo and answers its id. The caller checked the unit and the count; this
    /// checks them again so no row with a bad unit or a zero count is stored.
    /// </summary>
    internal static OpResult SpendAddDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        static string? Text(JsonValue args, string key) => args.Member(key) is { IsText: true } v ? v.AsString() : null;
        var dir = Text(args, "dir") ?? throw new InvalidOperationException("args.dir is undefined");
        var unit = SpendRules.UnitOf(Text(args, "unit")) ?? throw new InvalidOperationException("args.unit is not a unit");
        var micros = args.Member("micros") is { IsNumber: true } m && m.AsNumber() != 0 && m.AsNumber() == Math.Floor(m.AsNumber())
            ? (long)m.AsNumber()
            : throw new InvalidOperationException("args.micros is not a non-zero integer");
        var what = Text(args, "what") is { Length: > 0 and <= SpendRules.MaxWhat } w ? w : throw new InvalidOperationException("args.what is empty or too long");
        var by = Text(args, "by") ?? throw new InvalidOperationException("args.by is undefined");
        long? item = args.Member("item") is { IsNumber: true } i ? (long)i.AsNumber() : null;
        tx.Execute(
            "INSERT INTO spend(hier_dir, team, run, item, unit, micros, what, \"by\", session, ts_ms) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
            Backlog.DirKey(dir), Text(args, "team"), Text(args, "run"), item, unit, micros, what, by, Text(args, "session"),
            args.Member("ts_ms") is { IsNumber: true } ts ? (long)ts.AsNumber() : throw new InvalidOperationException("args.ts_ms is undefined"));
        return new JsonObject { ["id"] = tx.Query("SELECT last_insert_rowid()") is [[long id]] ? id : 0 };
    });
}
