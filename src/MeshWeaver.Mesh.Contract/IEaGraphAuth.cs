using System.Diagnostics;
using System.Reactive.Linq;
using MeshWeaver.Messaging;

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
///
/// <para>🚨 <b>The surface is <see cref="IObservable{T}"/>, and that is load-bearing — #3433.</b>
/// This seam has TWO consumers with incompatible threading contracts: the consent controller, an
/// ASP.NET action where <c>async</c>/<c>await</c> is sanctioned, and the Executive Assistant's
/// agent TOOLS, which run on a hub. A <c>Task</c>-shaped seam does not merely permit the wrong
/// thing on the second path, it FORCES it: the only way to consume a <c>Task&lt;T&gt;</c> is to
/// await it, and awaiting from a hub turn parks the single-threaded action block that has to
/// process the reply to the very mesh read being awaited. The read then cannot complete, its
/// timeout fires, and the caller is told the user never connected. A reactive seam is consumable
/// from BOTH: the hub side composes and <c>Subscribe</c>s, and the controller bridges once at its
/// own edge through <c>ObserveCompletion</c>. See
/// <c>Doc/Architecture/ExecutiveAssistantCredentialReads</c>.</para>
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

    /// <summary>
    /// Exchanges the consent auth-code for tokens and stores the user's encrypted refresh token.
    /// Cold: the exchange runs on <c>Subscribe</c>, and emits once — <c>true</c> when a refresh
    /// token was stored.
    /// </summary>
    /// <param name="code">The authorization code Microsoft redirected back with.</param>
    /// <param name="redirectUri">The callback URI the code was issued against.</param>
    /// <param name="userObjectId">The directory object id of the consenting user.</param>
    IObservable<bool> ExchangeAndStore(string code, string redirectUri, string userObjectId);

    /// <summary>
    /// Mints a fresh delegated access token for the user. Cold: nothing happens until
    /// <c>Subscribe</c>, and the result emits exactly once.
    ///
    /// <para>🚨 The three states of <see cref="EaGraphAccess.Connection"/> are all distinct
    /// answers. <see cref="EaConnection.Connected"/> carries the token;
    /// <see cref="EaConnection.NotConnected"/> means the credential read completed and found none —
    /// the only truthful moment to offer <see cref="ConnectPath"/>; and
    /// <see cref="EaConnection.Undetermined"/> means the read or the exchange did not produce an
    /// answer at all. Folding the third into the second is the defect this seam was reshaped to
    /// remove.</para>
    /// </summary>
    /// <param name="userObjectId">The directory object id of the acting user.</param>
    IObservable<EaGraphAccess> GetAccessToken(string userObjectId);

    /// <summary>
    /// Reads the user's stored credential WITHOUT minting a token — the cheap "has this user
    /// connected?" question the consent endpoint and the send-as-user precheck ask.
    /// <see cref="EaGraphAccess.AccessToken"/> is always null here.
    /// </summary>
    /// <param name="userObjectId">The directory object id of the user to check.</param>
    IObservable<EaGraphAccess> GetConnection(string userObjectId);

    // ── Retiring Task surface ────────────────────────────────────────────────────────────────────
    //
    // 🚨 These three exist ONLY so MeshWeaver.Plugins (ExecutiveAssistantPlugin, GraphEmailSender)
    // keeps compiling across the two merges this change takes, and they are HUB-UNSAFE by
    // construction: awaiting one from an agent round or any hub-reachable code is exactly #3433.
    // Nothing in this repository calls them. They are default-implemented as forwarders over the
    // reactive surface so an implementer only has to write the reactive half, and they are counted
    // as debt by HubReachableAsyncGuard's contract-seam arm — the ratchet is what removes them once
    // MeshWeaver.Plugins is off them. Do NOT add a caller, and do NOT add a fourth.
    //
    // They are deliberately NOT [Obsolete]: MeshWeaver.Plugins builds -warnaserror against a core
    // CHECKOUT at a pinned ref, so the attribute would red that repo's main the moment this merges,
    // before its own half could land. The guard carries the deprecation instead, where it cannot
    // break a dependent that has not migrated yet.

    /// <summary>
    /// Deprecated forwarder — <see cref="ExchangeAndStore"/>. Retained only for the cross-repo
    /// migration; see the note above.
    /// </summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="redirectUri">The callback URI the code was issued against.</param>
    /// <param name="userObjectId">The directory object id of the consenting user.</param>
    /// <param name="ct">Cancels the WAIT, not the exchange.</param>
    Task<bool> ExchangeAndStoreAsync(
        string code, string redirectUri, string userObjectId, CancellationToken ct)
        => ExchangeAndStore(code, redirectUri, userObjectId)
            .ObserveCompletion(LateFault(nameof(ExchangeAndStoreAsync)), ct);

    /// <summary>
    /// Deprecated forwarder — <see cref="GetAccessToken"/>. Collapses the three-state answer back
    /// to a nullable string, which is the information loss #3433 is about; migrate the caller.
    /// </summary>
    /// <param name="userObjectId">The directory object id of the acting user.</param>
    /// <param name="ct">Cancels the WAIT, not the read.</param>
    Task<string?> GetAccessTokenAsync(string userObjectId, CancellationToken ct)
        => GetAccessToken(userObjectId)
            .Select(a => a.AccessToken)
            .ObserveCompletion(LateFault(nameof(GetAccessTokenAsync)), ct);

    /// <summary>
    /// Deprecated forwarder — <see cref="GetConnection"/>. Collapses
    /// <see cref="EaConnection.Undetermined"/> into <c>false</c>, i.e. into "never connected";
    /// migrate the caller.
    /// </summary>
    /// <param name="userObjectId">The directory object id of the user to check.</param>
    /// <param name="ct">Cancels the WAIT, not the read.</param>
    Task<bool> IsConnectedAsync(string userObjectId, CancellationToken ct)
        => GetConnection(userObjectId)
            .Select(a => a.IsConnected)
            .ObserveCompletion(LateFault(nameof(IsConnectedAsync)), ct);

    /// <summary>
    /// The late-fault reporter every forwarder above hands to <c>ObserveCompletion</c>. Never an
    /// empty lambda: a fault arriving after the bridge's Task settled has no other sink, and
    /// discarding it is half of what that bridge exists to prevent. <c>Trace</c> rather than a
    /// logger because this seam is SDK-free and holds no <c>ILogger</c> — the same choice
    /// <see cref="MeshWeaver.Mesh.Threading.IIoPool.InvokeObservable{T}"/> makes.
    /// </summary>
    private static Action<Exception> LateFault(string member) =>
        ex => Trace.TraceError(
            "IEaGraphAuth.{0}: the credential operation faulted AFTER the wait settled — reported, "
            + "not orphaned: {1}: {2}", member, ex.GetType().Name, ex.Message);
}
