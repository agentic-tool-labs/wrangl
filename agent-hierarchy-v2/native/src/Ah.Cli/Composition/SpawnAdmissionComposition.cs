using System.Text;
using Ah.Abstractions;
using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Composition;

/// <summary>The spawn verbs' route to the daemon's pacing: <c>POST /v1/load/admit-spawn</c>, never started on demand, never retried.</summary>
internal sealed class SpawnAdmissionComposition(ServiceGraph graph) : IRosterSpawnAdmission
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    public SpawnAdmissionReply Ask(string team, string member, bool dryRun)
    {
        var body = new JsonObject { ["team"] = team, ["member"] = member, ["dry_run"] = dryRun };
        DaemonReply reply;
        try
        {
            reply = graph.Daemon.RequestAsync(new DaemonRequest("POST", "/v1/load/admit-spawn", Encoding.UTF8.GetBytes(JsonText.Stringify(body, 0)), Budget, StartIfDown: false)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Unknown($"daemon unavailable: {e.Message}");
        }

        if (reply.Status == 0)
        {
            return Unknown($"daemon unavailable: {reply.FallbackReason ?? "down"}");
        }

        try
        {
            if (reply.Status == 200 && JsonText.Parse(reply.Body.ToArray()) is { IsMap: true } answer && answer.Member("admitted") is { IsBool: true } admitted)
            {
                var triggers = answer.Member("triggers") is { IsArray: true } list
                    ? list.AsArray().Where(t => t.IsText).Select(t => t.AsString()).ToList()
                    : [];
                return new SpawnAdmissionReply(
                    admitted.AsBool(), answer.Member("next_at") is { IsText: true } next ? next.AsString() : null, triggers, null,
                    answer.Member("harnesses") is { IsNumber: true } harnesses ? (int)harnesses.AsNumber() : null);
            }
        }
        catch (JsonParseException)
        {
            // an unreadable body is reported below with the status
        }

        return Unknown($"the daemon answered http {reply.Status}");
    }

    private static SpawnAdmissionReply Unknown(string why) => new(true, null, [], why);
}
