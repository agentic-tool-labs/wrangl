using System.Text;

namespace Ah.Core.Text;

/// <summary>Node's <c>node:path</c> POSIX functions, on strings, with no file system access. <c>resolve</c> takes the current directory explicitly.</summary>
public static class NodePath
{
    public static bool IsAbsolute(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Length > 0 && path[0] == '/';
    }

    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return ".";
        }

        var isAbsolute = path[0] == '/';
        var trailing = path[^1] == '/';
        var normalized = NormalizeSegments(path, !isAbsolute);
        if (normalized.Length == 0)
        {
            if (isAbsolute)
            {
                return "/";
            }

            return trailing ? "./" : ".";
        }

        if (trailing)
        {
            normalized += "/";
        }

        return isAbsolute ? "/" + normalized : normalized;
    }

    public static string Join(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var joined = new StringBuilder();
        foreach (var part in parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            if (part.Length == 0)
            {
                continue;
            }

            if (joined.Length > 0)
            {
                joined.Append('/');
            }

            joined.Append(part);
        }

        return joined.Length == 0 ? "." : Normalize(joined.ToString());
    }

    /// <summary><c>path.resolve(...parts)</c> with <paramref name="cwd"/> standing in for <c>process.cwd()</c>.</summary>
    public static string Resolve(string cwd, params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(cwd);
        ArgumentNullException.ThrowIfNull(parts);
        var resolved = string.Empty;
        var absolute = false;
        for (var i = parts.Length - 1; i >= -1 && !absolute; i--)
        {
            var part = i >= 0 ? parts[i] : cwd;
            if (part.Length == 0)
            {
                continue;
            }

            resolved = part + "/" + resolved;
            absolute = part[0] == '/';
        }

        var normalized = NormalizeSegments(resolved, !absolute);
        if (absolute)
        {
            return "/" + normalized;
        }

        return normalized.Length > 0 ? normalized : ".";
    }

    public static string Dirname(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return ".";
        }

        var hasRoot = path[0] == '/';
        var end = -1;
        var matchedSlash = true;
        for (var i = path.Length - 1; i >= 1; i--)
        {
            if (path[i] == '/')
            {
                if (!matchedSlash)
                {
                    end = i;
                    break;
                }
            }
            else
            {
                matchedSlash = false;
            }
        }

        if (end == -1)
        {
            return hasRoot ? "/" : ".";
        }

        if (hasRoot && end == 1)
        {
            return "//";
        }

        return path[..end];
    }

    public static string Basename(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var end = path.Length;
        while (end > 0 && path[end - 1] == '/')
        {
            end--;
        }

        if (end == 0)
        {
            return string.Empty;
        }

        var start = path.LastIndexOf('/', end - 1) + 1;
        return path[start..end];
    }

    /// <summary><c>path.relative(from, to)</c> with <paramref name="cwd"/> standing in for <c>process.cwd()</c>.</summary>
    public static string Relative(string cwd, string from, string to)
    {
        var f = Resolve(cwd, from);
        var t = Resolve(cwd, to);
        if (f == t)
        {
            return string.Empty;
        }

        var fromParts = f == "/" ? [] : f[1..].Split('/');
        var toParts = t == "/" ? [] : t[1..].Split('/');
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

        for (var i = common; i < toParts.Length; i++)
        {
            result.Add(toParts[i]);
        }

        return string.Join('/', result);
    }

    private static string NormalizeSegments(string path, bool allowAboveRoot)
    {
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
                else if (allowAboveRoot)
                {
                    stack.Add("..");
                }

                continue;
            }

            stack.Add(segment);
        }

        return string.Join('/', stack);
    }
}
