using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>
/// The team files a registration was made under. A registration written by the CLI carries <c>team_files</c> (address → team file,
/// one per box); an older one, or one made through <c>POST /v1/register</c>, carries only <c>team_file</c>.
/// </summary>
internal static class RegistrationTeams
{
    /// <summary>The distinct team files of the registration in order: <c>team_files</c> values when any, else <c>[team_file]</c>, else none.</summary>
    public static IReadOnlyList<string> Of(JsonObject? registration)
    {
        var files = new List<string>();
        if (registration?.Get("team_files") is { IsMap: true } byAddress)
        {
            var map = byAddress.AsObject();
            foreach (var key in map.Keys)
            {
                if (map[key] is { IsText: true } f && f.AsString().Length > 0 && !files.Contains(f.AsString()))
                {
                    files.Add(f.AsString());
                }
            }
        }

        if (files.Count == 0 && registration?.Get("team_file") is { IsText: true } single && single.AsString().Length > 0)
        {
            files.Add(single.AsString());
        }

        return files;
    }

    /// <summary>The team file recorded for <paramref name="address"/>: its <c>team_files</c> entry, else <c>team_file</c>, else null.</summary>
    public static string? For(JsonObject? registration, string address)
    {
        if (registration?.Get("team_files") is { IsMap: true } byAddress
            && byAddress.AsObject().Get(address) is { IsText: true } f && f.AsString().Length > 0)
        {
            return f.AsString();
        }

        return registration?.Get("team_file") is { IsText: true } single && single.AsString().Length > 0 ? single.AsString() : null;
    }

    /// <summary>The boxes of the registration: a map of address to directory, or an older list of directories keyed by position.</summary>
    public static IReadOnlyList<BoxRef> Boxes(JsonObject? registration)
    {
        var boxes = registration?.Get("boxes");
        var list = new List<BoxRef>();
        if (boxes is { IsMap: true } map)
        {
            foreach (var key in map.AsObject().Keys)
            {
                if (map.AsObject()[key] is { IsText: true } dir)
                {
                    list.Add(new BoxRef(key, dir.AsString()));
                }
            }
        }
        else if (boxes is { IsArray: true } array)
        {
            for (var i = 0; i < array.AsArray().Count; i++)
            {
                if (array.AsArray()[i] is { IsText: true } dir)
                {
                    list.Add(new BoxRef(i.ToString(System.Globalization.CultureInfo.InvariantCulture), dir.AsString()));
                }
            }
        }

        return list;
    }
}
