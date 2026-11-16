using Ah.Core.Json;

namespace Ah.Daemon;

/// <summary>What the bridge's <c>POST /ui/v1/answer</c> needs to answer a parked decision for the user; the host composes it.</summary>
/// <param name="ResolveDir">The hierarchy dir a dirId names among the dirs the <c>questions:</c> slices are built from, or null.</param>
/// <param name="Answer">
/// Answers as the user: <c>{dir, team, run, id, choice | choices}</c> in; out the shared answer core's result, <c>{refused: {reason,
/// detail}}</c> or <c>{id, answers, decision}</c>, with <c>event_error</c> when the Orchestrator could not be told and <c>event_id</c>, the
/// mailbox entry of the <c>question-answered</c> event, when it was. Or answers a waiting item: <c>{dir, team, item, since, choice}</c> in;
/// out the same, or <c>{bad_request}</c> for a choice that names none of the item's options.
/// </param>
/// <param name="Status">
/// For <c>GET /ui/v1/answer-status</c>: <c>(team, event id)</c> in; out <c>{seen, seen_at?, state}</c> for a <c>question-answered</c> event in
/// that team's Orchestrator box, or null for any other id. Reads only.
/// </param>
/// <param name="History">
/// For <c>GET /ui/v1/history</c>: <c>(hierarchy dir, query)</c> in. A query with an <c>Id</c> gets that thread whole, <c>{threads, dir}</c>
/// (no thread of that id is an empty list); any other gets the page the History's filter keeps, <c>{threads, dir, next}</c>, each reply cut
/// to its head. Reads only.
/// </param>
public sealed record UiAnswerSupport(Func<string, string?> ResolveDir, Func<JsonObject, JsonObject> Answer, Func<string, string, JsonObject?>? Status = null, Func<string, Ah.Hierarchy.QaHistoryQuery, JsonObject>? History = null);
