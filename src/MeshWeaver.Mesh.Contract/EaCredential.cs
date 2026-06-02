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
}
