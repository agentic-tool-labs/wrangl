using System.Globalization;
using Ah.Abstractions;

namespace Ah.Core.Files;

/// <summary>How a store names the temporary file it writes before renaming over the target.</summary>
public enum TmpNameStyle
{
    /// <summary><c>{path}.{pid}.tmp</c></summary>
    PidTmp,

    /// <summary><c>{path}.tmp-{pid}</c></summary>
    TmpPid,

    /// <summary><c>.{basename}.{pid}.tmp</c> in the target's own directory.</summary>
    DotfilePidTmp,

    /// <summary><c>{path}.{pid}.{8 hex digits}.tmp</c>, the digits from 4 random bytes.</summary>
    PidHexTmp,
}

public static class TmpNames
{
    /// <summary>Builds the temporary path for <paramref name="path"/>. <paramref name="path"/> is used as given, with no normalisation.</summary>
    public static string For(TmpNameStyle style, string path, int pid, IRandom? random = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var p = pid.ToString(CultureInfo.InvariantCulture);
        switch (style)
        {
            case TmpNameStyle.PidTmp:
                return $"{path}.{p}.tmp";
            case TmpNameStyle.TmpPid:
                return $"{path}.tmp-{p}";
            case TmpNameStyle.DotfilePidTmp:
                var slash = path.LastIndexOf('/');
                var name = $".{path[(slash + 1)..]}.{p}.tmp";
                return slash < 0 ? name : (slash == 0 ? "/" : path[..slash] + "/") + name;
            case TmpNameStyle.PidHexTmp:
                ArgumentNullException.ThrowIfNull(random);
                Span<byte> bytes = stackalloc byte[4];
                random.Fill(bytes);
                return $"{path}.{p}.{Convert.ToHexStringLower(bytes)}.tmp";
            default:
                throw new ArgumentOutOfRangeException(nameof(style));
        }
    }
}
