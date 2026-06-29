using System;

namespace MeshWeaver.Social;

/// <summary>
/// Credentials for calling a single user's social platform API. Persisted as the
/// content of an <c>ApiCredential</c> MeshNode under the Profile's <c>_ApiCredentials</c>
/// satellite partition. Access rules restrict read to Admin + the Profile owner.
///
/// Token storage security is left to the hosting app (Data Protection on monolith,
/// KeyVault-backed protector in Aspire). This record only carries the fields; the
/// wire-level encryption is applied by an <c>IPersonalDataProtector</c>-style wrapper
/// outside this project.
/// </summary>
public sealed record PlatformCredential
{
    /// <summary>Platform identifier matching <see cref="IPlatformPublisher.Platform"/>.</summary>
    public required string Platform { get; init; }

    /// <summary>The authorized user's stable ID on the platform (e.g. LinkedIn URN "urn:li:person:xyz").</summary>
    public required string SubjectId { get; init; }

    /// <summary>Current access token. May be short-lived — refreshed via <see cref="RefreshToken"/> when expired.</summary>
    public required string AccessToken { get; init; }

    /// <summary>Refresh token (null for platforms that don't support refresh, e.g. raw OAuth2 implicit flow).</summary>
    public string? RefreshToken { get; init; }

    /// <summary>When the current <see cref="AccessToken"/> expires. Publishers check this and refresh if within 60s of expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>OAuth scope granted, space-delimited (e.g. "r_member_social w_member_social").</summary>
    public string? Scope { get; init; }

    /// <summary>When these credentials were last refreshed. Used for auditing + detecting stale entries.</summary>
    public DateTimeOffset AcquiredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True if the access token has expired (or is within 60 seconds of expiring).</summary>
    public bool IsExpired => ExpiresAt is not null && ExpiresAt.Value <= DateTimeOffset.UtcNow.AddSeconds(60);
}
