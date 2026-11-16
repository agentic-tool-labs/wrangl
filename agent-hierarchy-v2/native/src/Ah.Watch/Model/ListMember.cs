namespace Ah.Watch.Model;

/// <summary>
/// The <c>member=</c> filter of a list: the member's full slice id, <c>member:&lt;dirId&gt;/&lt;team&gt;/&lt;member&gt;</c>. It is refused as
/// <c>bad_member</c> unless it has that form and names the list's own directory (and, for a team's list, the list's own team).
/// </summary>
internal static class ListMember
{
    public const string BadMember = "bad_member";

    /// <summary>
    /// Reads the parameter <paramref name="name"/> (<c>member</c>, or <c>with</c>) of <paramref name="request"/>: null in <paramref name="member"/> when the parameter is absent, otherwise the parts of
    /// the id. Returns the refusal, or null when the parameter is absent or well formed. <paramref name="team"/> is the list's team, or null for a
    /// directory's list, which accepts a member of any of its teams.
    /// </summary>
    public static ListResult.Invalid? Parse(ListRequest request, string dirId, string? team, out (string DirId, string Team, string Member)? member, string name = "member")
    {
        member = null;
        if (request.Param(name) is not { } id)
        {
            return null;
        }

        if (!id.StartsWith("member:", StringComparison.Ordinal) || TreeIds.OfMember(id) is not { } parts || parts.DirId != dirId || (team is not null && parts.Team != team))
        {
            return new ListResult.Invalid(BadMember);
        }

        member = parts;
        return null;
    }
}
