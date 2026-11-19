using Ah.Core.Json;

namespace Ah.Config;

/// <summary>
/// The durable testing orders: the standing per-repo test policy every member gets in its directive. The <c>testing</c> block of
/// the roster level files maps a role (or <c>e4</c>, the UI perf harness) to free text shown verbatim; its leading word before
/// <c>:</c> is the mode a gate can check. Layers merge per key, the more specific level winning, over the defaults.
/// </summary>
public interface ITestingOrders
{
    /// <summary>The top-level key of a level file.</summary>
    public const string Key = "testing";

    /// <summary>The modes a value's leading word may name.</summary>
    private static readonly string[] Modes = ["touched", "project", "full", "none"];

    private static readonly (string Key, string Text)[] Defaults =
    [
        ("implementor", "touched: only the test classes or files for the code you changed; never a whole project or the full set"),
        ("ux-engineer", "touched: only the test classes or files for the code you changed, plus your own visual and contrast checks; E4 only when the brief asks"),
        ("reviewer", "none: delegate any run you need, unless a rule of engagement says reviewers run nothing; then ask for no run"),
        ("qa", "full: the full set once per ship batch"),
        ("e4", "background: run the E4 perf harness in the background"),
    ];

    /// <summary>
    /// The orders of <paramref name="resolved"/> (a <c>ResolveConfig</c> result, whose <c>testing</c> block is already merged across the
    /// level files), key to text, over the defaults; null or no block gives the defaults.
    /// </summary>
    public static JsonObject Resolve(JsonObject? resolved)
    {
        var orders = new JsonObject();
        foreach (var (key, text) in Defaults)
        {
            orders[key] = text;
        }

        if (resolved?.Get(Key) is { IsMap: true } block)
        {
            var entries = block.AsObject();
            foreach (var key in entries.Keys)
            {
                if (entries.Get(key) is { IsText: true } value && value.AsString().Trim().Length > 0)
                {
                    orders[key] = value.AsString().Trim();
                }
            }
        }

        return orders;
    }

    /// <summary>
    /// The order key that applies to <paramref name="role"/>: its own key, else for a custom role its class's built-in role (implement →
    /// implementor, review → reviewer); null when none applies.
    /// </summary>
    internal static string? KeyFor(JsonObject orders, string role, bool builtin, string? roleClass) =>
        orders.ContainsKey(role) ? role
        : builtin ? null
        : roleClass switch
        {
            "implement" => "implementor",
            "review" => "reviewer",
            _ => null,
        };

    /// <summary>The mode <paramref name="text"/> names by its leading word before <c>:</c>, or null when it names none.</summary>
    public static string? Mode(string text)
    {
        var head = text.Split(':', 2)[0].Trim();
        return Modes.Contains(head) ? head : null;
    }
}
