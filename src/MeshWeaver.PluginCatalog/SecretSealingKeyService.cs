using MeshWeaver.AI;   // IProviderKeyProtector keeps its ORIGINAL namespace in MeshWeaver.Mesh.Contract (#2398 forwarders)
using System.Reactive.Linq;
using System.Security.Cryptography;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>The public half of the sealing key, as published to installations.</summary>
/// <param name="KeyId">The key's id, named by every envelope sealed to it.</param>
/// <param name="PublicKey">The P-256 public key, base64 SPKI.</param>
public sealed record SealingPublicKey(string KeyId, string PublicKey);

/// <summary>
/// The control instance's secret-sealing key: an installation seals a secret to its PUBLIC half
/// (<see cref="SecretEnvelope"/>), and only this service opens it. The key pair is a MESH NODE
/// (<c>Admin/SecretSealingKey/current</c>) minted on first use, shared by every replica, with nothing to
/// configure and no credential to copy, exactly like the sync token signing key.
///
/// <para>🚨 <b>The private half is never node content in the clear.</b> It is stored as <c>enc:</c> ciphertext of
/// the platform key protector (<see cref="IProviderKeyProtector"/>), refused when no master key exists. The
/// public half is the only thing published.</para>
///
/// <para><b>Rotation.</b> The key rotates every <see cref="SecretSealingKeys.RotationInterval"/>; the outgoing key
/// stays accepted for <see cref="SecretSealingKeys.RotationGrace"/>, so a value sealed just before a rotation
/// still opens. An envelope naming any other key id does not open.</para>
/// </summary>
public sealed class SecretSealingKeyService(IMessageHub hub, ILogger<SecretSealingKeyService> logger)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The public key to seal to, minting the pair on first use and rotating it when it is due. Cold; emits once.</summary>
    public IObservable<SealingPublicKey> PublicKey()
        => Resolve().Select(stored => new SealingPublicKey(stored.CurrentId, stored.CurrentPublicKey));

    /// <summary>
    /// Opens an envelope sealed for <paramref name="context"/>. Null for anything that does not open: not an
    /// envelope, an unknown or expired key id, a different context, tampering. Cold; emits once.
    /// </summary>
    public IObservable<string?> Unseal(string? envelopeJson, string context)
    {
        if (SecretEnvelope.TryParse(envelopeJson) is not { } envelope)
            return Observable.Return<string?>(null);
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(Read).Select(stored =>
        {
            if (stored is null) return null;
            var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
            var protectedPrivate = envelope.KeyId == stored.CurrentId ? stored.ProtectedCurrentPrivateKey
                : envelope.KeyId == stored.PreviousId && stored.PreviousStillValid(DateTimeOffset.UtcNow) ? stored.ProtectedPreviousPrivateKey
                : null;
            if (protectedPrivate is null || Unprotect(protector, protectedPrivate) is not { } privateKey)
                return null;
            try { return envelope.Unseal(privateKey, context); }
            finally { CryptographicOperations.ZeroMemory(privateKey); }
        });
    }

    /// <summary>Rotates now (an operator's call; the schedule does it by itself). Cold; emits the new public key.</summary>
    public IObservable<SealingPublicKey> Rotate(string rotatedBy)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => Read().SelectMany(stored => Write(NewKey(stored), stored is null)))
            .Do(_ => logger.LogInformation("Secret sealing key rotated by {By}; the outgoing key stays accepted for {Grace}", rotatedBy, SecretSealingKeys.RotationGrace))
            .Select(stored => new SealingPublicKey(stored.CurrentId, stored.CurrentPublicKey));
    }

    private IObservable<SecretSealingKey> Resolve()
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        // ONE sealed System scope around the whole resolve: the key is in the Admin partition and the
        // caller is an HTTP request that must not be left holding System.
        return access.RunAsSystem(() => Read().SelectMany(stored =>
            stored is null ? Write(NewKey(null), create: true)
            : stored.RotateAfter is { } due && DateTimeOffset.UtcNow >= due ? Write(NewKey(stored), create: false)
            : Observable.Return(stored)));
    }

    private SecretSealingKey NewKey(SecretSealingKey? outgoing)
    {
        var protector = hub.ServiceProvider.GetRequiredService<IProviderKeyProtector>();
        var (publicKey, privateKey) = SecretEnvelope.GenerateKeyPair();
        var now = DateTimeOffset.UtcNow;
        try
        {
            return new SecretSealingKey
            {
                CurrentId = "k" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
                CurrentPublicKey = Convert.ToBase64String(publicKey),
                // Required: a private key that cannot be encrypted is not stored at all.
                ProtectedCurrentPrivateKey = protector.Protect(Convert.ToBase64String(privateKey))
                    ?? throw new InvalidOperationException("The sealing key cannot be stored: no master key is configured."),
                CurrentIssuedAt = now,
                PreviousId = outgoing?.CurrentId,
                ProtectedPreviousPrivateKey = outgoing?.ProtectedCurrentPrivateKey,
                PreviousValidUntil = outgoing is null ? null : now.Add(SecretSealingKeys.RotationGrace),
                RotateAfter = now.Add(SecretSealingKeys.RotationInterval),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private IObservable<SecretSealingKey> Write(SecretSealingKey key, bool create)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(SecretSealingKeys.Id, SecretSealingKeys.Namespace)
        {
            Name = "Secret sealing key",
            NodeType = SecretSealingKeys.NodeType,
            State = MeshNodeState.Active,
            Content = key,
        };
        // 🚨 ALWAYS read back; never return what we proposed. Under a concurrent create both callers are told
        // "created" while storage keeps the FIRST write: the stored node is the only authority, otherwise two
        // replicas end up sealing to different keys.
        var written = create ? meshService.CreateNodes([node]).Select(_ => 0) : meshService.CreateOrUpdateNode(node).Select(_ => 0);
        return written.SelectMany(_ => Read()).Select(stored =>
            stored ?? throw new InvalidOperationException("The secret sealing key was written but could not be read back."));
    }

    private IObservable<SecretSealingKey?> Read()
        => hub.GetMeshNode(SecretSealingKeys.Path, ReadTimeout).Take(1)
            .Select(node => node?.ContentAs<SecretSealingKey>(hub.JsonSerializerOptions));

    private static byte[]? Unprotect(IProviderKeyProtector? protector, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var raw = protector is null ? stored : protector.Unprotect(stored);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return Convert.FromBase64String(raw); }
        catch (FormatException) { return null; }
    }
}
