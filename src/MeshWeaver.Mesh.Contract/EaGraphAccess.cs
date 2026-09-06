namespace MeshWeaver.Mesh;

/// <summary>
/// What the platform actually KNOWS about a user's delegated Microsoft Graph connection. Three
/// states, not two — and the third one is the whole point of this type.
///
/// <para>🚨 <b>Read <see cref="Undetermined"/> before writing a caller.</b> Until #3433 the
/// credential read answered <c>(null, null)</c> for BOTH "this user never connected" and "the read
/// did not complete", so a swallowed 10-second timeout on the credential node rendered as
/// <i>"please connect your mailbox"</i> to a user whose grant was stored and valid. Pressing that
/// link re-consents something that was never revoked, so it appears to work — which is why the
/// defect survived undiagnosed. A caller that folds <see cref="Undetermined"/> into
/// <see cref="NotConnected"/> re-creates it exactly.</para>
/// </summary>
public enum EaConnection
{
    /// <summary>
    /// The user has no stored credential: the read completed and told us so. This is the ONLY state
    /// in which offering the consent link is truthful.
    /// </summary>
    NotConnected,

    /// <summary>A credential was read and carries a refresh token.</summary>
    Connected,

    /// <summary>
    /// The read did not produce an answer — it timed out, the transport faulted, or the stored
    /// content could not be interpreted. <b>Nothing is known</b> about whether the user connected.
    ///
    /// <para>Callers must say so ("I could not check your mailbox connection just now") rather than
    /// asserting either of the other two. Offering the consent link here is the #3433 lie; refusing
    /// outright is equally wrong, because the user may genuinely never have connected.</para>
    /// </summary>
    Undetermined,
}

/// <summary>
/// The answer to "can the Executive Assistant act on this user's mailbox right now?" — the
/// <see cref="Connection"/> state, the minted token when one was minted, and a
/// <see cref="Diagnostic"/> that says WHY when the answer is not a clean yes.
///
/// <para>Deliberately one record for both entry points on <see cref="IEaGraphAuth"/>:
/// <see cref="IEaGraphAuth.GetConnection"/> never fills <see cref="AccessToken"/> (it reads the
/// stored credential and mints nothing), while <see cref="IEaGraphAuth.GetAccessToken"/> fills it
/// exactly when <see cref="Connection"/> is <see cref="EaConnection.Connected"/>. Both answer the
/// same three-state question, so they answer it in the same shape.</para>
/// </summary>
/// <param name="Connection">What is known about the user's connection.</param>
/// <param name="AccessToken">
/// A freshly minted delegated Graph access token — non-empty if and only if
/// <paramref name="Connection"/> is <see cref="EaConnection.Connected"/> AND the caller asked for a
/// token. Never persisted by this record; it is short-lived by construction.
/// </param>
/// <param name="Diagnostic">
/// Why the answer is what it is, for logs and for a message a human can act on. Present for
/// <see cref="EaConnection.Undetermined"/>, and for a refusal the token endpoint explained; null for
/// a clean <see cref="EaConnection.Connected"/>.
/// </param>
public sealed record EaGraphAccess(
    EaConnection Connection,
    string? AccessToken = null,
    string? Diagnostic = null)
{
    /// <summary>True only for <see cref="EaConnection.Connected"/> — never for the other two.</summary>
    public bool IsConnected => Connection == EaConnection.Connected;

    /// <summary>
    /// The user has no stored credential. The read COMPLETED and said so — this is not the
    /// fallback for a read that failed (<see cref="Unknown"/> is).
    /// </summary>
    public static EaGraphAccess NotConnected(string? diagnostic = null) =>
        new(EaConnection.NotConnected, Diagnostic: diagnostic);

    /// <summary>A credential is stored; the token is present when the caller asked for one.</summary>
    public static EaGraphAccess Connected(string? accessToken = null) =>
        new(EaConnection.Connected, accessToken);

    /// <summary>
    /// The read did not produce an answer. <paramref name="diagnostic"/> is REQUIRED — an
    /// undetermined state with nothing to say is indistinguishable from the swallow this type
    /// exists to remove.
    /// </summary>
    public static EaGraphAccess Unknown(string diagnostic) =>
        new(EaConnection.Undetermined, Diagnostic: diagnostic);
}
