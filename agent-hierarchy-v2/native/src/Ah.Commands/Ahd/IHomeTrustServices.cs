using Ah.Abstractions;

namespace Ah.Commands;

/// <summary>What <c>ahd home trust|untrust</c> runs on: the trust-store service that reads the two files, the writer that replaces one, and who is asking.</summary>
public interface IHomeTrustServices
{
    /// <summary>The one reader of <c>trusted-homes</c> and <c>trusted-hierarchy-dirs</c>; also names where the files live.</summary>
    ITrustStore Trust { get; }

    IFileReader Files { get; }

    IRawFileWriter Writer { get; }

    IAhEnvironment Environment { get; }

    /// <summary>The invoking user's id, or null when it cannot be known.</summary>
    uint? CurrentUserId { get; }

    /// <summary>Whether standard input and standard error are both terminals.</summary>
    bool IsTerminal();
}
