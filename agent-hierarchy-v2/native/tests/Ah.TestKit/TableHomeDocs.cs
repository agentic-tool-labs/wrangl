using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.TestKit;

/// <summary>
/// The home documents (b397 group 2: <c>meta</c> rows) as the unit tables pin them: each key's row is the file the table names, at its
/// old path and in its old format (two-space JSON, trailing newline), so a case's <c>files</c> plant the row and <c>expectFiles</c> check it.
/// Test-only: the product reads and writes only the database. As a transaction it serves the one read and the one upsert the domains run.
/// </summary>
public sealed class TableHomeDocs(IAhEnvironment env) : IHomeDocs, IDbOpTransaction
{
    private string PathOf(string key) => Ah.Core.Text.AhPaths.UserEntry(env.HomeDirectory, key switch
    {
        "home.ultra-gate" => "agent-hierarchy.gate.json",
        "home.session-roles" => "agent-hierarchy.session-roles.json",
        _ => throw new InvalidOperationException($"no table file for {key}"),
    });

    public string? Read(string key)
    {
        try
        {
            return File.ReadAllText(PathOf(key));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool TryRead(string key, out string? body)
    {
        body = Read(key);
        return true;
    }

    public void Write(string key, string? body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(key))!);
        File.WriteAllText(PathOf(key), JsonText.Stringify(JsonText.Parse(body!), 2) + "\n");
    }

    public void Execute(string sql, params object?[] args) => Write((string)args[0]!, (string)args[1]!);

    public IReadOnlyList<object?[]> Query(string sql, params object?[] args) => Read((string)args[0]!) is { } text ? [[text]] : [];
}
