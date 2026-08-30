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

    /// <summary>
    /// How long a DEFINITIVE "this key is unknown" is reused — far shorter than a positive.
    ///
    /// <para>The two are not symmetric. A positive answer costs a minute of staleness on a
    /// revocation, which is the trade <see cref="CacheDuration"/> makes deliberately. A negative
    /// costs a minute of lockout to an instance that has just been registered or re-keyed, for no
    /// benefit at all: nobody polls a key that does not work. Five seconds still absorbs a burst of
    /// unauthenticated requests without turning a registration into a coffee break.</para>
    /// </summary>
    public static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromSeconds(5);

    /// <summary>Retry-After (seconds) an endpoint advertises when resolution was UNAVAILABLE.
    /// Matches the API-token leg's <c>ApiTokenAuthenticationHandler.RetryAfterSeconds</c>.</summary>
    public const int RetryAfterSeconds = 5;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, AuthenticatedInstance? Result)> cache = new();

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Forgets the cached verdict for the instance whose key hashes to <paramref name="keyHash"/>.
    /// Called by the writes that change what a verdict says — a plan promotion, a disable — so the
    /// change is visible on the NEXT request served by this process rather than after
    /// <see cref="CacheDuration"/>; other replicas see it within the window. Not a test hook: a
    /// promotion an admin just made and a fetch that still refuses is the exact confusion #2804
    /// exists to remove.
    /// </summary>
    public void Invalidate(string keyHash)
    {
        if (!string.IsNullOrWhiteSpace(keyHash))
            cache.TryRemove(keyHash, out _);
    }

    /// <summary>
    /// The node read, injectable so the CACHE and CLASSIFICATION rules can be driven without a
    /// mesh. Production is <see cref="IMessageHub"/>'s interrogable one-shot read; a test supplies
    /// an unavailable/absent/present sequence directly.
    /// </summary>
    internal Func<string, IObservable<NodeReadOutcome>>? ReadOverride { get; init; }

    /// <summary>
    /// Resolves the caller. Emits the authenticated instance, or <c>null</c> when the header carries
    /// no instance key, the key is unknown, its hash does not match, or the instance is disabled.
    /// Never throws for an unauthenticated caller — a failure to authenticate is a <c>null</c>, and
    /// the endpoint turns that into a 401.
    ///
    /// <para>🚨 A <c>null</c> here still conflates "unknown key" with "could not find out", which is
    /// why every endpoint should call <see cref="AuthenticateOutcome"/> instead. This overload is
    /// kept for callers that genuinely only need the instance.</para>
    /// </summary>
    public IObservable<AuthenticatedInstance?> Authenticate(string? authorizationHeader) =>
        AuthenticateOutcome(authorizationHeader).Select(outcome => outcome.Instance);

    /// <summary>
    /// Resolves the caller, keeping "this key is unknown" apart from "I could not find out".
    ///
    /// <para>🚨 THE DISTINCTION IS THE POINT (#2695). Three point reads stand between a bearer key
    /// and its instance, and a transient failure of ANY of them used to become a <c>null</c> — which
    /// the endpoint renders as <c>401 "A registered instance key is required"</c>, and which was then
    /// <b>cached for a full minute</b>. One slow read on one pod therefore made a perfectly valid key
    /// unknown to that pod for sixty seconds, and the 401 steered its owner at the wrong fix: a CI
    /// guard read it as "this instance needs a whole-source grant" while the grant was present and
    /// unchanged (MeshWeaver.Crm run 33269921011, both jobs, passing minutes later with no change
    /// anywhere).</para>
    ///
    /// <para>So: an <see cref="NodeReadStatus.Unavailable"/> on any leg yields
    /// <see cref="InstanceAuthResult.Unavailable"/>, and <b>is never cached</b> — a fault must not
    /// become a fact. Only a read that reached a verdict is remembered. This is the same two-shape
    /// outcome the identity side adopted for #637; the instance-key leg simply never got it.</para>
    /// </summary>
    public IObservable<InstanceAuthResult> AuthenticateOutcome(string? authorizationHeader)
    {
        var rawKey = InstanceKeys.ExtractKey(authorizationHeader);
        if (rawKey is null)
            return AuthenticateToken(authorizationHeader);

        var hash = InstanceKeys.Hash(rawKey);
        if (cache.TryGetValue(hash, out var hit)
            && DateTimeOffset.UtcNow - hit.At < (hit.Result is null ? NegativeCacheDuration : CacheDuration))
            return Observable.Return(InstanceAuthResult.Resolved(hit.Result));

        return Resolve(hash)
            .Do(result =>
            {
                // Only a VERDICT is cached. Caching the unavailable branch is the whole defect.
                if (!result.IsUnavailable)
                    cache[hash] = (DateTimeOffset.UtcNow, result.Instance);
            })
            .Catch((Exception ex) =>
            {
                // A read failure is NOT an authentication success — and it is not a denial either.
                // It is unavailability: uncached, and surfaced as such so the caller retries instead
                // of being told its key is unknown.
                logger.LogWarning(ex, "Instance key resolution UNAVAILABLE for hash prefix {Prefix} "
                    + "— reporting retryable, NOT 'unknown key'", InstanceKeys.HashPrefix(hash));
                return Observable.Return(InstanceAuthResult.Unavailable(ex.Message));
            });
    }

    /// <summary>
    /// The short-lived-token half of <see cref="AuthenticateOutcome"/>. A verified token resolves
    /// through exactly the same index as the key it was exchanged from — it carries that key's hash
    /// — so there is one resolution path, and re-issuing an instance key invalidates every
    /// outstanding token because the instance record then holds a different hash.
    ///
    /// <para>🚨 The token contributes IDENTITY and SCOPE, never authority. The live
    /// <see cref="PluginGrant"/> is still read and still decides, so a revoked or expired sync
    /// licence takes effect immediately instead of surviving until the token runs out.</para>
    /// </summary>
    private IObservable<InstanceAuthResult> AuthenticateToken(string? authorizationHeader)
    {
        var rawToken = SyncAccessToken.ExtractToken(authorizationHeader);
        if (rawToken is null)
            return Observable.Return(InstanceAuthResult.Resolved(null));

        var keys = hub.ServiceProvider.GetService<SyncTokenSigningKeyService>();
        if (keys is null)
        {
            // No key service is not "allow": a registry that cannot verify a signature must refuse
            // the token, never accept it unverified. This is a CONFIGURATION verdict, not a
            // transient one — retrying will not register the service — so it stays a denial.
            logger.LogWarning(
                "A sync access token was presented but no {Service} is registered — refusing.",
                nameof(SyncTokenSigningKeyService));
            return Observable.Return(InstanceAuthResult.Resolved(null));
        }

        // Existing(), never Resolve(): this caller has not authenticated yet, so minting on their
        // behalf would let an anonymous request write a node — and would be pointless anyway, since a
        // token cannot verify against a key minted after it was signed.
        return keys.Existing()
            .SelectMany(material =>
            {
                // Verification tries the current key and then the one the last rotation retired, so a
                // token minted moments before a rotation still works.
                var claims = material?.Verify(rawToken, DateTimeOffset.UtcNow);
                if (claims is null)
                    return Observable.Return(InstanceAuthResult.Resolved(null));

                return Resolve(claims.KeyHash)
                    .Select(outcome =>
                    {
                        if (outcome.IsUnavailable || outcome.Instance is null)
                            return outcome;
                        // The token names an instance AND routes to one. They must be the same
                        // instance, or it is being replayed against a record it does not describe.
                        if (!string.Equals(
                                outcome.Instance.Instance.InstanceId, claims.InstanceId, StringComparison.Ordinal))
                        {
                            logger.LogWarning(
                                "Sync access token claims instance {Claimed} but its key resolves to "
                                + "{Actual} — refusing.",
                                claims.InstanceId, outcome.Instance.Instance.InstanceId);
                            return InstanceAuthResult.Resolved(null);
                        }
                        return InstanceAuthResult.Resolved(outcome.Instance with { TokenScope = claims });
                    });
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex, "Sync access token resolution UNAVAILABLE");
                return Observable.Return(InstanceAuthResult.Unavailable(ex.Message));
            });
    }

    private IObservable<InstanceAuthResult> Resolve(string hash)
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
            .SelectMany(indexRead =>
            {
                if (Unavailable(indexRead, indexPath) is { } unavailable)
                    return Observable.Return(unavailable);

                var index = Content<MeshWeaverInstanceIndex>(indexRead.Node);
                if (index is null || !InstanceKeys.HashEquals(hash, index.KeyHash))
                    return Observable.Return(InstanceAuthResult.Resolved(null));

                return Read(index.InstancePath)
                    .SelectMany(instanceRead =>
                    {
                        if (Unavailable(instanceRead, index.InstancePath) is { } instanceUnavailable)
                            return Observable.Return(instanceUnavailable);

                        var instance = Content<MeshWeaverInstance>(instanceRead.Node);
                        // Re-check the hash on the instance itself: the index is a routing hint,
                        // the instance record is the authority. A stale index must not authenticate.
                        if (instance is null || !InstanceKeys.HashEquals(hash, instance.KeyHash))
                            return Observable.Return(InstanceAuthResult.Resolved(null));
                        if (instance.IsDisabled)
                        {
                            logger.LogWarning("Instance {InstanceId} presented a valid key but is disabled",
                                instance.InstanceId);
                            return Observable.Return(InstanceAuthResult.Resolved(null));
                        }

                        var grantPath = MeshWeaverInstanceNodeType.GrantPath(instance.InstanceId);
                        return Read(grantPath)
                            .Select(grantRead =>
                            {
                                // 🚨 The grant leg needs the SAME distinction, and it is the easiest
                                // one to miss: no grant node at all is the NORMAL state for a freshly
                                // registered instance (it authenticates, and is entitled to nothing),
                                // whereas an UNREADABLE grant would silently authenticate the caller
                                // and then refuse every package it asked for — a 403 nobody can act
                                // on, cached for a minute.
                                if (Unavailable(grantRead, grantPath) is { } grantUnavailable)
                                    return grantUnavailable;
                                return InstanceAuthResult.Resolved(new AuthenticatedInstance(
                                    instance,
                                    Content<PluginGrant>(grantRead.Node)
                                    ?? new PluginGrant { InstanceId = instance.InstanceId }));
                            })
                            // The plan ladder rides on the caller: a plan-scoped grant entry is
                            // decided against it at every surface, and reading it HERE — inside the
                            // same System scope, cached with the same lifetime as the verdict — is
                            // what keeps the endpoints from each resolving a ladder of their own.
                            // A ladder read that fails yields Empty: plan-scoped entries then license
                            // nothing (fail closed), plan-less entries are untouched, and the
                            // verdict itself stays a verdict.
                            .SelectMany(result => result.Instance is null
                                ? Observable.Return(result)
                                : Ladder().Select(ranks =>
                                    InstanceAuthResult.Resolved(result.Instance with { Ranks = ranks })));
                    });
            }));
    }

    /// <summary>The registry's plan ladder, or <see cref="PlanTierRanks.Empty"/> on a host that
    /// registers no <see cref="PlanTierLadder"/>. Composed inside the resolution's System scope.</summary>
    private IObservable<PlanTierRanks> Ladder() =>
        hub.ServiceProvider.GetService<PlanTierLadder>()?.Read()
        ?? Observable.Return(PlanTierRanks.Empty);

    /// <summary>The unavailable result for a read that reached no verdict, or null when it did.
    /// <see cref="NodeReadStatus.DeleteInProgress"/> counts as a verdict — the record is going away,
    /// so "unknown key" is the right answer and retrying would only delay it.</summary>
    private InstanceAuthResult? Unavailable(NodeReadOutcome read, string path)
    {
        if (read.Status != NodeReadStatus.Unavailable)
            return null;
        var reason = read.Failure?.Message ?? "the read reached no verdict";
        logger.LogWarning(
            "Instance key resolution: {Path} was UNAVAILABLE ({Reason}) — answering retryable, "
            + "never 'unknown key', and caching nothing", path, reason);
        return InstanceAuthResult.Unavailable($"{path}: {reason}");
    }

    /// <summary>
    /// One-shot INTERROGABLE read by exact path. The System identity comes from the single
    /// <see cref="ImpersonationScopeExtensions.RunAsSystem{T}"/> scope in <see cref="Resolve"/>, so
    /// this composes inside it rather than opening a scope of its own.
    ///
    /// <para>🚨 <c>GetMeshNodeOutcome</c>, not <c>GetMeshNode</c>: the convenience read maps every
    /// non-Present outcome to the same <c>null</c>, and "absent" versus "I could not read it" is the
    /// entire question this authenticator has to answer. <see cref="ReadTimeoutBehavior.EmitNull"/>
    /// keeps a budget overrun on the outcome channel as <see cref="NodeReadStatus.Unavailable"/>
    /// rather than throwing it into the caller's catch-all.</para>
    /// </summary>
    private IObservable<NodeReadOutcome> Read(string path) =>
        ReadOverride is { } test
            ? test(path)
            : hub.GetMeshNodeOutcome(path, ReadTimeout, ReadTimeoutBehavior.EmitNull);

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

