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
            return AuthenticateToken(authorizationHeader);

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

    /// <summary>
    /// The short-lived-token half of <see cref="Authenticate"/>. A verified token resolves through
    /// exactly the same index as the key it was exchanged from — it carries that key's hash — so
    /// there is one resolution path, and re-issuing an instance key invalidates every outstanding
    /// token because the instance record then holds a different hash.
    ///
    /// <para>🚨 The token contributes IDENTITY and SCOPE, never authority. The live
    /// <see cref="PluginGrant"/> is still read and still decides, so a revoked or expired sync
    /// licence takes effect immediately instead of surviving until the token runs out.</para>
    /// </summary>
    private IObservable<AuthenticatedInstance?> AuthenticateToken(string? authorizationHeader)
    {
        var rawToken = SyncAccessToken.ExtractToken(authorizationHeader);
        if (rawToken is null)
            return Observable.Return<AuthenticatedInstance?>(null);

        var signingKey = SigningKey();
        if (signingKey is null)
        {
            // Configured-off is not "allow": a registry that cannot verify a signature must refuse
            // the token, never accept it unverified.
            logger.LogWarning(
                "A sync access token was presented but {Section}:{Key} is not configured — refusing.",
                PluginCatalogOptions.SectionName, nameof(PluginCatalogOptions.TokenSigningKey));
            return Observable.Return<AuthenticatedInstance?>(null);
        }

        var claims = SyncAccessToken.Verify(rawToken, DateTimeOffset.UtcNow, signingKey);
        if (claims is null)
            return Observable.Return<AuthenticatedInstance?>(null);

        return Resolve(claims.KeyHash)
            .Select(resolved =>
            {
                if (resolved is null)
                    return null;
                // The token names an instance AND routes to one. They must be the same instance, or
                // the token is being replayed against a record it does not describe.
                if (!string.Equals(resolved.Instance.InstanceId, claims.InstanceId, StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Sync access token claims instance {Claimed} but its key resolves to {Actual} — refusing.",
                        claims.InstanceId, resolved.Instance.InstanceId);
                    return null;
                }
                return resolved with { TokenScope = claims };
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Sync access token resolution failed for instance {InstanceId}",
                    claims.InstanceId);
                return Observable.Return<AuthenticatedInstance?>(null);
            });
    }

    /// <summary>The configured HMAC key, or null when it is absent or too short to be usable.</summary>
    private byte[]? SigningKey()
    {
        var configured = hub.ServiceProvider.GetService<PluginCatalogOptions>()?.TokenSigningKey;
        if (string.IsNullOrWhiteSpace(configured))
            return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(configured);
        return SyncAccessToken.IsUsableSigningKey(bytes) ? bytes : null;
    }

    private IObservable<AuthenticatedInstance?> Resolve(string hash)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var indexPath = $"{MeshWeaverInstanceNodeType.IndexNamespace}/{InstanceKeys.HashPrefix(hash)}";

        // 🚨 ONE sealed System scope around the WHOLE resolution, not one Observable.Using per read
        // (#1790). Rx runs a Using factory on the SUBSCRIBING thread and disposes on termination, so
        // the old per-read form left the caller latched as System — and the callers here are HTTP
        // requests on the registry surface (/api/plugins, and now /api/instances/token), which would
        // then continue with Permission.All. RunAsSystem enters at Subscribe, leaves on the way out
        // of that same Subscribe, and delivers every notification under the subscriber's own
        // identity, so the three reads below are System and nothing downstream inherits it.
        return accessService.RunAsSystem(() => Read(indexPath)
            .SelectMany(indexNode =>
            {
                var index = Content<MeshWeaverInstanceIndex>(indexNode);
                if (index is null || !InstanceKeys.HashEquals(hash, index.KeyHash))
                    return Observable.Return<AuthenticatedInstance?>(null);

                return Read(index.InstancePath)
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

                        return Read(MeshWeaverInstanceNodeType.GrantPath(instance.InstanceId))
                            // No grant node at all is the NORMAL state for a freshly registered
                            // instance — it authenticates, and is entitled to nothing.
                            .Select(grantNode => (AuthenticatedInstance?)new AuthenticatedInstance(
                                instance,
                                Content<PluginGrant>(grantNode) ?? new PluginGrant { InstanceId = instance.InstanceId }));
                    });
            }));
    }

    /// <summary>One-shot read by exact path. The System identity comes from the single
    /// <see cref="ImpersonationScopeExtensions.RunAsSystem{T}"/> scope in <see cref="Resolve"/>, so
    /// this composes inside it rather than opening a scope of its own.</summary>
    private IObservable<MeshNode?> Read(string path) => hub.GetMeshNode(path, ReadTimeout);

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
    /// <summary>
    /// Present when the caller authenticated with a short-lived token rather than its durable key.
    /// A token can only NARROW what the grant already allows — never widen it — so this is an
    /// additional filter, never an alternative source of authority.
    /// </summary>
    public SyncAccessTokenClaims? TokenScope { get; init; }

    /// <summary>Whether this caller may pull <paramref name="packageId"/> from registry source
    /// <paramref name="sourceName"/> at <paramref name="now"/> — granted, still within the
    /// licence's term, and within the presented token's scope if one was used.</summary>
    public bool Allows(string sourceName, string packageId, DateTimeOffset now) =>
        Grant.Allows(sourceName, packageId, now)
        && (TokenScope is null || TokenScope.Covers(sourceName, packageId));

    /// <summary>Whether this caller may pull <paramref name="packageId"/> from registry source
    /// <paramref name="sourceName"/> right now — what a live request means.</summary>
    public bool Allows(string sourceName, string packageId) =>
        Allows(sourceName, packageId, DateTimeOffset.UtcNow);
}
