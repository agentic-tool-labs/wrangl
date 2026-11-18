using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Roster;

namespace Ah.Mesh;

/// <summary>The inbox's per-entry <c>ladder:</c> annotation: where each entry of a box stands on the delivery ladder.</summary>
/// <param name="Mailbox">The mailbox whose ladder rows are read.</param>
/// <param name="Roster">The roster that reads the team file.</param>
/// <param name="Config">The configuration that tells pane members.</param>
/// <param name="Hierarchy">The hierarchy services that read recorded activity.</param>
/// <param name="Clock">The clock the view's mail core runs on.</param>
public sealed record LadderView(IMailbox Mailbox, IRosterCore Roster, IConfigCore Config, HierarchyServices Hierarchy, IClock Clock)
{
    /// <summary>
    /// <paramref name="entries"/> of the box <paramref name="address"/> names, each entry not from <c>ahd</c> annotated; <see langword="null"/>
    /// when the box's team file cannot be read and the entries stay as they are.
    /// </summary>
    public IReadOnlyList<JsonValue>? Annotate(ResolvedAddress address, IReadOnlyList<JsonValue> entries, double nowMs) =>
        new DeliveryLadder(Mailbox, MeshModule.CreateMailCore(Clock), Roster, Config, Hierarchy, new NoPlacement(), Clock).ViewOf(address, entries, nowMs);

    private sealed class NoPlacement : IDeliveryPaste
    {
        public JsonObject PlacementOf(JsonValue member, string? teamTransport) =>
            new() { ["pasteable"] = false, ["reason"] = "no-pane", ["transport"] = JsonValue.Null, ["pane"] = JsonValue.Null };

        public PasteOutcome Paste(JsonValue member, string teamName, string? teamTransport, string payload, string entryId, string boxDir, int headerChars = 0, string? herdrFlavour = null) =>
            throw new InvalidOperationException("the inbox view never pastes");
    }
}
