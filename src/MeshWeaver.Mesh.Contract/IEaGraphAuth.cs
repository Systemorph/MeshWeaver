namespace MeshWeaver.Mesh;

/// <summary>
/// Seam for the Executive Assistant's per-user delegated Graph access. The real implementation
/// (<c>EaGraphAuth</c>, in the portal host alongside its OAuth consent controller) drives a
/// Microsoft OAuth consent + token flow; <b>tests substitute a hand-written fake</b> that returns a
/// canned token (or none) so the consent step is mocked away and the EA tool / plugin can be
/// exercised without a real browser consent or live Graph round-trip.
///
/// <para>It lives in the mesh contract — beside <see cref="IEmailSender"/> and
/// <see cref="EmailOptions"/> — because it is deliberately SDK-FREE (strings and bools only). That
/// is what lets the EA's mailbox TOOLS ship in the <c>MeshWeaver.Mail.MicrosoftGraph</c> module,
/// which carries the Microsoft Graph SDK, while the token flow and its consent controller stay in
/// the host, which does not.</para>
/// </summary>
public interface IEaGraphAuth
{
    /// <summary>True when the credentials needed for the delegated flow are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The portal-relative path the user is sent to in order to grant consent (e.g.
    /// <c>/auth/ea/connect</c>).
    ///
    /// <para>It hangs off this seam rather than being a constant a caller re-declares: the consent
    /// ROUTE is defined exactly once, by the controller that registers it in the host, and the
    /// implementation surfaces it here. That is what lets the Executive Assistant's tools — which
    /// ship in the Microsoft Graph mail MODULE — build a "please connect" link without referencing
    /// a host controller they cannot see.</para>
    /// </summary>
    string ConnectPath { get; }

    /// <summary>The Microsoft consent URL to send the user to (incremental consent).</summary>
    string BuildConsentUrl(string state, string redirectUri);

    /// <summary>Exchanges the consent auth-code for tokens and stores the user's encrypted refresh token.</summary>
    Task<bool> ExchangeAndStoreAsync(string code, string redirectUri, string userObjectId, CancellationToken ct);

    /// <summary>A fresh delegated access token for the user, or null when they have not connected.</summary>
    Task<string?> GetAccessTokenAsync(string userObjectId, CancellationToken ct);

    /// <summary>True when the user already connected (has a stored credential).</summary>
    Task<bool> IsConnectedAsync(string userObjectId, CancellationToken ct);
}
