using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Turns an inbound <c>Authorization: Bearer mwi_…</c> into the registered
/// <see cref="MeshWeaverInstance"/> it belongs to, together with the admin-owned
/// <see cref="PluginGrant"/> that says what that instance may pull.
///
/// <para>Resolution mirrors personal-token validation: hash the presented key, route by its hash
/// prefix to <c>MeshWeaverInstance/{prefix}</c>, follow the index to the instance node in its
/// owner's partition, then read the grant from the <b>Admin</b> partition. Every read runs as
/// System — this IS the step that turns an anonymous caller into a known instance, so by definition
/// there is no identity to read with yet. The hash comparison below is the authentication.</para>
///
/// <para>Registered as a mesh-scoped singleton so its cache dies with the mesh (never a static
/// collection). The cache is short-lived and keyed by hash: an instance disabled or re-granted takes
/// effect within <see cref="CacheDuration"/> without a restart.</para>
/// </summary>
public sealed class InstanceRegistryAuthenticator(IMessageHub hub, ILogger<InstanceRegistryAuthenticator> logger)
{
    /// <summary>How long a resolved instance + grant is reused before re-reading the mesh.
    /// Short on purpose: a revoked grant must stop working quickly, and the registry is not a
    /// hot path (a consumer polls its catalog, it does not stream).</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, AuthenticatedInstance? Result)> cache = new();

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Resolves the caller. Emits the authenticated instance, or <c>null</c> when the header carries
    /// no instance key, the key is unknown, its hash does not match, or the instance is disabled.
    /// Never throws for an unauthenticated caller — a failure to authenticate is a <c>null</c>, and
    /// the endpoint turns that into a 401.
    /// </summary>
    public IObservable<AuthenticatedInstance?> Authenticate(string? authorizationHeader)
    {
        var rawKey = InstanceKeys.ExtractKey(authorizationHeader);
        if (rawKey is null)
            return Observable.Return<AuthenticatedInstance?>(null);

        var hash = InstanceKeys.Hash(rawKey);
        if (cache.TryGetValue(hash, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheDuration)
            return Observable.Return(hit.Result);

        return Resolve(hash)
            .Do(result => cache[hash] = (DateTimeOffset.UtcNow, result))
            .Catch((Exception ex) =>
            {
                // A read failure is NOT an authentication success. Surface null (→ 401) and log;
                // never fall through to "allow" because the mesh was briefly unreachable.
                logger.LogWarning(ex, "Instance key resolution failed for hash prefix {Prefix}",
                    InstanceKeys.HashPrefix(hash));
                return Observable.Return<AuthenticatedInstance?>(null);
            });
    }

    private IObservable<AuthenticatedInstance?> Resolve(string hash)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var indexPath = $"{MeshWeaverInstanceNodeType.IndexNamespace}/{InstanceKeys.HashPrefix(hash)}";

        return ReadAsSystem(accessService, indexPath)
            .SelectMany(indexNode =>
            {
                var index = Content<MeshWeaverInstanceIndex>(indexNode);
                if (index is null || !InstanceKeys.HashEquals(hash, index.KeyHash))
                    return Observable.Return<AuthenticatedInstance?>(null);

                return ReadAsSystem(accessService, index.InstancePath)
                    .SelectMany(instanceNode =>
                    {
                        var instance = Content<MeshWeaverInstance>(instanceNode);
                        // Re-check the hash on the instance itself: the index is a routing hint,
                        // the instance record is the authority. A stale index must not authenticate.
                        if (instance is null || !InstanceKeys.HashEquals(hash, instance.KeyHash))
                            return Observable.Return<AuthenticatedInstance?>(null);
                        if (instance.IsDisabled)
                        {
                            logger.LogWarning("Instance {InstanceId} presented a valid key but is disabled",
                                instance.InstanceId);
                            return Observable.Return<AuthenticatedInstance?>(null);
                        }

                        return ReadAsSystem(accessService,
                                MeshWeaverInstanceNodeType.GrantPath(instance.InstanceId))
                            // No grant node at all is the NORMAL state for a freshly registered
                            // instance — it authenticates, and is entitled to nothing.
                            .Select(grantNode => (AuthenticatedInstance?)new AuthenticatedInstance(
                                instance,
                                Content<PluginGrant>(grantNode) ?? new PluginGrant { InstanceId = instance.InstanceId }));
                    });
            });
    }

    private IObservable<MeshNode?> ReadAsSystem(AccessService accessService, string path) =>
        Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => hub.GetMeshNode(path, ReadTimeout));

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

/// <summary>An authenticated caller: which instance presented the key, and what it may pull.</summary>
/// <param name="Instance">The registered instance the key belongs to.</param>
/// <param name="Grant">Its grant. Never null — an instance with no grant node carries an empty
/// grant, which authorizes nothing.</param>
public sealed record AuthenticatedInstance(MeshWeaverInstance Instance, PluginGrant Grant)
{
    /// <summary>Whether this caller may pull <paramref name="packageId"/> from registry source
    /// <paramref name="sourceName"/>.</summary>
    public bool Allows(string sourceName, string packageId) => Grant.Allows(sourceName, packageId);
}
