using Ah.Abstractions;
using Ah.Cli.Composition;

namespace Ah.Cli.Tests;

/// <summary>Who may pass <c>ahd heal --force</c> (ah2-22): the user's principal only, and nobody when the principal cannot be read.</summary>
public sealed class HealCallerTests
{
    [Fact]
    public void TheCallerIsTheUserOnlyWhenThePrincipalSaysSoAndNeverWhenItCannotBeRead()
    {
        Assert.True(DaemonComposition.CallerIsUser(static () => PrincipalKind.User));
        Assert.False(DaemonComposition.CallerIsUser(static () => PrincipalKind.Session));
        Assert.False(DaemonComposition.CallerIsUser(static () => PrincipalKind.Anonymous));

        // negative: the registrations cannot be read (a hung daemon holds the database), or nothing is composed. No fallback to the
        // process table: an ancestry that cannot be read must not make an agent the user.
        Assert.Null(DaemonComposition.CallerIsUser(static () => throw new IOException("database is locked")));
        Assert.Null(DaemonComposition.CallerIsUser(static () => null));
    }
}
