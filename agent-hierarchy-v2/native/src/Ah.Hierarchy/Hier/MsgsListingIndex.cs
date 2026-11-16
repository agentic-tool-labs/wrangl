using System.Runtime.CompilerServices;

namespace Ah.Hierarchy;

/// <summary>
/// The exchanges of one listing of a message pool: the first request name and the first response name of each id, in ordinal order of
/// the names. It is built once per listing instance and found again by that instance, so it lives exactly as long as the listing the
/// stat-validated listing cache keeps; a listing made afresh is a new instance and gets a new index.
/// </summary>
internal sealed class MsgsListingIndex
{
    private static readonly ConditionalWeakTable<IReadOnlyList<string>, MsgsListingIndex> Built = new();

    private readonly Dictionary<string, (string? Request, string? Response)> _byId = new(StringComparer.Ordinal);

    private MsgsListingIndex(IReadOnlyList<string> names)
    {
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            if (MessageText.ParseFilename(name) is not { } parts || (parts.Type != "request" && parts.Type != "response"))
            {
                continue;
            }

            _byId.TryGetValue(parts.Id, out var kept);
            _byId[parts.Id] = parts.Type == "request" ? (kept.Request ?? name, kept.Response) : (kept.Request, kept.Response ?? name);
        }
    }

    public static MsgsListingIndex For(IReadOnlyList<string> listing) => Built.GetValue(listing, static names => new MsgsListingIndex(names));

    public (string? Request, string? Response) Of(string id) => _byId.GetValueOrDefault(id);
}
