using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// The user's recorded pre-approval of the Codex trust auto-answer: the <see cref="RosterModule.CodexTrustPreapprovalKey"/> meta row,
/// <c>{v:1, scopes:[{path, kind:"root"|"parent", granted_at, via}]}</c>. Only <c>codex-trust --preapprove … --yes</c> adds a scope;
/// <c>--revoke</c> and <c>--revoke-all</c> remove one.
/// </summary>
internal static class CodexPreapproval
{
    internal static readonly string[] Vias = ["install", "bootstrap", "cli"];

    internal static string StorePath(IAhEnvironment env) => RosterModule.CodexTrustPreapprovalKey;

    internal static string AutoAnswerLogPath(IAhEnvironment env) => RosterModule.CodexTrustAutoAnswerLogPath(env);

    internal static ICodexTrustAutoAnswer Of(RosterContext c) =>
        RosterModule.CreateCodexTrustAutoAnswer(c.Services.Files, c.Services.Environment, CodexTrustNeeds.TrustOf(c), c.Services.Clock, c.Services.HomeDocs);

    /// <summary>The recorded scopes; a missing file is none, and a file that is not the expected shape is a failure, never silently none.</summary>
    internal static List<JsonObject> Read(RosterContext c)
    {
        var scopes = Of(c).ReadScopes(out var problem);
        if (problem is not null)
        {
            throw new RosterFailure($"codex-trust: {StorePath(c.Services.Environment)} is not a pre-approval store ({problem}) — nothing was changed; fix or remove it");
        }

        return scopes;
    }

    /// <summary>Records <paramref name="scopes"/> as the store's row; null removes the row.</summary>
    internal static void Write(RosterContext c, List<JsonObject>? scopes)
    {
        string? body = null;
        if (scopes is not null)
        {
            var array = new JsonArray();
            foreach (var scope in scopes)
            {
                array.Add(scope);
            }

            body = JsonText.Stringify(new JsonObject { ["v"] = 1, ["scopes"] = array }, 0);
        }

        var docs = c.Services.HomeDocs ?? throw new RosterFailure("codex-trust: there is no state database to record the pre-approvals in");
        try
        {
            docs.Write(StorePath(c.Services.Environment), body);
        }
        catch (InvalidOperationException e)
        {
            throw new RosterFailure($"codex-trust: {e.Message}");
        }
    }

    /// <summary>How many auto-answer log rows are dated within the last <paramref name="days"/> days of <paramref name="now"/>.</summary>
    internal static int AutoAnswersSince(IAhEnvironment env, DateTimeOffset now, int days)
    {
        var path = AutoAnswerLogPath(env);
        if (!File.Exists(path))
        {
            return 0;
        }

        var since = now.AddDays(-days);
        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                var row = JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(line));
                if (row.IsMap && MemberPlanning.Text(row.AsObject(), "at") is { } at
                    && DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
                    && when >= since)
                {
                    count++;
                }
            }
            catch (FormatException)
            {
            }
        }

        return count;
    }
}
