namespace Ah.Watch;

/// <summary>The path computations a view build needs that read no store.</summary>
public interface IViewLayout
{
    /// <summary>The ah home: the directory a read of a home-scoped domain is made against.</summary>
    string AhHome { get; }

    /// <summary>The mailbox directory of member <paramref name="member"/> of the team whose file is <paramref name="teamFile"/>.</summary>
    /// <param name="teamFile">The team file path.</param>
    /// <param name="member">The member name.</param>
    /// <returns>The box directory.</returns>
    string BoxDirFor(string teamFile, string member);
}
