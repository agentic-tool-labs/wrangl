namespace Ah.Watch;

/// <summary>Answers for the member an overdue dispatch is for, kept for one evaluation of a session: the answer depends on the member's name and team, not on the dispatch.</summary>
internal static class OverdueTargetMemo
{
    /// <summary>The team named by <paramref name="candidate"/>'s address, else the team it was written in.</summary>
    internal static (string? Name, string? Team) KeyOf(DispatchCandidate candidate) =>
        (DispatchClock.NameOf(candidate.ToName, candidate.ToAddr),
            candidate.ToAddr is { } to && to.Contains('@', StringComparison.Ordinal) ? to[(to.IndexOf('@', StringComparison.Ordinal) + 1)..] : candidate.Team);

    /// <summary><paramref name="target"/> asked once per member and team.</summary>
    internal static Func<DispatchCandidate, OverdueTarget?> Wrap(Func<DispatchCandidate, OverdueTarget?> target)
    {
        var answers = new Dictionary<(string? Name, string? Team), OverdueTarget?>();
        return candidate =>
        {
            var key = KeyOf(candidate);
            if (!answers.TryGetValue(key, out var answer))
            {
                answer = target(candidate);
                answers[key] = answer;
            }

            return answer;
        };
    }
}
