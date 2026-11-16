using System.Text;
using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>A db-mode message store holding the rows it is given, with no file behind any of them; a row's body is its <c>Bytes</c>.</summary>
internal sealed class RowMsgs : IMsgScope
{
    public List<MsgFileRow> Rows { get; } = [];

    public Ah.Hierarchy.MsgsRoute Route() => new(this, new UnreadableStore.DbMode());

    public IMsgScope Scope() => this;

    public void Dispose()
    {
    }

    public IReadOnlyList<MsgExchangePair> Exchanges(IReadOnlyList<string> poolDirs) => [];

    public IReadOnlyList<MsgFileRow> Messages(string poolDir, string? id = null, string? type = null) =>
        [.. Rows.Where(r => r.PoolDir == poolDir && !r.Archived && (id is null || r.Id == id) && (type is null || r.Type == type))];

    public IReadOnlyList<MsgFileRow> ArchivedMessages(string poolDir) => [.. Rows.Where(r => r.PoolDir == poolDir && r.Archived)];

    /// <summary>Whether <see cref="MessagesById"/> fails as an unreadable store does.</summary>
    public bool ByIdFails { get; set; }

    public IReadOnlyList<MsgFileRow> MessagesById(string id, string type) =>
        ByIdFails ? throw new InvalidOperationException("the state database cannot be read") : [.. Rows.Where(r => r.Id == id && r.Type == type)];

    public MsgFileRow? ByPath(string path) => Rows.FirstOrDefault(r => r.Path == path);

    public long Version() => Rows.Count;

    public IReadOnlyList<string> ChangedIds(string poolDir, long sinceVersion) => [];

    /// <summary>A sealed message row of <paramref name="text"/> named <paramref name="name"/> in <paramref name="pool"/>'s msgs directory.</summary>
    public string Add(string pool, string name, string text, bool archived = false)
    {
        var parts = name.Split("--");
        var path = pool + (archived ? "/msgs/archive/" : "/msgs/") + name;
        Rows.Add(new MsgFileRow(pool, name, parts[0], parts[3].Replace(".md", string.Empty, StringComparison.Ordinal), parts[1], parts[2], null, null, null, null, null, null, null, archived, archived ? "archived" : "sealed", Encoding.UTF8.GetBytes(text), path));
        return path;
    }
}
