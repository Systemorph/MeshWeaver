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
/// <para>The key is cached in memory for <see cref="CacheDuration"/>. Short, because a rotation
/// performed by another replica has to be picked up without a restart; the outgoing key stays
/// verifiable through its grace window, so a replica briefly holding the older material still
/// verifies everything it should.</para>
/// </summary>
public sealed class SyncTokenSigningKeyService(IMessageHub hub, ILogger<SyncTokenSigningKeyService> logger)
{
    /// <summary>How long resolved key material is reused before the node is re-read. Short so a
    /// rotation on another replica takes effect without a restart.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private (DateTimeOffset At, SyncTokenSigningMaterial Material)? cached;

    /// <summary>
    /// The key material to sign and verify with, minting it if this registry has none yet.
    /// </summary>
    /// <returns>The current signing key plus any still-valid previous key.</returns>
    public IObservable<SyncTokenSigningMaterial> Resolve()
    {
        lock (gate)
        {
            if (cached is { } hit && DateTimeOffset.UtcNow - hit.At < CacheDuration)
                return Observable.Return(hit.Material);
        }

        var access = hub.ServiceProvider.GetService<AccessService>();
        // ONE sealed System scope around the whole resolve — the key lives in the Admin partition and
        // the caller is an HTTP request that must not be left holding System (#1790).
        return access.RunAsSystem(() => Read()
                .SelectMany(stored => stored is null ? Mint() : Observable.Return(stored)))
            .Select(Materialize)
            .Do(material =>
            {
                lock (gate)
                    cached = (DateTimeOffset.UtcNow, material);
            });
    }

    /// <summary>
    /// The key material as it ALREADY stands, without minting. This is the VERIFY path: a caller
    /// presenting a token has not authenticated yet, and minting on their behalf would let an
    /// anonymous request write a node — while being pointless anyway, since a token cannot verify
    /// against a key created after it was signed.
    /// </summary>
    /// <returns>The material, or null when this registry has never minted a key.</returns>
    public IObservable<SyncTokenSigningMaterial?> Existing()
    {
        lock (gate)
        {
            if (cached is { } hit && DateTimeOffset.UtcNow - hit.At < CacheDuration)
                return Observable.Return<SyncTokenSigningMaterial?>(hit.Material);
        }

        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(Read)
            .Select(stored => stored is null ? null : Materialize(stored))
            .Do(material =>
            {
                if (material is null)
                    return;
                lock (gate)
                    cached = (DateTimeOffset.UtcNow, material);
            });
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
        var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
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
                lock (gate)
                    cached = (DateTimeOffset.UtcNow, material);
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
        var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
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
        hub.GetMeshNode(SyncTokenSigningKeys.Path, ReadTimeout)
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

    private static string Protect(IProviderKeyProtector? protector, byte[] secret)
    {
        var encoded = Convert.ToBase64String(secret);
        return protector?.Protect(encoded) ?? encoded;
    }

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
