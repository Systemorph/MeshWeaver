using MeshWeaver.AI;   // IProviderKeyProtector keeps its ORIGINAL namespace in MeshWeaver.Mesh.Contract (#2398 forwarders)
using System.Reactive.Linq;
using System.Security.Cryptography;
using MeshWeaver.Data;
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
    /// The current public key if one exists; null otherwise. <b>Never mints and never rotates</b>: this is what the
    /// anonymous publication endpoint reads, so a request from nobody cannot provoke a write. The pair is minted by
    /// <see cref="PublicKey"/>, which the control instance calls itself.
    /// </summary>
    public IObservable<SealingPublicKey?> Existing()
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(Read).Select(stored =>
            stored is null ? null : new SealingPublicKey(stored.CurrentId, stored.CurrentPublicKey));
    }

    /// <summary>
    /// Opens an envelope sealed for <paramref name="context"/>. Null for anything that does not open: not an
    /// envelope, an unknown or expired key id, a different context, tampering. Cold; emits once.
    ///
    /// <para>🚨 <b>Authorized as the CALLER, executed as System.</b> This service is a mesh singleton, so any code
    /// holding a hub can resolve it — in-mesh NodeType source included. The private key is read as System, which no
    /// caller holds, so the caller's own right is checked FIRST: <see cref="Permission.Execute"/> on
    /// <see cref="SecretSealingKeys.Path"/> (an Admin-partition node: a global admin, or the platform running as
    /// System). A caller without it gets <see cref="UnauthorizedAccessException"/>; a check that could not be
    /// decided fails closed with <see cref="InvalidOperationException"/>. Nothing is elevated before either.</para>
    /// </summary>
    public IObservable<string?> Unseal(string? envelopeJson, string context)
        => AuthorizedThenAsSystem(Permission.Execute, "open a sealed secret", () =>
        {
            if (SecretEnvelope.TryParse(envelopeJson) is not { } envelope)
                return Observable.Return<string?>(null);
            return Read().Select(stored =>
            {
                if (stored is null) return null;
                var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
                var protectedPrivate = stored.ProtectedPrivateKeyFor(envelope.KeyId, DateTimeOffset.UtcNow);
                if (protectedPrivate is null || Unprotect(protector, protectedPrivate) is not { } privateKey)
                    return null;
                try { return envelope.Unseal(privateKey, context); }
                finally { CryptographicOperations.ZeroMemory(privateKey); }
            });
        });

    /// <summary>
    /// Rotates now (an operator's call; the schedule does it by itself). Every outgoing key stays accepted for
    /// <see cref="SecretSealingKeys.RotationGrace"/> from its own rotation. Cold; emits the new public key.
    /// Authorized as the caller first — <see cref="Permission.Update"/> on <see cref="SecretSealingKeys.Path"/> —
    /// exactly like <see cref="Unseal"/>.
    /// </summary>
    public IObservable<SealingPublicKey> Rotate(string rotatedBy)
        => AuthorizedThenAsSystem(Permission.Update, "rotate the secret sealing key", () => Read()
                .SelectMany(stored => stored is null
                    ? Create()
                    : Rotated(_ => true)))
            .Do(_ => logger.LogInformation("Secret sealing key rotated by {By}; the outgoing key stays accepted for {Grace}", rotatedBy, SecretSealingKeys.RotationGrace))
            .Select(stored => new SealingPublicKey(stored.CurrentId, stored.CurrentPublicKey));

    /// <summary>
    /// The control-plane shape (Doc/Architecture/AuthorizeAsCallerExecuteAsSystem): check the CALLER's
    /// <paramref name="permission"/> on the key node, resolved at subscribe time from the caller's own context, and
    /// only then run <paramref name="work"/> as System. Denied is <see cref="UnauthorizedAccessException"/>;
    /// undetermined (a fold that faulted or produced no verdict) is <see cref="InvalidOperationException"/>, never
    /// a grant.
    /// </summary>
    private IObservable<T> AuthorizedThenAsSystem<T>(Permission permission, string what, Func<IObservable<T>> work)
        => hub.CheckPermissionOutcome(SecretSealingKeys.Path, permission)
            .Take(1)
            .SelectMany(outcome => outcome.IsGranted
                ? hub.ServiceProvider.GetService<AccessService>().RunAsSystem(work)
                : Observable.Throw<T>(outcome.IsUndetermined
                    ? new InvalidOperationException(
                        $"Could not check whether the caller may {what}: {outcome.UndeterminedReason}")
                    : new UnauthorizedAccessException(
                        $"The caller may not {what}: {permission} on {SecretSealingKeys.Path} is required.")));

    private IObservable<SecretSealingKey> Resolve()
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        // ONE sealed System scope around the whole resolve: the key is in the Admin partition and the
        // caller is an HTTP request that must not be left holding System.
        return access.RunAsSystem(() => Read().SelectMany(stored =>
            stored is null ? Create()
            : IsDue(stored, DateTimeOffset.UtcNow) ? Rotated(there => IsDue(there, DateTimeOffset.UtcNow))
            : Observable.Return(stored)));
    }

    /// <summary>Whether a new key is due at <paramref name="now"/>. Pure.</summary>
    internal static bool IsDue(SecretSealingKey key, DateTimeOffset now) => key.RotateAfter is { } due && now >= due;

    /// <summary>
    /// What a rotation makes of the key as it is at the OWNING hub: when <paramref name="rotate"/> still holds for
    /// it, <paramref name="candidate"/> becomes current and the outgoing key is retired; otherwise it is left exactly
    /// as it is, so a request that read the same overdue key as another ADOPTS the rotation that landed first
    /// instead of replacing it. Pure.
    /// </summary>
    internal static SecretSealingKey RotateAtOwner(SecretSealingKey there, SecretSealingKey candidate,
        Func<SecretSealingKey, bool> rotate, DateTimeOffset now)
        => rotate(there) ? there.RotatedTo(candidate, now, SecretSealingKeys.RotationGrace) : there;

    // 🚨 The due-check and the rotation happen together, inside the update at the owning hub, which serialises
    // them: two requests that both read an overdue K0 can never publish KA and KB, both naming K0 — the second
    // finds KA current (not due) and leaves it. The answer is the key as stored afterwards, never the proposal.
    private IObservable<SecretSealingKey> Rotated(Func<SecretSealingKey, bool> rotate)
    {
        var candidate = NewKey();
        return hub.GetWorkspace().GetMeshNodeStream(SecretSealingKeys.Path)
            .Update(node => node.ContentAs<SecretSealingKey>(hub.JsonSerializerOptions) is { } there
                ? node with { Content = RotateAtOwner(there, candidate, rotate, DateTimeOffset.UtcNow) }
                : node)
            .Take(1)
            .SelectMany(_ => Read())
            .Select(stored => stored ?? throw new InvalidOperationException("The secret sealing key was rotated but could not be read back."));
    }

    private SecretSealingKey NewKey()
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
                RotateAfter = now.Add(SecretSealingKeys.RotationInterval),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private IObservable<SecretSealingKey> Create()
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(SecretSealingKeys.Id, SecretSealingKeys.Namespace)
        {
            Name = "Secret sealing key",
            NodeType = SecretSealingKeys.NodeType,
            State = MeshNodeState.Active,
            Content = NewKey(),
        };
        // 🚨 ALWAYS read back; never return what we proposed. Under a concurrent create both callers are told
        // "created" while storage keeps the FIRST write: the stored node is the only authority, otherwise two
        // replicas end up sealing to different keys.
        return meshService.CreateNodes([node]).Select(_ => 0).SelectMany(_ => Read()).Select(stored =>
            stored ?? throw new InvalidOperationException("The secret sealing key was written but could not be read back."));
    }

    private IObservable<SecretSealingKey?> Read()
        => hub.GetMeshNodeOutcome(SecretSealingKeys.Path, ReadTimeout).Take(1)
            .Select(outcome => KeyFrom(outcome, hub.JsonSerializerOptions));

    /// <summary>
    /// The stored key from a read. 🚨 Null ONLY for <see cref="NodeReadStatus.Absent"/>: that is the one answer that
    /// lets <see cref="Resolve"/> and <see cref="Rotate"/> CREATE a key. A delete in flight or a read that could not be
    /// answered is a fault, never "no key yet", or a transient failure would mint a second key over the stored one.
    /// </summary>
    internal static SecretSealingKey? KeyFrom(NodeReadOutcome outcome, System.Text.Json.JsonSerializerOptions options)
        => outcome.Status switch
        {
            // 🚨 A node that is there but does not read as a key is a fault, not absence: null here would make
            // Rotate/Resolve create a key at a path that already holds a node.
            NodeReadStatus.Present => outcome.Node!.ContentAs<SecretSealingKey>(options)
                ?? throw new InvalidOperationException(
                    $"The node at {SecretSealingKeys.Path} is present but its content is not a {SecretSealingKeys.NodeType}."),
            NodeReadStatus.Absent => null,
            NodeReadStatus.DeleteInProgress => throw new InvalidOperationException(
                $"The secret sealing key at {SecretSealingKeys.Path} is being deleted; refusing to treat it as absent."),
            _ => throw new InvalidOperationException(
                $"The secret sealing key at {SecretSealingKeys.Path} could not be read ({outcome.Status}).", outcome.Failure),
        };

    private static byte[]? Unprotect(IProviderKeyProtector? protector, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var raw = protector is null ? stored : protector.Unprotect(stored);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return Convert.FromBase64String(raw); }
        catch (FormatException) { return null; }
    }
}
