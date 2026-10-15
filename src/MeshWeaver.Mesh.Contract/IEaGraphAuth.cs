using System.Collections.Immutable;
using System.Reactive.Linq;

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

    // ── Several mailboxes (2026-10-13) ────────────────────────────────────────────────────────
    // Every member below has a DEFAULT that answers for the primary mailbox only, so an
    // implementation written before several mailboxes existed (a test fake, an older host) keeps
    // compiling and keeps serving exactly the one mailbox it always served.

    /// <summary>
    /// The consent URL for one mailbox: <paramref name="loginHint"/> pre-fills the account Microsoft
    /// asks for (a reconnect of a known address), and <paramref name="selectAccount"/> sends
    /// <c>prompt=select_account</c>, so a person adding ANOTHER mailbox can pick a different
    /// Microsoft account than the one they are signed in with.
    /// </summary>
    string BuildConsentUrl(string state, string redirectUri, string? loginHint, bool selectAccount) =>
        BuildConsentUrl(state, redirectUri);

    /// <summary>
    /// Exchanges the consent code and stores the grant for <paramref name="account"/>:
    /// <see cref="EaMailboxes.Primary"/> (or null) for the primary mailbox, an added mailbox's id to
    /// reconnect it, or <see cref="EaMailboxes.New"/> to add one. A NEW consent whose address is
    /// already connected rewrites that mailbox instead of adding a duplicate. Cold; emits once.
    /// </summary>
    IObservable<bool> ExchangeAndStore(string code, string redirectUri, string userObjectId, string? account) =>
        EaMailboxes.IsPrimary(account)
            ? ExchangeAndStore(code, redirectUri, userObjectId)
            : Observable.Return(false);

    /// <summary>
    /// Mints a delegated access token for one of the person's mailboxes (null = the primary). Same
    /// three-state contract as <see cref="GetAccessToken(string)"/>.
    /// </summary>
    IObservable<EaGraphAccess> GetAccessToken(string userObjectId, string? account) =>
        EaMailboxes.IsPrimary(account)
            ? GetAccessToken(userObjectId)
            : Observable.Return(EaGraphAccess.NotConnected("this host serves a single mailbox"));

    /// <summary>
    /// Every mailbox the person has connected, primary first, each with its classified state and
    /// no token. Cold; emits once. 🚨 A failed read of the LIST is an error on the stream, never an
    /// empty list — an empty list is the positive answer "nothing is connected".
    /// </summary>
    IObservable<ImmutableList<EaMailbox>> GetMailboxes(string userObjectId) =>
        GetConnection(userObjectId).Select(access => access.Connection == EaConnection.NotConnected
            ? ImmutableList<EaMailbox>.Empty
            : ImmutableList.Create(new EaMailbox(EaMailboxes.Primary, null, access)));

    /// <summary>
    /// Disconnects one mailbox. Emits <c>true</c> when a grant was removed, <c>false</c> when there
    /// was none. Cold. The primary mailbox's record is emptied rather than deleted (the added
    /// mailboxes live beneath it); an added mailbox's record is deleted.
    /// </summary>
    IObservable<bool> RemoveMailbox(string userObjectId, string account) => Observable.Return(false);
}
