using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>The home documents (b397 group 2: <c>meta</c> rows), planted and read the way the session-roles, ultra-gate and permissions code reads them.</summary>
public static class TestHomeDocs
{
    /// <summary>The <c>meta</c> key of the session roles document.</summary>
    public const string SessionRoles = "home.session-roles";

    /// <summary>The <c>meta</c> key of the permissions ledger.</summary>
    public const string Permissions = "home.permissions";

    /// <summary>Stores <paramref name="body"/> under <paramref name="key"/> in <paramref name="ahHome"/>'s database, creating the database first.</summary>
    public static void Put(string ahHome, string key, string body)
    {
        using (StateDbModule.Open(ahHome, mayCreate: true))
        {
        }

        StateDbModule.CreateHomeDocs(ahHome).Write(key, body);
    }

    /// <summary>Appends each of <paramref name="lines"/> as a <c>usage_rows</c> row, verbatim, creating the database first.</summary>
    public static void PutUsage(string ahHome, params string[] lines)
    {
        using (StateDbModule.Open(ahHome, mayCreate: true))
        {
        }

        var outcome = StateDbModule.CreateOpRunner(ahHome).Run(new Ah.Abstractions.DbOpRequest("usage", null, ahHome, tx =>
        {
            foreach (var line in lines)
            {
                tx.Execute("INSERT INTO usage_rows(body) VALUES (?1)", line);
            }

            return new Ah.Abstractions.OpResult(true, "{}"u8.ToArray());
        }, DbOnly: true));
        if (outcome.Status != Ah.Abstractions.DbOpStatus.Done)
        {
            throw new InvalidOperationException($"usage put: {outcome.Error}");
        }
    }

    /// <summary>The text under <paramref name="key"/>, or null.</summary>
    public static string? Text(string ahHome, string key) => StateDbModule.CreateHomeDocs(ahHome).Read(key);
}