/// <summary>
/// The outcome of resolving a presented instance key — the seam that keeps <b>"this key is
/// unknown"</b> apart from <b>"I could not find out"</b> (#2695).
///
/// <para>Exactly two shapes, deliberately mirroring the identity side's
/// <c>IdentityReadOutcome&lt;T&gt;</c> (#637), because it is the same mistake in a different leg:
/// <list type="bullet">
///   <item><description><b>Resolved</b> — the resolution reached a verdict.
///     <see cref="Instance"/> <c>null</c> here is a DEFINITIVE negative: no such key, hash
///     mismatch, or the instance is disabled. The endpoint answers <c>401</c>, and the result may
///     be cached.</description></item>
///   <item><description><b>Unavailable</b> — one of the three reads reached no verdict. NOTHING was
///     established about the key, so the endpoint answers <c>503</c> + <c>Retry-After</c> and the
///     result is NEVER cached.</description></item>
/// </list></para>
/// </summary>
public sealed record InstanceAuthResult
{
    /// <summary>The authenticated caller, or <c>null</c> — which is a definitive negative when
    /// resolved, and carries no meaning at all when <see cref="IsUnavailable"/>.</summary>
    public AuthenticatedInstance? Instance { get; init; }

    /// <summary>Why no verdict was reached, or <c>null</c> when one was.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>True when resolution reached NO verdict — retryable, never "unknown key".</summary>
    public bool IsUnavailable => UnavailableReason is not null;

