using System;
using System.Collections.Immutable;
using System.Linq;

namespace MeshWeaver.Mesh.Security;

/// <summary>Which of an instance's two key slots a presented key hash matches.</summary>
public enum InstanceKeySlot
{
    /// <summary>Neither — the key does not authenticate as this instance.</summary>
    None = 0,

    /// <summary>The instance's current key (<see cref="MeshWeaverInstance.KeyHash"/>).</summary>
    Current = 1,

    /// <summary>A key a rotation staged and has not committed (<see cref="MeshWeaverInstance.PendingKeyHash"/>).</summary>
    Staged = 2,
}

/// <summary>
/// The outcome of one key transition: the instance as it must be written, the hashes whose index
/// entries must be deleted (they stop authenticating), the hash whose index entry must be written
/// (null when none), or — when the transition is refused — the reason, and nothing else.
/// </summary>
/// <param name="Next">The instance after the transition (unchanged when refused or a no-op).</param>
/// <param name="Retired">Hashes that stop authenticating; their index entries are deleted.</param>
/// <param name="Indexed">A hash that starts authenticating; its index entry is written first.</param>
/// <param name="Refusal">Why the transition was refused, or null.</param>
/// <param name="Changed">Whether the instance record must be written — false for a refusal and for
/// an idempotent repeat, which write nothing at all.</param>
public sealed record InstanceKeyTransition(
    MeshWeaverInstance Next,
    ImmutableList<string> Retired,
    string? Indexed,
    string? Refusal,
    bool Changed);

/// <summary>
/// The registry-side rules of a TWO-PHASE instance-key rotation (MeshWeaver#2802), pure so they can
/// be tested without a mesh and read in one place.
///
/// <para>🚨 <b>Why two phases.</b> The first rotation design had the control plane ADOPT the new hash
/// the moment the operator reported it — which deleted the old key's index entry at once, while Key
/// Vault, the synced Secret and the pods still held the old key. Any step after that which failed
/// (the sync wait, the restart, or an adoption routed to a portal that is not the registry) left an
/// instance whose next pod restart presents a key the registry does not accept: a delayed 401 storm
/// behind a quiet failure. The registry now STAGES the new hash first — both keys authenticate — and
/// COMMITS only when the key the instance actually reads is presented back to it.</para>
///
/// <para>Every transition is authorised by POSSESSION: the caller presents a key of this instance,
/// and the slot that key matches decides what it may do. A key can therefore only ever re-key or
/// revoke the instance it belongs to — there is no standing credential that can rotate someone
/// else's.</para>
/// </summary>
public static class InstanceKeyRotation
{
    /// <summary>Whether <paramref name="hash"/> is a lowercase SHA-256 hex digest (64 chars) — the
    /// only shape the registry stores or accepts. Pure.</summary>
    public static bool IsKeyHash(string? hash) =>
        hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Which slot of <paramref name="instance"/> the presented hash matches. Constant-time
    /// per comparison; an empty slot never matches. Pure.</summary>
    public static InstanceKeySlot SlotOf(MeshWeaverInstance instance, string presentedHash)
    {
        if (!IsKeyHash(presentedHash))
            return InstanceKeySlot.None;
        if (!string.IsNullOrEmpty(instance.KeyHash) && InstanceKeys.HashEquals(presentedHash, instance.KeyHash))
            return InstanceKeySlot.Current;
        if (!string.IsNullOrEmpty(instance.PendingKeyHash) && InstanceKeys.HashEquals(presentedHash, instance.PendingKeyHash))
            return InstanceKeySlot.Staged;
        return InstanceKeySlot.None;
    }

    /// <summary>
    /// STAGE <paramref name="newHash"/> as the instance's next key, authorised by presenting the
    /// instance's CURRENT key. Afterwards the current key AND the new one authenticate.
    ///
    /// <list type="bullet">
    /// <item>An earlier staged hash is replaced (and retired). The caller decides that this is safe —
    /// the operator does so only after establishing that neither Key Vault nor the synced Secret
    /// holds that key, i.e. that nobody holds it; otherwise it RESUMES that earlier rotation instead
    /// of staging a new one.</item>
    /// <item>The new hash already staged: an idempotent repeat, nothing changes.</item>
    /// <item>Presenting the STAGED key is refused. The registry cannot tell from the key alone which
    /// key the running pods present — they re-read it only when they restart — so it never lets a
    /// stage retire the current key: only a <see cref="Commit"/>, after the restart, may.</item>
    /// </list>
    /// </summary>
    public static InstanceKeyTransition Stage(
        MeshWeaverInstance instance, string presentedHash, string newHash, DateTimeOffset now)
    {
        if (!IsKeyHash(newHash))
            return Refused(instance, "keyHash must be the lowercase SHA-256 hex of the new raw key (64 hex chars) — "
                + "the registry stores hashes only, and this one arrived in the wrong shape");
        switch (SlotOf(instance, presentedHash))
        {
            case InstanceKeySlot.None:
                return Refused(instance, "the presented key is not a key of this instance");
            case InstanceKeySlot.Staged:
                return Refused(instance, "the presented key is the STAGED key of a rotation that has not been committed — "
                    + "finish that rotation (restart onto it, then commit) before staging another; nothing was changed");
        }
        if (string.Equals(instance.KeyHash, newHash, StringComparison.Ordinal))
            return Refused(instance, "that hash is already this instance's current key — mint a new key to rotate to");
        if (string.Equals(instance.PendingKeyHash, newHash, StringComparison.Ordinal))
            return NoOp(instance);

        var retired = string.IsNullOrEmpty(instance.PendingKeyHash)
            ? ImmutableList<string>.Empty
            : ImmutableList.Create(instance.PendingKeyHash);
        var next = instance with { PendingKeyHash = newHash, PendingKeyIssuedAt = now };
        return new InstanceKeyTransition(next, retired, newHash, null, Changed: true);
    }

