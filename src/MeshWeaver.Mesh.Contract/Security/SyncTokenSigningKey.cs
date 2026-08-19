namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The registry's HMAC key for signing short-lived <see cref="SyncAccessToken"/>s, held as a mesh
/// node so it is minted once and shared by every replica.
///
/// <para><b>Why a node and not configuration.</b> A signing key has to be IDENTICAL across every
/// replica of one registry — a token minted on pod A must verify on pod B — and it should never pass
/// through a human's hands. Configuration achieves the first only if an operator gets it right in
/// every environment, and fails the second by construction. A node is minted by the mesh on first
/// need, is automatically the same for every replica reading it, and no one ever sees it. Same
/// reasoning, same partition and the same <c>enc:</c> envelope as
/// <c>PluginRegistryCredential</c>.</para>
///
/// <para>🚨 <b>Uniqueness is the NODE's, not a lock's.</b> Two replicas racing to mint both issue a
/// create against the same path, and the mesh's create-only semantics let exactly one win while
/// reporting the path as existing to the loser, which then adopts the winner's key. The node write is
/// the idempotency token — the same discipline <see cref="Reminder"/> uses to fire exactly once
/// across replicas. There is no hand-rolled gate, and there must never be one: a lock cannot span
/// pods, and a "read, then create if absent" without adopting the loser's answer is precisely the
/// race that leaves two replicas signing with different keys.</para>
///
/// <para><b>Rotation keeps the outgoing key.</b> Tokens already in flight were signed by the key
/// being replaced, so a rotation that simply overwrote it would invalidate every one of them
/// mid-run. <see cref="ProtectedPrevious"/> stays verifiable until
/// <see cref="PreviousValidUntil"/> — one token lifetime is enough, since nothing older can still
/// be unexpired.</para>
/// </summary>
public record SyncTokenSigningKey
{
    /// <summary>The key tokens are SIGNED with, base64, <c>enc:</c>-protected at rest when a master
    /// key is configured (plaintext passthrough otherwise — the same policy as provider keys).</summary>
    public string ProtectedCurrent { get; init; } = "";

    /// <summary>When <see cref="ProtectedCurrent"/> was minted.</summary>
    public DateTimeOffset CurrentIssuedAt { get; init; }

    /// <summary>The key replaced by the last rotation. Still ACCEPTED for verification until
    /// <see cref="PreviousValidUntil"/>, never used for signing. Null before the first rotation.</summary>
    public string? ProtectedPrevious { get; init; }

    /// <summary>When the previous key stops being accepted. Past this instant it is dead weight and
    /// the next write drops it.</summary>
    public DateTimeOffset? PreviousValidUntil { get; init; }

    /// <summary>
    /// When the next rotation is DUE. Data rather than a hard-coded interval, so the schedule is
    /// visible and adjustable without a deploy — and so whatever drives rotation (an admin action
    /// today, a durable <see cref="Reminder"/> once the reminder runner exists) reads the due date
    /// from the same place instead of keeping its own copy.
    /// </summary>
    public DateTimeOffset? RotateAfter { get; init; }

    /// <summary>Whether rotation is due at <paramref name="now"/>.</summary>
    /// <param name="now">The instant to judge against.</param>
    /// <returns>True when <see cref="RotateAfter"/> is set and has passed.</returns>
    public bool RotationDue(DateTimeOffset now) => RotateAfter is { } due && now >= due;

    /// <summary>Whether the previous key should still be accepted at <paramref name="now"/>.</summary>
    /// <param name="now">The instant to judge against.</param>
    /// <returns>True while the grace window is open.</returns>
    public bool PreviousStillValid(DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(ProtectedPrevious)
        && PreviousValidUntil is { } until && now < until;
}

/// <summary>Where the signing key lives, and the constants governing its lifecycle.</summary>
public static class SyncTokenSigningKeys
{
    /// <summary>The node type.</summary>
    public const string NodeType = "SyncTokenSigningKey";

    /// <summary>Namespace under the Admin partition — System and platform admins only.</summary>
    public const string Namespace = "Admin/SyncTokenSigningKey";

    /// <summary>
    /// Node id. There is exactly ONE signing key per registry, and it is a FIXED path on purpose:
    /// uniqueness is enforced by two replicas colliding on the same path, which cannot happen if the
    /// id varies by host, pod or time.
    /// </summary>
    public const string Id = "current";

    /// <summary>The single node path.</summary>
    public const string Path = $"{Namespace}/{Id}";

    /// <summary>Entropy per key, in bytes — comfortably above
    /// <see cref="SyncAccessToken.MinimumSigningKeyBytes"/>.</summary>
    public const int KeyByteLength = 48;

    /// <summary>How long a key signs before rotation is due.</summary>
    public static readonly TimeSpan RotationInterval = TimeSpan.FromDays(30);

    /// <summary>
    /// How long the outgoing key stays verifiable after a rotation. One maximum token lifetime,
    /// because no token signed before the rotation can still be unexpired after that — a longer
    /// window would keep a retired key alive for no reader.
    /// </summary>
    public static readonly TimeSpan RotationGrace = SyncAccessToken.MaximumLifetime;
}