    /// <summary>The resolution completed; <paramref name="instance"/> is the answer (possibly a
    /// definitive <c>null</c>).</summary>
    public static InstanceAuthResult Resolved(AuthenticatedInstance? instance) =>
        new() { Instance = instance };

    /// <summary>The resolution reached no verdict — answer retryable, and cache nothing.</summary>
    public static InstanceAuthResult Unavailable(string reason) =>
        new() { UnavailableReason = reason };
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

    /// <summary>
    /// The registry's plan ladder at resolution time — what a plan-scoped grant entry is decided
    /// against (<see cref="PlanTierRanks.Covers"/>). <see cref="PlanTierRanks.Empty"/> on a host
    /// with no tier nodes or when the read failed: plan-scoped entries then license nothing.
    /// </summary>
    public PlanTierRanks Ranks { get; init; } = PlanTierRanks.Empty;

    /// <summary>Whether this caller may pull <paramref name="packageId"/> from registry source
    /// <paramref name="sourceName"/> at <paramref name="now"/> WITHOUT knowing the package's tier —
    /// only a plan-less grant entry can say yes (see <see cref="PluginGrant.Allows(string,string,DateTimeOffset)"/>),
    /// and the presented token's scope must cover it if one was used.</summary>
    public bool Allows(string sourceName, string packageId, DateTimeOffset now) =>
        Grant.Allows(sourceName, packageId, now)
        && (TokenScope is null || TokenScope.Covers(sourceName, packageId));

