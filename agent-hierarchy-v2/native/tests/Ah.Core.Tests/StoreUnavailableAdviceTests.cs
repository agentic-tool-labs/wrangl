using Ah.Core.Json;

namespace Ah.Core.Tests;

/// <summary>Only a store that cannot be read is pointed at a restore; a busy one or one not imported never is.</summary>
public sealed class StoreUnavailableAdviceTests
{
    [Theory]
    [InlineData("the state database is busy", "try again in a moment")]
    [InlineData("the state database could not be read: database is locked", "try again in a moment")]
    [InlineData("the state database could not be read: file is not a database", "restore it with: ah db restore")]
    [InlineData("the state database could not be read: disk I/O error", null)]
    public void TheAdviceFollowsTheReason(string reason, string? advice) =>
        Assert.Equal(advice, new StoreUnavailableException(reason).Advice);
}