    /// <summary>
    /// COMMIT the staged key, authorised by presenting THAT key — which is the proof that the key the
    /// instance reads is the staged one. The staged key becomes current and the old current is retired.
    /// Presenting the current key while nothing is staged is an idempotent repeat of a commit that
    /// already happened. Presenting the current key while a key IS staged is refused: whoever presents
    /// it has not received the staged key, and retiring the key they hold would lock them out.
    /// </summary>
    public static InstanceKeyTransition Commit(MeshWeaverInstance instance, string presentedHash, DateTimeOffset now)
    {
        switch (SlotOf(instance, presentedHash))
        {
            case InstanceKeySlot.Staged:
                var retired = string.IsNullOrEmpty(instance.KeyHash)
                    ? ImmutableList<string>.Empty
                    : ImmutableList.Create(instance.KeyHash);
                var next = instance with
                {
                    KeyHash = instance.PendingKeyHash,
                    KeyIssuedAt = now,
                    PendingKeyHash = "",
                    PendingKeyIssuedAt = null,
                };
                return new InstanceKeyTransition(next, retired, null, null, Changed: true);
            case InstanceKeySlot.Current when string.IsNullOrEmpty(instance.PendingKeyHash):
                return NoOp(instance);
            case InstanceKeySlot.Current:
                return Refused(instance, "the presented key is the CURRENT key while a new key is staged — whoever "
                    + "presents it has not received the staged key, so nothing was retired; both keys still authenticate");
            default:
                return Refused(instance, "the presented key is not a key of this instance");
        }
    }

    /// <summary>
    /// REVOKE the presented key without a successor: it stops authenticating at once. Revoking the
    /// current key while a key is staged leaves the staged key as the one that still authenticates.
    /// </summary>
    public static InstanceKeyTransition RevokePresented(MeshWeaverInstance instance, string presentedHash, DateTimeOffset now) =>
        SlotOf(instance, presentedHash) switch
        {
            InstanceKeySlot.Current => new InstanceKeyTransition(
                instance with { KeyHash = "", KeyRevokedAt = now },
                ImmutableList.Create(instance.KeyHash), null, null, Changed: true),
            InstanceKeySlot.Staged => new InstanceKeyTransition(
                instance with { PendingKeyHash = "", PendingKeyIssuedAt = null, KeyRevokedAt = now },
                ImmutableList.Create(instance.PendingKeyHash), null, null, Changed: true),
            _ => Refused(instance, "the presented key is not a key of this instance"),
        };

    /// <summary>
    /// REVOKE every key of the instance — the registry-side admin act for a key whose value nobody
    /// should need to know. The instance record and its grants stay; it authenticates again only once
    /// its owner re-issues a key. Revoking an instance that holds no key is an idempotent repeat.
    /// </summary>
    public static InstanceKeyTransition RevokeAll(MeshWeaverInstance instance, DateTimeOffset now)
    {
        var retired = new[] { instance.KeyHash, instance.PendingKeyHash }
            .Where(h => !string.IsNullOrEmpty(h))
            .ToImmutableList();
        if (retired.IsEmpty)
            return NoOp(instance);
        return new InstanceKeyTransition(
            instance with { KeyHash = "", PendingKeyHash = "", PendingKeyIssuedAt = null, KeyRevokedAt = now },
            retired, null, null, Changed: true);
    }

    private static InstanceKeyTransition NoOp(MeshWeaverInstance instance) =>
        new(instance, ImmutableList<string>.Empty, null, null, Changed: false);

    private static InstanceKeyTransition Refused(MeshWeaverInstance instance, string reason) =>
        new(instance, ImmutableList<string>.Empty, null, reason, Changed: false);
}
