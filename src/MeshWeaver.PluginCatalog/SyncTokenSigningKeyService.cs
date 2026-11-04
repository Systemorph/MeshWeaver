using MeshWeaver.AI;   // IProviderKeyProtector & co keep their ORIGINAL namespace in MeshWeaver.Mesh.Contract (#2398 forwarders)
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Resolves — minting on first need — the HMAC key the registry signs
/// <see cref="SyncAccessToken"/>s with, and rotates it.
///
/// <para>🚨 <b>Uniqueness comes from the MESH NODE, never from a lock.</b> Minting issues a
/// create against the single fixed path <see cref="SyncTokenSigningKeys.Path"/>. The mesh creates
/// only — an existing path is reported back rather than overwritten — so when two replicas race,
/// exactly one key is written and the loser is TOLD it lost and adopts the winner's key by reading
/// it. A lock could not span pods; a "read, create if absent" that ignored the collision would leave
/// two replicas signing with different keys, and a token minted on one would fail on the other.</para>
///
/// <para>🚨 <b>The key is HELD, not re-read (MeshWeaver#7354).</b> Every instance-key and sync-token
/// check on the registry verifies against this key, so it is the hottest read on the
/// <c>/api/plugins</c> surface — and it almost never changes (one rotation a month). It used to be a
/// one-shot <c>GetMeshNode</c> behind a one-minute TTL, so once a minute a request paid a
/// cross-hub round-trip to the key's owning per-node hub, and any round-trip that went unanswered
/// for ten seconds answered that request 503 (13 of 18 registry-side 503s over 43 h, against an
/// idle owner). Now the first successful read takes ONE standing subscription to the node's mirror
/// in the process-wide cache (<c>GetMeshNodeStream</c>), as System, for the life of the mesh: every
/// later request is answered from memory, and a rotation written on ANY replica arrives through the
/// change feed instead of waiting for a TTL. The one-shot read remains only for a cold process (no
/// hold yet) or a hold that has terminated and whose last material is older than
/// <see cref="CacheDuration"/>.</para>
///
/// <para>A hold that faults or completes is dropped; the last material it delivered stays servable
/// for <see cref="CacheDuration"/> (the staleness the TTL always allowed — the outgoing key stays
/// verifiable through its grace window) and the next request re-takes the hold without waiting for
/// it. Instance state, never static (Doc/Architecture/NoStaticState): the service is a mesh-scoped
/// singleton and its hold dies with the mesh.</para>
/// </summary>
public sealed class SyncTokenSigningKeyService(IMessageHub hub, ILogger<SyncTokenSigningKeyService> logger)
    : IDisposable
{
    /// <summary>How long material may be served from memory when NO hold is live — a cold process
    /// whose first read has not answered yet, or a hold that terminated. While the hold is live the
    /// node is never re-read; this is then only how often the held record is re-materialised, so a
    /// closed grace window or a due rotation is noticed without any IO.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private (DateTimeOffset At, SyncTokenSigningMaterial Material, SyncTokenSigningKey? From)? cached;

    /// <summary>The live hold on the key's mirror; null when none is held.</summary>
    private HoldSlot? hold;

    /// <summary>The newest record the live hold delivered; null until its first frame.</summary>
    private SyncTokenSigningKey? held;

    private bool disposed;
    private int readsIssued;

    /// <summary>The clock the cache window is judged on. Init-only so a test can step past
    /// <see cref="CacheDuration"/> without waiting a minute.</summary>
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>How many one-shot reads of the key node this service has issued. Test seam: a held
    /// key is answered with NO read, however long it has been.</summary>
    internal int ReadsIssued => Volatile.Read(ref readsIssued);

    /// <summary>Whether a hold on the key's mirror is live and has delivered a record. Test seam.</summary>
    internal bool IsHeld
    {
        get
        {
            lock (gate)
                return hold is not null && held is not null;
        }
    }

    /// <summary>
    /// The key material to sign and verify with, minting it if this registry has none yet.
    /// </summary>
    /// <returns>The current signing key plus any still-valid previous key.</returns>
    public IObservable<SyncTokenSigningMaterial> Resolve() => Observable.Defer(ResolveNow);

    // Deferred so a held record that no longer decrypts surfaces as OnError, never as a throw at the
    // call site.
    private IObservable<SyncTokenSigningMaterial> ResolveNow()
    {
        if (FromMemory() is { } inMemory)
            return Observable.Return(inMemory);

        var access = hub.ServiceProvider.GetService<AccessService>();
        // ONE sealed System scope around the whole resolve — the key lives in the Admin partition and
        // the caller is an HTTP request that must not be left holding System (#1790).
        return access.RunAsSystem(() => Read()
                .SelectMany(stored => stored is null ? Mint() : Observable.Return(stored)))
            .Select(stored => (Stored: stored, Material: Materialize(stored)))
            .Do(read => Remember(read.Stored, read.Material))
            .Select(read => read.Material);
    }

    /// <summary>
    /// The key material as it ALREADY stands, without minting. This is the VERIFY path: a caller
    /// presenting a token has not authenticated yet, and minting on their behalf would let an
    /// anonymous request write a node — while being pointless anyway, since a token cannot verify
    /// against a key created after it was signed.
    /// </summary>
    /// <returns>The material, or null when this registry has never minted a key.</returns>
    public IObservable<SyncTokenSigningMaterial?> Existing() => Observable.Defer(ExistingNow);

    private IObservable<SyncTokenSigningMaterial?> ExistingNow()
    {
        if (FromMemory() is { } inMemory)
            return Observable.Return<SyncTokenSigningMaterial?>(inMemory);

        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(Read)
            .Select(stored => stored is null ? null : Materialize(stored))
            .Do(material =>
            {
                // Absent: nothing to hold — a point stream on a path that does not exist is a framework
                // defect (CqrsAndContentAccess, "An OPTIONAL node"), and a verify never mints.
                if (material is null)
                    return;
                Remember(null, material);
            });
    }

    /// <summary>
    /// The material this replica can answer with WITHOUT any IO, or null when it must read. A live
    /// hold answers always (re-materialised from the held record at most once per
    /// <see cref="CacheDuration"/>); without one, the last material answers while it is younger
    /// than <see cref="CacheDuration"/>, and the hold is re-taken without waiting for it. Never blocks.
    /// </summary>
    private SyncTokenSigningMaterial? FromMemory()
    {
        var now = Now();
        SyncTokenSigningKey? record;
        HoldSlot? toSubscribe = null;
        SyncTokenSigningMaterial? answer = null;
        lock (gate)
        {
            record = hold is null ? null : held;
            if (record is null)
            {
                if (cached is { } hit && now - hit.At < CacheDuration)
                {
                    toSubscribe = TakeHoldLocked();
                    answer = hit.Material;
                }
            }
            else if (cached is { } current && ReferenceEquals(current.From, record) && now - current.At < CacheDuration)
                answer = current.Material;
        }
        if (toSubscribe is not null)
            Subscribe(toSubscribe);
        if (answer is not null || record is null)
            return answer;

        // Re-materialise the HELD record — local CPU only (decrypt + grace/rotation evaluation).
        var material = Materialize(record);
        lock (gate)
            cached = (now, material, record);
        return material;
    }

    /// <summary>Records material just read or written, and takes the hold: the node exists now.</summary>
    private void Remember(SyncTokenSigningKey? stored, SyncTokenSigningMaterial material)
    {
        HoldSlot? toSubscribe;
        lock (gate)
        {
            cached = (Now(), material, stored);
            toSubscribe = TakeHoldLocked();
        }
        if (toSubscribe is not null)
            Subscribe(toSubscribe);
    }

    /// <summary>
    /// Takes the ONE standing hold on the key node's mirror if none is live. Only ever called once a
    /// read or a write has established the node exists. Caller holds <see cref="gate"/> and
    /// subscribes the returned slot AFTER leaving it, so a mirror that answers synchronously from
    /// its replay re-enters the lock from outside, never under it.
    /// </summary>
    /// <returns>The new slot to subscribe, or null when a hold is already live.</returns>
    private HoldSlot? TakeHoldLocked()
    {
        if (disposed || hold is not null)
            return null;
        var slot = new HoldSlot();
        hold = slot;
        held = null;
        return slot;
    }

    private void Subscribe(HoldSlot slot)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(hold, slot))
                return;
        }

        var source = HoldOverride is { } test
            ? test()
            : hub.ServiceProvider.GetService<AccessService>()
                .RunAsSystem(() => (IObservable<MeshNode>)hub.GetMeshNodeStream(SyncTokenSigningKeys.Path));
        slot.Subscription.Disposable = source.Subscribe(
            node =>
            {
                var record = node?.ContentAs<SyncTokenSigningKey>(hub.JsonSerializerOptions);
                if (record is null)
                    return;
                lock (gate)
                {
                    if (ReferenceEquals(hold, slot))
                        held = record;
                }
            },
            ex =>
            {
                logger.LogDebug(ex,
                    "The held mirror of {Path} faulted — released; the last key stays servable for {Window} "
                    + "and the next request re-takes the hold", SyncTokenSigningKeys.Path, CacheDuration);
                Release(slot);
            },
            () => Release(slot));
    }

    /// <summary>Drops <paramref name="slot"/> if it is still the live hold (pair-exact, so a newer hold
    /// is never touched). The last material stays servable for <see cref="CacheDuration"/> from now:
    /// a terminated mirror establishes nothing NEW about the key, and the TTL always allowed that much.</summary>
    private void Release(HoldSlot slot)
    {
        lock (gate)
        {
            if (ReferenceEquals(hold, slot))
            {
                hold = null;
                held = null;
                if (cached is { } last)
                    cached = (Now(), last.Material, null);
            }
        }
        slot.Subscription.Dispose();
    }

    /// <summary>
    /// The stream a hold subscribes to, injectable so a test can terminate the mirror on cue.
    /// Production holds the owner's mirror in the process-wide cache.
    /// </summary>
    internal Func<IObservable<MeshNode>>? HoldOverride { get; init; }

    /// <summary>Releases the hold. Called by the container when the mesh is disposed.</summary>
    public void Dispose()
    {
        HoldSlot? slot;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            slot = hold;
            hold = null;
            held = null;
        }
        slot?.Subscription.Dispose();
    }

    private sealed class HoldSlot
    {
        public SingleAssignmentDisposable Subscription { get; } = new();
    }

    /// <summary>
    /// Replaces the signing key, keeping the outgoing one verifiable for
    /// <see cref="SyncTokenSigningKeys.RotationGrace"/> so tokens already in flight keep working.
    /// Idempotent in effect but not in value: each call mints fresh material, so it is driven by a
    /// due date rather than called speculatively.
    /// </summary>
    /// <param name="rotatedBy">Who or what triggered the rotation, for the log.</param>
    /// <returns>The material in force after the rotation.</returns>
    public IObservable<SyncTokenSigningMaterial> Rotate(string rotatedBy)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        // Required: this path WRITES the signing key — see Protect below.
        var protector = hub.ServiceProvider.GetRequiredService<IProviderKeyProtector>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var now = DateTimeOffset.UtcNow;

        return access.RunAsSystem(() => Read()
                .SelectMany(stored =>
                {
                    var rotated = new SyncTokenSigningKey
                    {
                        ProtectedCurrent = Protect(protector, NewSecret()),
                        CurrentIssuedAt = now,
                        // The outgoing key stays verifiable — dropping it here would invalidate every
                        // token minted in the last few minutes.
                        ProtectedPrevious = stored?.ProtectedCurrent,
                        PreviousValidUntil = stored is null
                            ? null
                            : now.Add(SyncTokenSigningKeys.RotationGrace),
                        RotateAfter = now.Add(SyncTokenSigningKeys.RotationInterval),
                    };

                    return meshService.CreateOrUpdateNode(NodeFor(rotated)).Select(_ => rotated);
                }))
            .Select(Materialize)
            .Do(material =>
            {
                Remember(null, material);
                logger.LogInformation(
                    "Sync token signing key rotated by {By}; the outgoing key stays verifiable for {Grace}",
                    rotatedBy, SyncTokenSigningKeys.RotationGrace);
            });
    }

    /// <summary>
    /// Mints the first key for this registry. Runs INSIDE the caller's System scope.
    ///
    /// <para>The create is the whole concurrency story, and the outcome is read from the STORE, not
    /// from the response: storage keeps the first create and discards a concurrent second, but tells
    /// BOTH callers they created it. So we create, then read back, and sign with whatever is
    /// actually there.</para>
    /// </summary>
    private IObservable<SyncTokenSigningKey> Mint()
    {
        // Required: this path WRITES the signing key — see Protect below.
        var protector = hub.ServiceProvider.GetRequiredService<IProviderKeyProtector>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var now = DateTimeOffset.UtcNow;

        var minted = new SyncTokenSigningKey
        {
            ProtectedCurrent = Protect(protector, NewSecret()),
            CurrentIssuedAt = now,
            RotateAfter = now.Add(SyncTokenSigningKeys.RotationInterval),
        };

        return meshService.CreateNodes([NodeFor(minted)])
            .SelectMany(response =>
            {
                if (!response.Success && !response.Existing.Contains(SyncTokenSigningKeys.Path))
                    return Observable.Throw<SyncTokenSigningKey>(new InvalidOperationException(
                        $"Could not mint the sync token signing key: {response.Error}"));

                // 🚨 ALWAYS read back; NEVER return the locally minted material. Measured
                // 2026-08-18: under a genuine concurrent create BOTH callers are told
                // created=1/existing=0 — the response's exists-check lags, exactly as
                // IMeshService.CreateOrUpdateNode's remarks warn — while STORAGE keeps the FIRST
                // write and discards the second. So the response cannot tell you whether you won,
                // and the stored node is the only authority. Signing with what we proposed rather
                // than with what was stored is how two replicas end up on different keys.
                return Read().SelectMany(stored => stored switch
                {
                    null => Observable.Throw<SyncTokenSigningKey>(new InvalidOperationException(
                        "The sync token signing key was created but could not be read back.")),
                    _ => Observable.Return(stored),
                });
            })
            .Do(stored => logger.LogInformation(
                string.Equals(stored.ProtectedCurrent, minted.ProtectedCurrent, StringComparison.Ordinal)
                    ? "Minted this registry's sync token signing key at {Path}; rotation due {Due}"
                    : "Another replica had already minted the sync token signing key at {Path} — "
                      + "adopted the stored one; rotation due {Due}",
                SyncTokenSigningKeys.Path, stored.RotateAfter));
    }

    /// <summary>One-shot read by exact path, inside the caller's System scope.</summary>
    private IObservable<SyncTokenSigningKey?> Read() =>
        Observable.Defer(() =>
            {
                Interlocked.Increment(ref readsIssued);
                return hub.GetMeshNode(SyncTokenSigningKeys.Path, ReadTimeout);
            })
            .Take(1)
            .Select(node => node?.ContentAs<SyncTokenSigningKey>(hub.JsonSerializerOptions));

    private MeshNode NodeFor(SyncTokenSigningKey key) =>
        new(SyncTokenSigningKeys.Id, SyncTokenSigningKeys.Namespace)
        {
            Name = "Sync token signing key",
            NodeType = SyncTokenSigningKeys.NodeType,
            State = MeshNodeState.Active,
            Content = key,
        };

    /// <summary>
    /// Turns the stored record into usable bytes, dropping a previous key whose grace window has
    /// closed and reporting a rotation that is due.
    /// </summary>
    private SyncTokenSigningMaterial Materialize(SyncTokenSigningKey stored)
    {
        var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
        var now = DateTimeOffset.UtcNow;

        var current = Unprotect(protector, stored.ProtectedCurrent);
        if (current is null)
            // A key we cannot decrypt is not a key. Fail loudly and name the likely cause — silently
            // minting a replacement would invalidate every outstanding token AND hide a master-key
            // misconfiguration that also affects every other protected secret.
            throw new InvalidOperationException(
                "The stored sync token signing key could not be decrypted — was the master key changed?");

        var previous = stored.PreviousStillValid(now)
            ? Unprotect(protector, stored.ProtectedPrevious)
            : null;

        if (stored.RotationDue(now))
            logger.LogInformation(
                "The sync token signing key is due for rotation (due {Due}).", stored.RotateAfter);

        return new SyncTokenSigningMaterial(current, previous, stored.RotationDue(now));
    }

    private static byte[] NewSecret() =>
        RandomNumberGenerator.GetBytes(SyncTokenSigningKeys.KeyByteLength);

    /// <summary>
    /// 🚨 No fallback. This mints the key that SIGNS every sync token, so storing it in the clear
    /// would let anyone with read on the node forge tokens. The old body was
    /// <c>protector?.Protect(encoded) ?? encoded</c> — an unregistered protector, or a deployment
    /// with no master key, silently persisted the signing key verbatim. Protect now refuses in the
    /// latter case, and this refuses in the former: minting is an authenticated, on-demand action
    /// (never a boot path), so a loud failure is the right shape — the operator configures
    /// <c>Ai:KeyProtection:MasterKey</c> and retries.
    /// </summary>
    private static string Protect(IProviderKeyProtector protector, byte[] secret) =>
        protector.Protect(Convert.ToBase64String(secret))!;

    private static byte[]? Unprotect(IProviderKeyProtector? protector, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        var raw = protector is null ? stored : protector.Unprotect(stored);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            return Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The signing material in force: what to sign with, and what to still accept.
/// </summary>
/// <param name="Current">The key new tokens are signed with.</param>
/// <param name="Previous">The key retired by the last rotation, still accepted for verification
/// while its grace window is open; null when there is none.</param>
/// <param name="RotationDue">Whether the current key has passed its rotation due date.</param>
public sealed record SyncTokenSigningMaterial(byte[] Current, byte[]? Previous, bool RotationDue)
{
    /// <summary>
    /// Verifies <paramref name="token"/> against the current key, then — only if that fails — the
    /// previous one, so a token minted moments before a rotation still works.
    /// </summary>
    /// <param name="token">The raw token.</param>
    /// <param name="now">The instant to judge expiry against.</param>
    /// <returns>The verified claims, or null.</returns>
    public SyncAccessTokenClaims? Verify(string? token, DateTimeOffset now) =>
        SyncAccessToken.Verify(token, now, Current)
        ?? (Previous is null ? null : SyncAccessToken.Verify(token, now, Previous));
}
