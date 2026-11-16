namespace Ah.Hooks.Surface;

/// <summary>The glob match the compound allow hook reads paths and command words with.</summary>
internal static class SurfaceMatcher
{
    /// <summary>Glob match: <c>*</c> within one path segment, <c>**</c> across segments, <c>**/</c> for any directories (or none).</summary>
    internal static bool GlobMatch(string glob, int gi, string text, int ti)
    {
        while (gi < glob.Length)
        {
            if (glob[gi] != '*')
            {
                if (ti >= text.Length || glob[gi] != text[ti])
                {
                    return false;
                }

                gi++;
                ti++;
                continue;
            }

            if (gi + 1 < glob.Length && glob[gi + 1] == '*')
            {
                var dirs = gi + 2 < glob.Length && glob[gi + 2] == '/';
                var next = gi + (dirs ? 3 : 2);
                for (var k = ti; k <= text.Length; k++)
                {
                    if ((!dirs || k == ti || text[k - 1] == '/') && GlobMatch(glob, next, text, k))
                    {
                        return true;
                    }
                }

                return false;
            }

            for (var k = ti; k <= text.Length; k++)
            {
                if (GlobMatch(glob, gi + 1, text, k))
                {
                    return true;
                }

                if (k < text.Length && text[k] == '/')
                {
                    break;
                }
            }

            return false;
        }

        return ti == text.Length;
    }
}
