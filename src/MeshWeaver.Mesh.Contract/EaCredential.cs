using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh;

/// <summary>
/// A user's <b>delegated</b> Microsoft Graph credential for the Executive Assistant — acquired via
/// per-user, just-in-time consent (the user grants the EA access to their OWN mailbox/calendar only when
/// they first use the tool). The refresh token is stored <b>encrypted</b> (AES-GCM via the deployment
/// master key); never the raw token. One per user, under <c>{username}/_EaCredential/{id}</c>.
///
/// <para>This replaces standing application-wide Graph permissions: access is the user's own, consented
/// by them, and revocable by them.</para>
///
/// <para><b>Several mailboxes.</b> A person may connect more than one Microsoft 365 mailbox. The grant
/// written first (and every grant written before several mailboxes existed) is the PRIMARY one and keeps
/// its original path; each mailbox added since is its own record beneath it, with an
/// <see cref="EaMailboxes.NewAccountId"/> id. See <see cref="EaMailbox"/>.</para>
/// </summary>
public record EaCredential
{
    /// <summary>Stable satellite id; one credential per user, so it is constant.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = "ea-credential";   // one per user → stable id

    /// <summary>The user's directory object id (matches AccessContext.ObjectId).</summary>
    [Browsable(false)]
    public string? UserObjectId { get; init; }

    /// <summary>Encrypted (enc:v1:…) Graph refresh token. Decrypt only at token-exchange time.</summary>
    [Browsable(false)]
    public string? RefreshTokenEncrypted { get; init; }

    /// <summary>The delegated scopes the user consented to (space-separated).</summary>
    [Browsable(false)]
    public string? Scopes { get; init; }

    /// <summary>When consent was granted / the token last refreshed.</summary>
    [Browsable(false)]
    public DateTimeOffset AcquiredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When Microsoft's token endpoint DEFINITIVELY refused this grant (an OAuth <c>invalid_grant</c>
    /// / <c>interaction_required</c> answer: revoked, expired, password changed, consent withdrawn),
    /// stamped by the redemption that was refused. A stamped credential reads as NOT connected —
    /// for the assistant, which then hands over the consent link instead of "retry in a moment", and
    /// for the consent controller, which then runs the dialog instead of bouncing a "connected" user
    /// back (MeshWeaver.Plugins#1615). Cleared by the next successful consent, which rewrites the
    /// node. <c>null</c> for a grant that has never been refused.
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset? RefusedAt { get; init; }

    /// <summary>
    /// The mailbox account this grant serves: null (or <see cref="EaMailboxes.Primary"/>) for the
    /// primary grant, <c>ea-credential--{10 hex}</c> for a mailbox added since. Informational — the
    /// record's PATH is what identifies it.
    /// </summary>
    [Browsable(false)]
    public string? Account { get; init; }

    /// <summary>
    /// The address the person signed in as when consenting (the id token's <c>preferred_username</c>
    /// or <c>email</c>). The mailbox's label, and what the assistant's <c>mailbox</c> argument matches.
    /// Null on a grant written before addresses were recorded.
    /// </summary>
    [Browsable(false)]
    public string? Address { get; init; }

    /// <summary>The token endpoint's own words for the refusal (<c>error</c> + <c>error_description</c>), for the diagnostic.</summary>
    [Browsable(false)]
    public string? RefusalReason { get; init; }
}
