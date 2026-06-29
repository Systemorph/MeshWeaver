namespace Memex.Portal.Shared.Teams;

/// <summary>
/// Seam for talking to the Microsoft Teams / Bot Framework connector: validate that an inbound activity
/// really came from the Bot Framework, and post a reply back into a conversation. <b>Tests substitute a
/// hand-written fake</b> (CI has no Bot credentials / no real Teams), so the inbound→thread→reply pipeline
/// can be exercised without the live connector.
/// </summary>
public interface ITeamsClient
{
    /// <summary>True when the Teams bot is enabled and credentials are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Validates the inbound <c>Authorization</c> header as a genuine Bot Framework token.</summary>
    Task<bool> ValidateInboundAsync(string? authorizationHeader, CancellationToken ct);

    /// <summary>Posts a message activity back into the given Teams conversation.</summary>
    Task<bool> SendMessageAsync(string serviceUrl, string conversationId, string text, CancellationToken ct);
}
