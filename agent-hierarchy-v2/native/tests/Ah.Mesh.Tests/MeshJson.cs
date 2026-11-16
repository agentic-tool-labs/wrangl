using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>The JSON shapes the Node library hands back for the mesh rule's values, rebuilt from the native records.</summary>
internal static class MeshJson
{
    /// <summary>A verdict as <c>decide</c> returns it: the keys of its variant, in the order the rule writes them.</summary>
    internal static JsonObject Verdict(MeshVerdict v)
    {
        JsonValue Rule() => int.TryParse(v.Rule, out var n) ? n : v.Rule;

        var o = new JsonObject { ["allow"] = v.Allow, ["rule"] = Rule() };
        if (v.Warn)
        {
            o["warn"] = true;
            o["code"] = v.Code!;
            o["would"] = v.Would!;
            o["message"] = v.Message!;
            o["mirror"] = v.Mirror;
        }
        else if (!v.Allow)
        {
            o["code"] = v.Code!;
            o["status"] = v.Status!.Value;
            o["message"] = v.Message!;
        }
        else if (v.Count)
        {
            o["count"] = true;
            o["mirror"] = v.Mirror;
            if (v.Link is { } link)
            {
                o["link"] = link;
            }

            if (v.Task is { } task)
            {
                o["task"] = task;
            }

            o["team"] = v.Team!;
            o["dir"] = v.Dir!;
        }

        return o;
    }
}
