using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// Links a Memex agent thread to the Microsoft Teams conversation that spawned it, so the agent's reply
/// can be sent back into the same Teams chat (proactively, after the agent finishes). Stored as a
/// satellite of the thread at <c>{threadPath}/_TeamsConversation/{id}</c>. The reply sender uses
/// <see cref="LastDeliveredMessageId"/> to send only new agent messages once.
/// </summary>
public record TeamsConversation
{
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = "teams-conversation";   // one per thread → stable id

    /// <summary>The agent thread this Teams conversation drives.</summary>
    public string ThreadPath { get; init; } = string.Empty;

    /// <summary>Bot Framework <c>serviceUrl</c> for the channel (where replies are POSTed).</summary>
    public string ServiceUrl { get; init; } = string.Empty;

    /// <summary>The Teams conversation id to reply into.</summary>
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>The Teams (AAD) user id of the person chatting — used to run as the right Memex user.</summary>
    [Browsable(false)]
    public string? TeamsUserId { get; init; }

    /// <summary>The id of the last agent message already delivered to Teams (dedup / send-once).</summary>
    [Browsable(false)]
    public string? LastDeliveredMessageId { get; init; }
}
