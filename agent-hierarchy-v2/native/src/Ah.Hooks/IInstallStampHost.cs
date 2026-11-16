namespace Ah.Hooks;

/// <summary>
/// What the install-stamp notice does to the machine, behind one seam so a test never rebuilds anything: rewriting
/// <c>install.json</c> and starting the background rebuild.
/// </summary>
public interface IInstallStampHost
{
    /// <summary>Replaces the file at <paramref name="path"/> with <paramref name="text"/> through a temporary file and a rename.</summary>
    void WriteStamp(string path, string text);

    /// <summary>
    /// Starts <c>sh &lt;script&gt; --apply --rebuild-only --quiet</c> detached in <paramref name="home"/>, in its own session, with its
    /// output appended to <c>&lt;home&gt;/logs/rebuild.log</c>, and does not wait for it.
    /// </summary>
    void StartRebuild(string script, string home, IReadOnlyDictionary<string, string?> environment);
}
