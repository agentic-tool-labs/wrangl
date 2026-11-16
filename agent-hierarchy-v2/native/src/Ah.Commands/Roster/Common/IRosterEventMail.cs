using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>Sends one mailbox event under one id to several boxes.</summary>
public interface IRosterEventMail
{
    /// <summary>Sends the event <paramref name="eventName"/> with id <paramref name="id"/> to each of <paramref name="recipients"/> and returns the addresses it reached; one that cannot be reached is left out.</summary>
    IReadOnlyList<string> SendEvent(string cwd, string hierarchyDir, string id, string eventName, string text, JsonObject data, IReadOnlyList<string> recipients);
}
