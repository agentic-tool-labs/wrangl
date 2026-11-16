using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Launch;

/// <summary>
/// Whether a herdr pane is provably a member's own terminal, the tie a dismiss uses for a dead member's pane: under herdr core the
/// terminal carries <c>ah.spawn=&lt;spawn nonce&gt;</c>, or it is the one live terminal labelled <c>ah.name=&lt;member&gt;</c>. A pane id
/// alone is no tie (ids may be reused after a restart), and herdr TUI has no labels, so it never ties.
/// </summary>
internal static class PaneTie
{
    internal static bool IsMembers(IHerdr herdr, string pane, string? spawnNonce, string? name)
    {
        ArgumentNullException.ThrowIfNull(herdr);
        return herdr.Flavour == Ah.Core.Mux.HerdrFlavour.Core
            && ((spawnNonce is { Length: > 0 } && herdr.SpawnAddress(spawnNonce).Address == pane)
                || (name is { Length: > 0 } && NameHolder(herdr, name) == pane));
    }

    /// <summary>The one live terminal labelled <c>ah.name=<paramref name="name"/></c> under herdr core; null when none, several, unreadable, or TUI.</summary>
    internal static string? NameHolder(IHerdr herdr, string name)
    {
        ArgumentNullException.ThrowIfNull(herdr);
        if (herdr.Flavour != Ah.Core.Mux.HerdrFlavour.Core)
        {
            return null;
        }

        try
        {
            var got = herdr.InvokeAllowFailure(["agent", "get", name]);
            return got.Ok && JsonText.TryParse(Utf8Text.Encode(got.Stdout), out var doc)
                && doc.Member("result")?.Member("agent")?.Member("pane_id") is { IsText: true } pane
                    ? pane.AsString()
                    : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
