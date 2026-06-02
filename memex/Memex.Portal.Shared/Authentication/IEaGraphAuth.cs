namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Seam for the Executive Assistant's per-user delegated Graph access. The real
/// <see cref="EaGraphAuth"/> drives a Microsoft OAuth consent + token flow; <b>tests substitute a
/// hand-written fake</b> that returns a canned token (or none) so the consent step is mocked away and the
/// EA tool / plugin can be exercised without a real browser consent or live Graph round-trip.
/// </summary>
public interface IEaGraphAuth
{
    /// <summary>True when the credentials needed for the delegated flow are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>The Microsoft consent URL to send the user to (incremental consent).</summary>
    string BuildConsentUrl(string state, string redirectUri);

    /// <summary>Exchanges the consent auth-code for tokens and stores the user's encrypted refresh token.</summary>
    Task<bool> ExchangeAndStoreAsync(string code, string redirectUri, string userObjectId, CancellationToken ct);

    /// <summary>A fresh delegated access token for the user, or null when they have not connected.</summary>
    Task<string?> GetAccessTokenAsync(string userObjectId, CancellationToken ct);

    /// <summary>True when the user already connected (has a stored credential).</summary>
    Task<bool> IsConnectedAsync(string userObjectId, CancellationToken ct);
}
