using System.Text;
using Ah.Abstractions;
using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Composition;

/// <summary>The recover verb's route to the daemon: <c>POST /v1/bare/recover</c>, never started on demand, never retried.</summary>
internal sealed class BareRecoverComposition(ServiceGraph graph) : IRosterBareRecover
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public BareRecoverReply Ask(string hierarchyDir, string team, string member, string op)
    {
        var body = new JsonObject { ["hierarchy_dir"] = hierarchyDir, ["team"] = team, ["member"] = member, ["op"] = op };
        DaemonReply reply;
        try
        {
            reply = graph.Daemon.RequestAsync(new DaemonRequest("POST", "/v1/bare/recover", Encoding.UTF8.GetBytes(JsonText.Stringify(body, 0)), Budget, StartIfDown: false)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new BareRecoverReply(null, $"daemon-unavailable: {e.Message}");
        }

        if (reply.Status == 0)
        {
            return new BareRecoverReply(null, $"daemon-unavailable: {reply.FallbackReason ?? "down"}");
        }

        JsonObject? parsed = null;
        try
        {
            parsed = JsonText.Parse(reply.Body.ToArray()) is { IsMap: true } o ? o.AsObject() : null;
        }
        catch (JsonParseException)
        {
            // an unreadable body is reported below with the status
        }

        if (reply.Status is >= 200 and < 300 && parsed is not null)
        {
            return new BareRecoverReply(parsed, null);
        }

        var error = parsed?.Get("error");
        var code = error?.Member("code") is { IsText: true } c ? c.AsString() : $"http {reply.Status}";
        var message = error?.Member("message") is { IsText: true } m ? m.AsString() : string.Empty;
        return new BareRecoverReply(null, $"{code}: {message}".TrimEnd(' ', ':'));
    }
}
