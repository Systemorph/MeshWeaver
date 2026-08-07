using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Mints and validates registration bootstrap keys (<see cref="RegistrationKey"/>, <c>mwr_…</c>) —
/// the registry-side half of first-startup instance auto-registration. Same mint → hash → node +
/// System-written index discipline as instance keys; the raw key is returned once and never stored.
///
/// <para>Minting is exposed only on the admin surface (the Instance grants tab gates on
/// <c>IsGlobalAdmin</c>). The service itself does not re-check — a bootstrap key registers
/// instances OWNED BY ITS MINTER, which is exactly what the minter could already do by hand in
/// Settings ▸ Instances, so a mint is never a privilege escalation; the admin gate is curation,
/// not a security boundary.</para>
/// </summary>
public sealed class RegistrationKeyService(
    IMeshService nodeFactory, IMessageHub hub, ILogger<RegistrationKeyService> logger)
{
    /// <summary>Namespace holding a user's minted bootstrap keys, inside their own partition.</summary>
    public const string KeyNamespace = "RegistrationKey";

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Mints a bootstrap key for <paramref name="userId"/> (the admin whose identity newly
    /// registered instances will carry). Cold — writes happen on Subscribe. The key node lands in
    /// the minter's partition; the routing index is written under System into the shared
    /// instance-credential index namespace.
    /// </summary>
    public IObservable<RegistrationKeyMintResult> Mint(
        string userId, string userName, string userEmail,
        string description = "", DateTimeOffset? expiresAt = null)
    {
        var rawKey = RegistrationKeys.Generate();
        var hash = InstanceKeys.Hash(rawKey);
        var id = InstanceKeys.HashPrefix(hash);

        var key = new RegistrationKey
        {
            KeyHash = hash,
            Description = description,
            OwnerUserId = userId,
            OwnerUserName = userName,
            OwnerUserEmail = userEmail,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
        };

        var keyNode = new MeshNode(id, $"{userId}/{KeyNamespace}")
        {
            Name = string.IsNullOrWhiteSpace(description) ? $"Registration key {id}" : description,
            NodeType = MeshWeaverInstanceNodeType.RegistrationKeyNodeType,
            State = MeshNodeState.Active,
            MainNode = userId,
            Content = key,
        };

        return nodeFactory.CreateNode(keyNode)
            .SelectMany(created => WriteIndex(hash, created.Path)
                .Select(_ =>
                {
                    logger.LogInformation(
                        "Minted registration key {Id} for {UserId} (expires {ExpiresAt})",
                        id, userId, expiresAt?.ToString("u") ?? "never");
                    return new RegistrationKeyMintResult(rawKey, created, key);
                }));
    }

    /// <summary>
    /// Resolves a presented bootstrap key to its record, or <c>null</c> when the key is unknown,
    /// malformed, revoked or expired. Runs as System — this IS the step that authenticates an
    /// anonymous registration call, so there is no identity to read with yet.
    /// </summary>
    public IObservable<ResolvedRegistrationKey?> Resolve(string? rawKey)
    {
        if (!RegistrationKeys.HasKeyShape(rawKey))
            return Observable.Return<ResolvedRegistrationKey?>(null);

        var hash = InstanceKeys.Hash(rawKey!);
        var indexPath = $"{MeshWeaverInstanceNodeType.IndexNamespace}/{InstanceKeys.HashPrefix(hash)}";

        return ReadAsSystem(indexPath)
            .SelectMany(indexNode =>
            {
                var index = Content<RegistrationKeyIndex>(indexNode);
                if (index is null || !InstanceKeys.HashEquals(hash, index.KeyHash))
                    return Observable.Return<ResolvedRegistrationKey?>(null);

                return ReadAsSystem(index.KeyPath)
                    .Select(keyNode =>
                    {
                        var key = Content<RegistrationKey>(keyNode);
                        // Re-check the hash on the key node itself — the index is a routing hint,
                        // the key record is the authority.
                        if (key is null || !InstanceKeys.HashEquals(hash, key.KeyHash))
                            return (ResolvedRegistrationKey?)null;
                        if (!key.IsUsable(DateTimeOffset.UtcNow))
                        {
                            logger.LogWarning(
                                "Registration key {Path} presented but {Reason}",
                                index.KeyPath, key.IsRevoked ? "revoked" : "expired");
                            return null;
                        }
                        return new ResolvedRegistrationKey(key, index.KeyPath);
                    });
            });
    }

    /// <summary>Stamps a successful use (count + timestamp) onto the key record. Runs as System —
    /// the registration caller is anonymous. Cold; subscribe to run.</summary>
    public IObservable<Unit> StampUse(string keyPath)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Defer(() =>
        {
            var disposable = accessService.ImpersonateAsSystem();
            return hub.GetWorkspace().GetMeshNodeStream(keyPath)
                .Update(current => current with
                {
                    Content = Content<RegistrationKey>(current) is { } key
                        ? key with
                        {
                            UsageCount = key.UsageCount + 1,
                            LastUsedAt = DateTimeOffset.UtcNow,
                        }
                        : current.Content,
                })
                .Select(_ => Unit.Default)
                .Finally(() => disposable.Dispose());
        });
    }

    /// <summary>Revokes (or re-enables) a key. Runs under the CALLER's identity — the key node
    /// lives in the minter's partition, and revocation is an owner/admin action, not System's.</summary>
    public IObservable<MeshNode> SetRevoked(string keyPath, bool revoked) =>
        hub.GetWorkspace().GetMeshNodeStream(keyPath)
            .Update(current => current with
            {
                Content = Content<RegistrationKey>(current) is { } key
                    ? key with { IsRevoked = revoked }
                    : current.Content,
            });

    // Same System-scoped index write as MeshWeaverInstanceService.WriteIndex, including the lazy
    // top-level-namespace provisioning (the index namespace is its own Postgres schema and the
    // router does not lazy-create schemas — the 42P01 first-registration failure, 2026-08-06).
    private IObservable<MeshNode> WriteIndex(string hash, string keyPath)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var indexNode = new MeshNode(
            InstanceKeys.HashPrefix(hash), MeshWeaverInstanceNodeType.IndexNamespace)
        {
            Name = "Registration key index",
            NodeType = MeshWeaverInstanceNodeType.RegistrationKeyNodeType,
            State = MeshNodeState.Active,
            Content = new RegistrationKeyIndex { KeyHash = hash, KeyPath = keyPath },
        };

        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToArray();
        var provisioned = providers.Length == 0
            ? Observable.Return(Unit.Default)
            : Observable.Merge(providers.Select(p =>
                    p.EnsurePartitionProvisioned(MeshWeaverInstanceNodeType.IndexNamespace)))
                .DefaultIfEmpty(Unit.Default)
                .LastAsync();

        return provisioned.SelectMany(_ => Observable.Defer(() =>
        {
            var disposable = accessService.ImpersonateAsSystem();
            return nodeFactory.CreateNode(indexNode).Finally(() => disposable.Dispose());
        }));
    }

    private IObservable<MeshNode?> ReadAsSystem(string path)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => hub.GetMeshNode(path, ReadTimeout));
    }

    private T? Content<T>(MeshNode? node) where T : class
    {
        if (node?.Content is null) return null;
        if (node.Content is T typed) return typed;
        if (node.Content is not JsonElement json) return null;
        try { return JsonSerializer.Deserialize<T>(json.GetRawText(), hub.JsonSerializerOptions); }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not read {Type} from node {Path}", typeof(T).Name, node.Path);
            return null;
        }
    }
}

/// <summary>The outcome of minting: <paramref name="RawKey"/> is the ONLY time the bootstrap key is
/// available in the clear — show it now or it is lost.</summary>
public sealed record RegistrationKeyMintResult(string RawKey, MeshNode Node, RegistrationKey Key);

/// <summary>A validated, usable bootstrap key and the node path to stamp usage onto.</summary>
public sealed record ResolvedRegistrationKey(RegistrationKey Key, string KeyPath);
