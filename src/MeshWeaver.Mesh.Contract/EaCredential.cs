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

    /// <summary>The token endpoint's own words for the refusal (<c>error</c> + <c>error_description</c>), for the diagnostic.</summary>
    [Browsable(false)]
    public string? RefusalReason { get; init; }
}
