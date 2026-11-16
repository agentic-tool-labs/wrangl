using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Platform;

/// <summary>
/// The one test-mode predicate: <c>AH_TEST</c> is exactly <c>1</c> and the real path of <c>AH_HOME</c> has an ancestor directory
/// named <c>ahprobe.&lt;alphanumerics&gt;</c> directly inside <c>/tmp</c> or <c>/private/tmp</c>, owned by the invoking user and
/// not writable by group or other. A symlink never satisfies it: the check is on real paths.
/// </summary>
internal static partial class TestModePredicate
{
    private const uint GroupOrOtherWrite = 0b000_010_010;

    [GeneratedRegex("^ahprobe\\.[A-Za-z0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProbeName();

    internal static bool Holds(string? ahTest, string? ahHome, IFileReader files, uint uid)
    {
        if (ahTest != "1")
        {
            return false;
        }

        var configured = ahHome is null ? string.Empty : JsText.Trim(ahHome);
        if (configured.Length == 0 || configured[0] != '/')
        {
            return false;
        }

        var resolved = ResolveThroughLongestExistingPrefix(files, configured);
        if (resolved is null)
        {
            return false;
        }

        for (var d = resolved; d.Length > 1; d = Dirname(d))
        {
            if (Dirname(d) is not ("/tmp" or "/private/tmp") || !ProbeName().IsMatch(Basename(d)))
            {
                continue;
            }

            return files.Stat(d) is { Kind: FileKind.Directory } stat && stat.Uid == uid && (stat.Mode & GroupOrOtherWrite) == 0;
        }

        return false;
    }

    internal static string? ResolveThroughLongestExistingPrefix(IFileReader files, string path) =>
        RealPathResolver.ThroughLongestExistingPrefix(files, path);

    private static string Dirname(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static string Basename(string path) => path[(path.LastIndexOf('/') + 1)..];
}
