using System.Runtime.Versioning;
using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>A member's pending resume entries (<c>rewake-</c> ids) are withdrawn by session, by age, and never past an ack.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class WithdrawRewakesTests
{
    [Fact]
    public void OnlyTheUnackedRewakesOfTheSessionAreWithdrawnAndNegativeNotThePlainTheKeptTheOtherSessionsOrTheAckedOnes()
    {
        var rig = MailClaimTests.Create();
        void Send(string id, string session)
        {
            var outcome = rig.Fx.Mailbox.Send(new JsonObject
            {
                ["to"] = "b@tm", ["from"] = "a@tm", ["id"] = id, ["text"] = "resume " + id, ["to_session"] = session,
                ["cwd"] = rig.Hierarchy, ["hierarchy_dir"] = rig.Hierarchy,
            });
            Assert.True(outcome.Ok, outcome.Failure?.Message);
        }

        Send("rewake-a", "s1");
        Send("rewake-b", "s1");
        Send("rewake-c", "s2");
        Send("plain-1", "s1");
        Send("rewake-acked", "s1");
        _ = rig.Fx.Mailbox.AckIds(rig.BoxB, ["rewake-acked"], "test");

        Assert.Equal(1, rig.Fx.Mailbox.WithdrawRewakes(rig.BoxB, "s1", "rewake-b", null, "superseded"));
        Assert.Equal(0, rig.Fx.Mailbox.WithdrawRewakes(rig.BoxB, "s1", "rewake-b", null, "superseded"));
        Assert.Equal(0, rig.Fx.Mailbox.WithdrawRewakes(rig.BoxB, null, null, 0, "expired"));
        Assert.Equal(2, rig.Fx.Mailbox.WithdrawRewakes(rig.BoxB, null, null, double.MaxValue, "expired"));
    }
}