    /// <summary>Whether this caller may pull <paramref name="packageId"/> from registry source
    /// <paramref name="sourceName"/> right now — what a live request means.</summary>
    public bool Allows(string sourceName, string packageId) =>
        Allows(sourceName, packageId, DateTimeOffset.UtcNow);

    /// <summary>Whether this caller may pull <paramref name="packageId"/> — declaring
    /// <paramref name="packageTier"/> — from registry source <paramref name="sourceName"/> at
    /// <paramref name="now"/>: granted by an entry within term, covered by the INSTANCE's plan
    /// (<see cref="MeshWeaverInstance.Plan"/>, baseline when absent) under <see cref="Ranks"/>,
    /// and within the token's scope. The overload every registry surface decides with (#2804).</summary>
    public bool Allows(string sourceName, string packageId, string? packageTier, DateTimeOffset now) =>
        Grant.Allows(sourceName, packageId, packageTier, Ranks, Instance.Plan, now)
        && (TokenScope is null || TokenScope.Covers(sourceName, packageId));

    /// <summary><see cref="Allows(string,string,string?,DateTimeOffset)"/> right now.</summary>
    public bool Allows(string sourceName, string packageId, string? packageTier) =>
        Allows(sourceName, packageId, packageTier, DateTimeOffset.UtcNow);
}
