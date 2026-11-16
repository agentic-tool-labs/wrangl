namespace Ah.Codex;

/// <summary>The POSIX parts of Node's <c>path</c> module that the launch rules depend on, so a path is judged as Node judges it.</summary>
internal static class NodePath
{
    public static bool IsAbsolute(string path) => path.StartsWith('/');

    /// <summary><c>path.normalize</c>.</summary>
    public static string Normalize(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        var absolute = IsAbsolute(path);
        var trailing = path.EndsWith('/');
        var stack = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..")
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (!absolute)
                {
                    stack.Add("..");
                }

                continue;
            }

            stack.Add(segment);
        }

        var joined = string.Join('/', stack);
        if (joined.Length == 0)
        {
            return absolute ? "/" : (trailing ? "./" : ".");
        }

        if (trailing)
        {
            joined += "/";
        }

        return absolute ? "/" + joined : joined;
    }

    /// <summary><c>path.join</c>: the non-empty parts joined with a slash and normalized.</summary>
    public static string Join(params string[] parts)
    {
        var joined = string.Join('/', parts.Where(p => p.Length > 0));
        return joined.Length == 0 ? "." : Normalize(joined);
    }

    /// <summary><c>path.resolve</c> for one argument: an absolute path normalized without a trailing slash, a relative one under the working directory.</summary>
    public static string Resolve(string path)
    {
        var full = IsAbsolute(path) ? path : Directory.GetCurrentDirectory() + "/" + path;
        var normal = Normalize(full);
        return normal.Length > 1 && normal.EndsWith('/') ? normal[..^1] : normal;
    }

    /// <summary><c>path.relative(from, to)</c> for two paths that <see cref="Resolve"/> accepts.</summary>
    public static string Relative(string from, string to)
    {
        var a = Resolve(from);
        var b = Resolve(to);
        if (a == b)
        {
            return string.Empty;
        }

        var fromParts = a == "/" ? [] : a.TrimStart('/').Split('/');
        var toParts = b == "/" ? [] : b.TrimStart('/').Split('/');
        var common = 0;
        while (common < fromParts.Length && common < toParts.Length && fromParts[common] == toParts[common])
        {
            common++;
        }

        var result = new List<string>();
        for (var i = common; i < fromParts.Length; i++)
        {
            result.Add("..");
        }

        result.AddRange(toParts.Skip(common));
        return string.Join('/', result);
    }
}
