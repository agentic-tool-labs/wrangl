using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>
/// The reads only the orchestrator detail makes, beside <see cref="IViewSource"/>. A source that does not implement it serves the
/// groups that need it as unavailable.
/// </summary>
internal interface IDetailReads
{
    /// <summary>The git runner, or null when none is composed.</summary>
    GitRunner? Git { get; }

    /// <summary>The statusline tap reader, or null when none is composed.</summary>
    StatuslineTap? Tap { get; }

    /// <summary>The text of a small regular file, or null when the path is not one or cannot be read.</summary>
    string? ReadText(string path);

    /// <summary>The git work trees of a hierarchy directory's pools for the named teams, main checkout first.</summary>
    IReadOnlyList<string> Trees(string dir, IReadOnlyList<string> teams);

    /// <summary>The open pipeline run of a team: <c>{run_id, started_at, rows}</c>, or null.</summary>
    JsonObject? PipelineRun(string dir, string? team);

    /// <summary>The questions parked for the user in a hierarchy directory, open or not; empty when the source reads none.</summary>
    IReadOnlyList<Ah.Hierarchy.ParkedQuestion> ParkedQuestions(string dir) => [];

    /// <summary>The <c>flight</c> sub-keys this build cannot supply.</summary>
    IReadOnlyList<string> Unavailable => [];

    /// <summary>The progress records reader the plan document reads through, or null when none is composed.</summary>
    IViewProgress? Progress => null;
}
