using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    /// <summary>
    /// <c>ah msg decision ui-answer --json &lt;body&gt;</c>: the web form's answer route for a GUI caller (the Herdr mod). The body is the
    /// one <c>POST /ui/v1/answer</c> takes, run through the route's own table and the bridge's answer core with <c>caller: herdr</c>;
    /// stdout is the route's reply body. Exit 0 answered, 2 the route's 400 or 409, 3 a caller that is not proven the user, 1 an
    /// internal error (spec-herdr-desktop.md P3.2). No ahd is needed.
    /// </summary>
    private int UiAnswerVerb()
    {
        if (_opts.Text("json") is not { } body)
        {
            _out.Json(new JsonObject { ["error"] = "bad-request", ["detail"] = "--json needs the answer body as its value" });
            return 2;
        }

        if (!(_uiAnswerCaller?.Invoke() ?? UiAnswerCallerProven()))
        {
            _out.Json(new JsonObject { ["refused"] = true, ["reason"] = "unproven", ["detail"] = "only the user in a terminal, or the Herdr app with no agent on its process line, may answer here" });
            return 3;
        }

        var support = DaemonComposition.UiAnswer(new Lazy<ServiceGraph>(_graph), ProofHierarchyDirs, "herdr");
        var r = Ah.Daemon.DaemonModule.RunUiAnswer(support, System.Text.Encoding.UTF8.GetBytes(body));
        if (r.Reply is not { } reply)
        {
            _out.Note($"ui-answer: internal error ({r.Outcome})");
            return 1;
        }

        _out.Json(reply);
        return r.Status == 200 ? 0 : 2;
    }

    /// <summary>Rule (a): the caller is the user by the existing classifier; rule (b): its direct parent is <c>herdr-app</c> and no harness is on its line.</summary>
    private bool UiAnswerCallerProven()
    {
        if (Principal.Kind == PrincipalKind.User)
        {
            return true;
        }

        var table = PlatformModule.CreateProcessTable(_graph.Runner);
        return MeshModule.IsHerdrAppChild(table, Environment.ProcessId, pid => SessionDirectory.ExecutableOf(_graph.Runner, pid));
    }
}
