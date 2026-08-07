using System.Security.Cryptography;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// A registration bootstrap key — the credential a NEW deployment presents on first startup to
/// register itself as a <see cref="MeshWeaverInstance"/> without a human copying anything
/// (Kubernetes-bootstrap-token / pre-auth-key style). Minted by a <b>platform admin</b> on the
/// registry, reusable across installs, revocable, optionally expiring.
///
/// <para>🚨 A bootstrap key is NOT an instance key. It authorizes exactly ONE operation —
/// <c>POST /api/instances/register</c> — and never grants catalog access itself: registration
/// issues the instance its own <c>mwi_</c> key, and what that instance may pull is still decided by
/// <see cref="PluginGrant"/>s (including the operator's <c>PluginCatalog:DefaultGrants</c> seed).
/// The prefixes differ (<c>mwr_</c> vs <c>mwi_</c>) so neither validator can ever accept the
/// other's key, and a leaked value is immediately identifiable.</para>
///
/// <para>Instances registered with this key are owned by the admin who MINTED it — the key carries
/// its minter's identity, so auto-registered instances land in that admin's partition exactly like
/// hand-registered ones, and revoking the key cuts off further registrations without touching the
/// instances it already created.</para>
/// </summary>
public record RegistrationKey
{
    /// <summary>SHA-256 hex hash of the issued bootstrap key. The raw key is shown once at
    /// minting and never persisted.</summary>
    public string KeyHash { get; init; } = "";

    /// <summary>What this key is for, in the minting admin's words (e.g. "AKS env scaffold").</summary>
    public string Description { get; init; } = "";

    /// <summary>ObjectId of the platform admin who minted the key. Instances registered with it
    /// are created under THIS identity.</summary>
    public string OwnerUserId { get; init; } = "";

    /// <summary>Display name of the minting admin.</summary>
    public string OwnerUserName { get; init; } = "";

    /// <summary>Email of the minting admin.</summary>
    public string OwnerUserEmail { get; init; } = "";

    /// <summary>When the key was minted.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Optional expiry; a key past this instant fails validation. Null = does not expire
    /// (revocation remains available).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Kill switch. A revoked key fails validation while its record (and every instance it
    /// registered) stays intact — the audit trail survives the revocation.</summary>
    public bool IsRevoked { get; init; }

    /// <summary>How many registrations this key has performed. Stamped on each successful use —
    /// registration is a rare, deliberate operation, so there is no write-frequency concern.</summary>
    public int UsageCount { get; init; }

    /// <summary>When the key last performed a registration.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Whether the key is currently usable at <paramref name="now"/>.</summary>
    public bool IsUsable(DateTimeOffset now) =>
        !IsRevoked && (ExpiresAt is null || now < ExpiresAt);
}

/// <summary>
/// Routing index from a bootstrap key's hash to the <see cref="RegistrationKey"/> node that owns
/// it, mirroring <see cref="MeshWeaverInstanceIndex"/>: hash the presented key, route by its prefix
/// to <c>MeshWeaverInstance/{hashPrefix}</c> (the shared instance-credential index namespace — the
/// content type tells the two apart), follow the pointer. Written under System identity.
/// </summary>
public record RegistrationKeyIndex
{
    /// <summary>SHA-256 hex hash of the bootstrap key.</summary>
    public string KeyHash { get; init; } = "";

    /// <summary>Full path of the <see cref="RegistrationKey"/> node this hash belongs to.</summary>
    public string KeyPath { get; init; } = "";
}

/// <summary>
/// The bootstrap-key contract — mint, hash and recognize <c>mwr_</c> keys. Reuses the
/// <see cref="InstanceKeys"/> primitives (same entropy, same hashing, same fixed-time comparison)
/// with a distinct prefix so the two credential kinds can never be confused.
/// </summary>
public static class RegistrationKeys
{
    /// <summary>Prefix on every issued bootstrap key. Distinct from <c>mwi_</c> (instance) and
    /// <c>mw_</c> (personal): a bootstrap key registers instances, nothing else.</summary>
    public const string KeyPrefix = "mwr_";

    /// <summary>Mints a fresh bootstrap key. The raw value is returned ONCE — only its hash is
    /// ever stored.</summary>
    public static string Generate() =>
        KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(InstanceKeys.KeyByteLength))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    /// <summary>Whether <paramref name="rawKey"/> has the bootstrap-key shape. Validity is decided
    /// by hashing and resolving the index, never by comparing configured strings.</summary>
    public static bool HasKeyShape(string? rawKey) =>
        !string.IsNullOrWhiteSpace(rawKey) && rawKey.StartsWith(KeyPrefix, StringComparison.Ordinal);
}
