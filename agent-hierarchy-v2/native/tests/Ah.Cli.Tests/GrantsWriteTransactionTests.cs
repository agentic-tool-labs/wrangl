using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>Per-dir-files-to-db S11e test 2: a grants write is one transaction holding its audit row and the document carrying that row's hash.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class GrantsWriteTransactionTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    /// <summary>A transaction whose document replace fails, after the audit row went in.</summary>
    private sealed class FailingPut(IDbOpTransaction inner) : IDbOpTransaction
    {
        public void Execute(string sql, params object?[] args)
        {
            if (sql.StartsWith("INSERT INTO worktree_grants(", StringComparison.Ordinal))
            {
                throw new IOException("injected fault after the audit insert");
            }

            inner.Execute(sql, args);
        }

        public IReadOnlyList<object?[]> Query(string sql, params object?[] args) => inner.Query(sql, args);
    }

    private (string Hier, IGrantSource Grants, string Audit, Func<Func<IDbOpTransaction, IDbOpTransaction>, DbOpOutcome> Gate) Rig()
    {
        Assert.Null(StateDbModule.EnsureDatabase(_sandbox.AhHome));
        var hier = Directory.CreateDirectory(Path.Combine(_sandbox.Root, "repo", ".claude", "ah2")).FullName;
        var audit = AhPaths.UserEntry(_sandbox.Home, AhPaths.WorktreeAuditFile);
        var grants = StateDbModule.CreateGrants(_sandbox.AhHome, audit);
        var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = _sandbox.Home, ["AH_HOME"] = _sandbox.AhHome });
        var domain = ConfigModule.CreateStateDomains(PlatformModule.CreateIo(null).Files, env, new FakeClock(DateTimeOffset.UtcNow), grants: grants).Single(d => d.Name == "worktree-grants");
        Assert.True(domain.DbOnly);
        var gate = domain.Ops.Single(o => o.Name == "gate");
        var call = new OpCall("gate", Encoding.UTF8.GetBytes(JsonText.Stringify(new JsonObject { ["dir"] = hier, ["team"] = "t1", ["mode"] = "enforce" }, 0)), new RequestContext(hier, hier, hier, hier));
        var runner = StateDbModule.CreateOpRunner(_sandbox.AhHome);
        return (hier, grants, audit, wrap => runner.Run(new DbOpRequest("worktree-grants", null, hier, tx => gate.DbBody!(call, wrap(tx)), DbOnly: true)));
    }

    [Fact]
    public void AWriteRefusesAGrantsFileAnOlderBuildChangedAfterTheTake()
    {
        var (hier, grants, _, gate) = Rig();
        Assert.Equal(DbOpStatus.Done, gate(tx => tx).Status);
        Assert.True(grants.Read(hier)!.TryDocuments(out var docs));
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(hier, "grants")).FullName, "t1.json");

        // the negative: a newer file with the row's own bytes (a restore) lets the write go ahead
        File.WriteAllText(file, Assert.Single(docs).Body);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(DbOpStatus.Done, gate(tx => tx).Status);

        // an older build rewrites the file (a revoke) after the row: the write is refused and the row stays as it was
        File.WriteAllText(file, "{\"mode\":\"off\"}");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(2));
        var refused = gate(tx => tx);
        Assert.NotEqual(DbOpStatus.Done, refused.Status);
        Assert.Contains("the grants changed outside this build", refused.Error, StringComparison.Ordinal);
        Assert.Equal(2, grants.AuditRows().Count);
    }

    [Fact]
    public void AGateWriteKeepsBothRowsOrNeitherAndARowEditedOutOfBandIsCaught()
    {
        var (hier, grants, audit, rigGate) = Rig();
        DbOpOutcome Gate(Func<IDbOpTransaction, IDbOpTransaction> wrap) => rigGate(wrap);

        // a fault after the audit insert leaves neither row
        Assert.NotEqual(DbOpStatus.Done, Gate(tx => new FailingPut(tx)).Status);
        Assert.Empty(grants.AuditRows());
        Assert.True(grants.Read(hier)!.TryDocuments(out var none));
        Assert.Empty(none);

        Assert.Equal(DbOpStatus.Done, Gate(tx => tx).Status);
        var row = Assert.Single(grants.AuditRows());
        Assert.True(grants.Read(hier)!.TryDocuments(out var docs));
        var doc = Assert.Single(docs);
        Assert.Equal(row, doc.AuditRow);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(row))), JsonText.Parse(doc.Body).Member("audit_hash")!.Value.AsString());
        Assert.False(File.Exists(Path.Combine(hier, "grants", "t1.json")));
        Assert.False(File.Exists(audit));

        // an out-of-band edit of the row: the read carries no audit row (inconsistent), and the next write refuses to replace it
        Assert.True(StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("UPDATE worktree_grants SET body = replace(body, 'enforce', 'off')");
            return 0;
        }).Ok);

        Assert.True(grants.Read(hier)!.TryDocuments(out var edited));
        Assert.Null(Assert.Single(edited).AuditRow);
        Assert.NotEqual(DbOpStatus.Done, Gate(tx => tx).Status);
    }
}
