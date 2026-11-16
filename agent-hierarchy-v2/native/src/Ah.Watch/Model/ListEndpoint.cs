using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The request side of <c>GET /v1/list/&lt;slice-id&gt;</c>: it finds the pager by the id's prefix, checks the query and writes the answer.
/// A page is never published; the answer carries no state of the model.
/// </summary>
internal static class ListEndpoint
{
    public static async Task<(int Status, byte[] Body)> ServeAsync(ListPagerRegistry pagers, string sliceId, IReadOnlyDictionary<string, string> query, CancellationToken cancellation)
    {
        if (pagers.Find(sliceId) is not { } pager)
        {
            return Error(404, "not_found");
        }

        foreach (var key in query.Keys)
        {
            if (key is not ("after" or "before" or "limit") && !pager.Filters.Contains(key))
            {
                return (400, JsonText.ToUtf8(new JsonObject { ["error"] = "bad_filter", ["key"] = key }, 0));
            }
        }

        if (!ListRequest.TryParse(sliceId, query, out var request, out var invalid))
        {
            return Error(400, invalid.Reason);
        }

        switch (await pager.PageAsync(request, cancellation).ConfigureAwait(false))
        {
            case ListResult.Found found:
                return (200, JsonText.ToUtf8(found.Page.ToJson(), 0));
            case ListResult.Document document:
                return (200, JsonText.ToUtf8(document.Body, 0));
            case ListResult.Invalid { Key: { } key } bad:
                return (400, JsonText.ToUtf8(new JsonObject { ["error"] = bad.Reason, ["key"] = key }, 0));
            case ListResult.Invalid bad:
                return Error(400, bad.Reason);
            default:
                return Error(404, "not_found");
        }
    }

    private static (int Status, byte[] Body) Error(int status, string error) =>
        (status, JsonText.ToUtf8(new JsonObject { ["error"] = error }, 0));
}
