using System.Globalization;

namespace Ah.Watch.Model;

/// <summary>
/// The pager of <c>list:questions:&lt;dirId&gt;/&lt;team&gt;</c>: the questions and decisions of the team's open run as the held
/// <see cref="QuestionsLane"/> read them, newest first. The cursor is <c>(at ms, line number)</c>, both compared as numbers.
/// </summary>
internal sealed class QuestionsPager(Func<long> publish, Func<string, IReadOnlyList<KeyedRow>?> held) : IListPager
{
    public const string ListPrefix = "list:questions:";

    public string Prefix => ListPrefix;

    public IReadOnlyCollection<string> Filters { get; } = [];

    public ValueTask<ListResult> PageAsync(ListRequest request, CancellationToken cancellation) =>
        ValueTask.FromResult(held(request.SliceId[ListPrefix.Length..]) is { } rows
            ? KeysetRows.Page(request, publish(), 2, rows, Compare)
            : new ListResult.NotFound());

    private static int Compare(string[] a, string[] b)
    {
        var byAt = Number(a[0]).CompareTo(Number(b[0]));
        return byAt != 0 ? byAt : Number(a[1]).CompareTo(Number(b[1]));
    }

    private static long Number(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

/// <summary>
/// <c>list:qa-thread:&lt;dirId&gt;/&lt;thread id&gt;</c>: one question-log thread whole, <c>{prompt, replies, ask}</c>, read on demand when its
/// panel opens; the questions list carries only the reply heads. Unknown dir or thread is not found.
/// </summary>
internal sealed class QaThreadPager(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs) : IListPager
{
    public const string ListPrefix = "list:qa-thread:";

    public string Prefix => ListPrefix;

    public IReadOnlyCollection<string> Filters { get; } = [];

    public ValueTask<ListResult> PageAsync(ListRequest request, CancellationToken cancellation)
    {
        var parts = request.SliceId[ListPrefix.Length..].Split('/');
        var dir = parts.Length == 2 && parts[1].Length > 0 ? WatchModule.DirOfId(parts[0], canonicalDirs()) : null;
        var thread = dir is null ? Ah.Core.Json.JsonValue.Null : reader.Read("decisions", "qa-thread", dir, new Ah.Core.Json.JsonObject { ["dir"] = dir, ["id"] = parts[1] });
        return ValueTask.FromResult<ListResult>(thread.IsMap ? new ListResult.Document(thread) : new ListResult.NotFound());
    }
}
