using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
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
/// <para>🚨 <b>Every read is a LIVE stream, never a per-request round-trip (#3119).</b> The three
/// legs used to be one-shot <c>GetDataRequest</c>s to the owning per-node hubs, ten seconds each,
/// memoised for a minute per key. On a registry pod under cross-schema fan-out load the owner did
/// not answer inside ten seconds, so once a minute EVERY consumer of that key — every satellite CI
/// gate and every bake — was told <c>503 Instance-key resolution is temporarily unavailable</c>;
/// the Reinsurance publish-bake failed three times in one day on it. Now each leg reads through the
/// process-wide <c>IMeshNodeStreamCache</c>: a children LISTING of the parent namespace decides
/// existence (empty-on-absent, so an unknown key never opens a point read on a path that does not
/// exist) and the owner's mirror supplies the content. Both are hydrated once per process and kept
/// current by the change feed, so only the FIRST request for an instance waits on an owner, every
/// later one reads memory, and a disable or a plan change is seen by the very next request on every
/// replica — no TTL, no verdict cache, nothing to invalidate. Design page:
/// <c>Doc/Architecture/InstanceKeyResolution</c>.</para>
///
/// <para>🚨 <b>…and the mirrors it reads are HELD, so "hydrated once per process" is true
/// (MeshWeaver#7354).</b> A read-cache entry with no live subscriber is released by the cache's
/// idle sweep after <c>MeshNodeStreamCacheOptions.ReadStreamIdleExpiration</c> (ten minutes), and
/// every leg here is a <c>Take(1)</c> — so the instance and grant mirrors were dropped between two
/// requests further apart than that window. A consumer's registry safety net polls every thirty
/// minutes, so EVERY poll found them cold and paid a fresh owner subscription — the per-request
/// owner round-trip #3119 removed, back by way of the idle sweep, and bounded by the same ten-second
/// budget (the 10.09 s <c>503</c> on the consumer side). The authenticator therefore keeps ONE
/// standing subscription on each mirror it has found present (<see cref="Hold"/>): an instance
/// field on this mesh-scoped singleton, released when the node goes away (the mirror completes),
/// when the mirror faults (the next request re-opens and re-holds it), and when the mesh disposes
/// this service. Its size is bounded by the registered instances that have authenticated — never
/// by the keys presented, because an unknown key is refused by the listing before any mirror is
/// opened.</para>
///
/// <para>Registered as a mesh-scoped singleton; the held mirrors are its only state.</para>
/// </summary>
public sealed class InstanceRegistryAuthenticator(IMessageHub hub, ILogger<InstanceRegistryAuthenticator> logger)
    : IDisposable
{
    // 🚨 One standing subscription per mirror this resolution reads (MeshWeaver#7354), so the
    // cache's idle sweep never releases it between two polls. Instance state on a mesh-scoped
    // singleton (NoStaticState); a slot is removed pair-exact when its mirror terminates.
    private readonly ConcurrentDictionary<string, HoldSlot> holds = new(StringComparer.Ordinal);
    // Holds subscribed but not yet published (no first frame yet) — OWNED here too, so a Dispose
    // releases them and nothing a hold opens is ever out of reach.
    private readonly ConcurrentDictionary<HoldSlot, string> pending = new();
    private long holdSequence;
    private int disposed;

    /// <summary>One hold: its subscription, and the order in which its mirror delivered its FIRST
    /// frame — the moment the cache entry it attached to answered, which is what decides whether it
    /// can be on an older mirror than a hold already published (see <see cref="Publish"/>).</summary>
    private sealed class HoldSlot
    {
        private long sequence;

        public SingleAssignmentDisposable Subscription { get; } = new();

        /// <summary>The first-frame order, or 0 before the first frame.</summary>
        public long Sequence => Interlocked.Read(ref sequence);

        /// <summary>Stamps the first-frame order; false when a frame was already stamped.</summary>
        public bool TryStamp(Func<long> next)
        {
            if (Interlocked.Read(ref sequence) != 0)
                return false;
            return Interlocked.CompareExchange(ref sequence, next(), 0) == 0;
        }
    }

    /// <summary>Retry-After (seconds) an endpoint advertises when resolution was UNAVAILABLE.
    /// Matches the API-token leg's <c>ApiTokenAuthenticationHandler.RetryAfterSeconds</c>.</summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>
    /// How long one leg may wait for the FIRST frame of its live listing or mirror before the request
    /// is answered <see cref="InstanceAuthResult.Unavailable"/>. The SAME ten seconds the one-shot
    /// read had — deliberately not widened, #3119 was never a budget problem — but it now bounds only
    /// a cold hydration: the entry keeps hydrating after this subscriber gives up, so the retry the
    /// 503 asks for finds the frame in memory. It is ALSO the deadline of the three node legs
    /// together (#7484, see <see cref="WithinBudget"/>), so a key resolution answers within it
    /// rather than within three of it. Init-only for tests that pin the classification without
    /// waiting ten seconds.
    /// </summary>
    internal TimeSpan ReadBudget { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The scheduler the whole-resolution deadline (<see cref="ReadBudget"/> over all three node legs,
    /// #7484) runs on. Init-only so a test drives the deadline on virtual time.
    /// </summary>
    internal IScheduler DeadlineScheduler { get; init; } = Scheduler.Default;

    private InstanceKeyResolutionTally? outcomes;

    /// <summary>
    /// What every instance-key resolution on this replica answered: resolved, refused or
    /// unavailable, with the time of the newest of each (MeshWeaver#7721). Read by
    /// <see cref="InstanceKeyExchangeHealthCheck"/>. Timestamps come from
    /// <see cref="DeadlineScheduler"/>, so a test on virtual time reads virtual timestamps.
    /// </summary>
    public InstanceKeyResolutionTally Outcomes =>
        LazyInitializer.EnsureInitialized(ref outcomes, () => new InstanceKeyResolutionTally(() => DeadlineScheduler.Now));

    /// <summary>
    /// Retained for source compatibility with callers compiled outside this repository (in-mesh
    /// control planes cannot be swept from here). Since #3119 there is NO verdict to forget: every
    /// request reads the index, instance and grant nodes off their live mirrors, so a plan promotion
    /// or a disable written through <c>GetMeshNodeStream(path).Update(...)</c> is visible to the next
    /// request on EVERY replica the moment the change feed delivers it — the cross-replica lag #2804
    /// worked around no longer exists. This method therefore does nothing.
    /// </summary>
    /// <param name="keyHash">Ignored.</param>
    public void Invalidate(string keyHash)
    {
        // Intentionally empty — see the summary. There is no cache behind this class any more.
    }

    /// <summary>
    /// The node read, injectable so the CLASSIFICATION rules can be driven without a mesh.
    /// Production is the live listing-then-mirror read in <see cref="ReadLive"/>; a test supplies
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
    /// <see cref="InstanceAuthResult.Unavailable"/> — a fault must not become a fact. Since #3119
    /// nothing is remembered at all, verdicts included: every request re-derives its answer from the
    /// live listings and mirrors, which costs a few in-memory lookups and means a revocation is seen
    /// by the next request rather than after a cache window. This is the same two-shape outcome the
    /// identity side adopted for #637; the instance-key leg simply never got it.</para>
    /// </summary>
    public IObservable<InstanceAuthResult> AuthenticateOutcome(string? authorizationHeader)
    {
        var rawKey = InstanceKeys.ExtractKey(authorizationHeader);
        if (rawKey is null)
            return AuthenticateToken(authorizationHeader);

        var hash = InstanceKeys.Hash(rawKey);
        return Resolve(hash)
            .Catch((Exception ex) =>
            {
                // A read failure is NOT an authentication success — and it is not a denial either.
                // It is unavailability, surfaced as such so the caller retries instead of being told
                // its key is unknown.
                logger.LogWarning(ex, "Instance key resolution UNAVAILABLE for hash prefix {Prefix} "
                    + "— reporting retryable, NOT 'unknown key'", InstanceKeys.HashPrefix(hash));
                return Observable.Return(InstanceAuthResult.Unavailable(ex.Message));
            })
            // Counted AFTER the catch, so a faulted read is counted as what the caller is told
            // (unavailable), and once per answer the caller receives.
            .Do(Outcomes.Record);
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

        // 🚨 ONE verifier, TWO issuers, a trust node per issuer (#2483). The fork is on the token's
        // `iss`, read UNVERIFIED — it can only send a forged token to a verifier that will reject
        // it, and it grants nothing on its own. Everything below this line is unchanged: the HMAC
        // leg still never honours a token's own `alg`, and the GitHub leg never sees the HMAC key.
        if (GitHubActionsToken.IsGitHubIssued(rawToken))
            return AuthenticateBuild(rawToken);

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

    /// <summary>
    /// The BUILD-PRINCIPAL leg (#2483): a GitHub Actions OIDC token, verified against GitHub's own
    /// JWKS and then resolved to the <see cref="BuildPrincipal"/> node that says whether this mesh
    /// trusts that repository at all.
    ///
    /// <para>🚨 <b>A verified signature is not an authorization.</b> Every workflow run on GitHub
    /// carries a token signed by these same keys, so the signature establishes only WHICH repository
    /// asked. No <see cref="BuildPrincipal"/> node ⇒ no caller ⇒ 401. This is the same shape as the
    /// instance leg — the token is identity, the admin-owned node is authority — and it is why the
    /// repository claim is re-compared against the record the path routed to.</para>
    ///
    /// <para><b>The NODE is read on every request, from its OWNER — only the JWKS is cached.</b>
    /// That is what makes a <see cref="BuildPrincipalActions.Revoke"/> take effect from the moment it
    /// commits. A verdict cache would hold a revoked principal until it expired, and so would the
    /// process-wide mirror the instance leg reads: it advances on the owner's commit ECHO, which the
    /// revoke's acknowledgement does not wait for, so a request between the two was authenticated on
    /// the pre-revoke record (a full-run failure of
    /// <c>BuildPrincipalAuthenticationTest.ARevokedPrincipal_StopsAuthenticatingAtOnce</c>; on another
    /// replica the window is as long as the cross-process feed is late). A build asks a handful of
    /// times per run, so the owner round-trip costs nothing that matters and closes the window. See
    /// <see cref="ReadBuildPrincipal"/>.</para>
    ///
    /// <para>🚨 <b>Three outcomes, not two.</b> A key set that cannot be read is
    /// <see cref="InstanceAuthResult.Unavailable"/> — a retryable 503, never "unknown token" and
    /// never an admission. Collapsing an unreachable check into a boolean is the defect core #2901
    /// exists to name; on an authentication path it is the difference between a build that retries
    /// and a build that is told its perfectly good identity is unknown.</para>
    /// </summary>
    private IObservable<InstanceAuthResult> AuthenticateBuild(string rawToken)
    {
        var now = DateTimeOffset.UtcNow;
        var audiences = BuildPrincipalConfiguration.Audiences(
            hub.ServiceProvider.GetService<IConfiguration>());
        if (audiences.Count == 0)
        {
            // A CONFIGURATION verdict, not a transient one — retrying will not configure an
            // audience — so it stays a denial. Accepting any audience here would trust every
            // GitHub Actions run that ever pointed a token at anything.
            logger.LogWarning(
                "A GitHub Actions token was presented but no build-principal audience is configured "
                + "({Key}) — refusing.", BuildPrincipalConfiguration.AudienceConfigKey);
            return Observable.Return(InstanceAuthResult.Resolved(null));
        }

        var keyService = hub.ServiceProvider.GetService<GitHubOidcKeyService>();
        if (keyService is null)
        {
            // Same rule as the sync-token leg: a registry that cannot verify a signature must refuse
            // the token, never accept it unverified.
            logger.LogWarning(
                "A GitHub Actions token was presented but no {Service} is registered — refusing.",
                nameof(GitHubOidcKeyService));
            return Observable.Return(InstanceAuthResult.Resolved(null));
        }

        return keyService.Keys(now)
            .SelectMany(keys =>
            {
                var verified = GitHubActionsToken.Verify(rawToken, now, audiences, keys.ByKeyId);
                if (!verified.KeyUnknown)
                    return BuildOutcome(verified, keys, keys, now);

                // An unknown `kid` is the signal of a GitHub key rotation, so re-read ONCE. The
                // service's own floor decides whether that re-read actually happens; when it does
                // not, the set comes back unchanged and the verdict below stays UNDETERMINED rather
                // than becoming a denial we did not earn.
                return keyService.Refresh(keys, now)
                    .SelectMany(refreshed => BuildOutcome(
                        GitHubActionsToken.Verify(rawToken, now, audiences, refreshed.ByKeyId),
                        keys, refreshed, now));
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex,
                    "GitHub build-principal resolution UNAVAILABLE — answering retryable, never "
                    + "'unknown token'");
                return Observable.Return(InstanceAuthResult.Unavailable(ex.Message));
            });
    }

    /// <summary>Turns a verification into the authenticator's outcome, keeping "the key set was
    /// never re-read" apart from "GitHub does not publish that key".</summary>
    private IObservable<InstanceAuthResult> BuildOutcome(
        GitHubTokenVerification verified,
        GitHubSigningKeys before,
        GitHubSigningKeys after,
        DateTimeOffset now)
    {
        if (verified.KeyUnknown)
        {
            // ReferenceEquals, not a value compare: a suppressed refresh hands back the very set it
            // was given, so identity is the exact answer to "was anything re-read".
            if (ReferenceEquals(before, after))
            {
                // The refresh floor suppressed the re-read, so NOTHING was established about this
                // token. Retryable — the build asks again once the floor elapses.
                logger.LogWarning(
                    "A GitHub Actions token named a signing key absent from the set read at "
                    + "{FetchedAt}, and the refresh floor suppressed a re-read — answering retryable",
                    before.FetchedAt);
                return Observable.Return(InstanceAuthResult.Unavailable(
                    "GitHub's signing keys were not re-read within the refresh floor"));
            }
            // The set WAS re-read and still does not hold the key: GitHub does not publish it.
            // That is a verdict.
            logger.LogWarning(
                "A GitHub Actions token named a signing key GitHub does not publish — refusing.");
            return Observable.Return(InstanceAuthResult.Resolved(null));
        }

        return verified.Claims is { } claims
            ? ResolveBuildPrincipal(claims, now)
            : Observable.Return(InstanceAuthResult.Resolved(null));
    }

    /// <summary>
    /// Resolves verified claims to the <see cref="BuildPrincipal"/> the mesh holds for that
    /// repository. The path is a routing hint; the RECORD is the authority, so the repository claim
    /// is compared again against what the node declares — exactly, never as a prefix.
    /// </summary>
    private IObservable<InstanceAuthResult> ResolveBuildPrincipal(
        GitHubBuildClaims claims, DateTimeOffset now)
    {
        var path = BuildPrincipal.PathFor(claims.Repository);
        if (path.Length == 0)
            return Observable.Return(InstanceAuthResult.Resolved(null));

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        // Whether a missing node would fall back to DECLARED trust. When it would, "absent" is itself
        // an admission, so it is asked of the owner too — see ReadBuildPrincipal.
        var declaredFallback = BuildPrincipalConfiguration
            .Declared(hub.ServiceProvider.GetService<IConfiguration>())
            .Find(claims.Repository) is not null;
        // One sealed System scope around the read, for the same reason as Resolve above: this IS
        // the step that turns an anonymous HTTP caller into a known principal, so there is no
        // identity to read with yet — and nothing downstream may inherit System.
        return accessService.RunAsSystem(() => ReadBuildPrincipal(path, declaredFallback)
            .Select(read =>
            {
                if (Unavailable(read, path, "GitHub build-principal resolution") is { } unavailable)
                    return unavailable;

                var principal = Content<BuildPrincipal>(read.Node);
                var principalPath = path;
                if (read.Status == NodeReadStatus.Absent
                    && BuildPrincipalConfiguration
                        .Declared(hub.ServiceProvider.GetService<IConfiguration>())
                        .Find(claims.Repository) is { } declared)
                {
                    // 🚨 DECLARED trust (policy declared-not-derived): no node exists for this
                    // repository, and the deployment's configuration declares it. Consulted ONLY on
                    // a definitive Absent — a node that is there decides, a revoke included, and an
                    // unreadable node was already answered retryable above, because a revoking node
                    // may be exactly what could not be read. Everything below (the repository
                    // re-compare, IsActive, and every Allows at the endpoints) runs on the declared
                    // principal unchanged.
                    principal = declared.Principal;
                    principalPath = declared.ConfigPath;
                }
                if (principal is null)
                {
                    // The NORMAL negative: this mesh does not trust that repository. Definitive,
                    // so the endpoint answers 401.
                    logger.LogWarning(
                        "A verified GitHub Actions token for {Repository} has no build principal at "
                        + "{Path} and none declared in configuration — refusing.",
                        claims.Repository, path);
                    return InstanceAuthResult.Resolved(null);
                }

                if (!GitHubActionsToken.RepositoryEquals(principal.Repository, claims.Repository))
                {
                    logger.LogWarning(
                        "Build principal {Path} declares repository {Declared} but the token claims "
                        + "{Claimed} — refusing.", principalPath, principal.Repository, claims.Repository);
                    return InstanceAuthResult.Resolved(null);
                }

                if (!principal.IsActive(now))
                {
                    logger.LogWarning(
                        "Build principal {Path} for {Repository} is revoked or out of term — refusing.",
                        principalPath, claims.Repository);
                    return InstanceAuthResult.Resolved(null);
                }

                return InstanceAuthResult.ResolvedBuild(
                    new AuthenticatedBuild(principal, claims, principalPath));
            }));
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
        return accessService.RunAsSystem(() => WithinBudget(ResolveNodes(hash, indexPath), indexPath)
            // The plan ladder rides on the caller: a plan-scoped grant entry is decided against it
            // at every surface, and reading it HERE — inside the same System scope — is what keeps
            // the endpoints from each resolving a ladder of their own. A ladder read that fails
            // yields Empty: plan-scoped entries then license nothing (fail closed), plan-less
            // entries are untouched, and the verdict itself stays a verdict. Outside the deadline
            // on purpose: the ladder is bounded by its own query budget and answers from its
            // snapshot, and cutting it here would cancel the one read that warms that snapshot.
            .SelectMany(result => result.Instance is null
                ? Observable.Return(result)
                : Ladder().Select(ranks =>
                {
                    ReportPlanStanding(result.Instance.Instance, ranks);
                    return InstanceAuthResult.Resolved(result.Instance with { Ranks = ranks });
                })));
    }

    /// <summary>
    /// 🚨 <b>ONE deadline over the three node legs, never one per leg (#7484).</b> The index,
    /// instance and grant reads run one after another, and each was bounded by
    /// <see cref="ReadBudget"/> on its own — so a resolution whose legs were each slow, but each
    /// inside its bound, took up to three budgets before it said anything. A caller that gave the
    /// exchange the advertised budget (the registry's authentication hook waits ten seconds) had
    /// hung up by then, so a correct retryable 503 reached nobody and the hook reported a bare 500.
    /// Now the legs SHARE the budget: whatever they have not answered when it is spent is
    /// <see cref="InstanceAuthResult.Unavailable"/> — retryable, never "unknown key" — and each
    /// leg's own hydration keeps running in the process-wide cache, so the retry the 503 asks for
    /// reads it from memory. COLD.
    /// </summary>
    /// <param name="legs">The index → instance → grant chain.</param>
    /// <param name="indexPath">The index path, for the diagnostic.</param>
    private IObservable<InstanceAuthResult> WithinBudget(IObservable<InstanceAuthResult> legs, string indexPath)
        => legs
            .Take(1)
            .Timeout(
                ReadBudget,
                Observable.Defer(() =>
                {
                    logger.LogWarning(
                        "Instance key resolution: {Path} reached no verdict within {Budget}s across its index, "
                        + "instance and grant reads — answering retryable, never 'unknown key', and caching nothing",
                        indexPath, ReadBudget.TotalSeconds);
                    return Observable.Return(InstanceAuthResult.Unavailable(
                        $"{indexPath}: the index, instance and grant reads reached no verdict within "
                        + $"{ReadBudget.TotalSeconds:F0}s together — retry; their hydration continues"));
                }),
                DeadlineScheduler);

    /// <summary>The three node legs of <see cref="Resolve"/>: index → instance → grant.</summary>
    private IObservable<InstanceAuthResult> ResolveNodes(string hash, string indexPath)
        => Read(indexPath)
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
                        // Either slot authenticates: a key STAGED by a rotation is valid alongside
                        // the current one until the rotation commits (InstanceKeyRotation, #2802).
                        if (instance is null || InstanceKeyRotation.SlotOf(instance, hash) == InstanceKeySlot.None)
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
                                    ?? new PluginGrant { InstanceId = instance.InstanceId })
                                {
                                    InstancePath = index.InstancePath,
                                    PresentedKeyHash = hash,
                                });
                            });
                    });
            });

    /// <summary>
    /// 🚨 Names a plan the registry is about to decide with WITHOUT knowing it (#5894). An unknown
    /// stored plan is decided at the baseline (<see cref="PlanTierRanks.CoversInstance"/>, fail
    /// closed) and a RETIRED one as its successor (<see cref="PlanTierRanks.RetiredPlans"/>) — or, when
    /// the ladder does not rank that successor either, at the baseline, and it is reported as such; both
    /// are facts an operator has to act on — re-set the plan on the Instance grants tab — and
    /// neither may pass silently. Once per resolution, which the caller caches, so this is at most
    /// one line per instance per cache window. A registry with NO ladder at all is not reported:
    /// every plan is unknown there by construction, and <see cref="PlanTierLadder"/> already says so.
    /// </summary>
    private void ReportPlanStanding(MeshWeaverInstance instance, PlanTierRanks ranks)
    {
        if (ranks.Ranks.Count == 0)
            return;
        var successor = PlanTierRanks.SuccessorOf(instance.Plan);
        // IsUnknownPlan reads the CANONICAL id, so a retired id whose successor this ladder does not
        // rank is unknown too — and is decided at the baseline, not as the successor. Test it first:
        // the "decided as its successor" sentence is only true when the ladder ranks the successor.
        if (ranks.IsUnknownPlan(instance.Plan))
        {
            if (successor is not null)
                logger.LogWarning(
                    "Instance {InstanceId} stores the RETIRED plan id '{StoredPlan}', whose successor '{Successor}' "
                    + "this registry's plan ladder does not know either (known: {KnownPlans}) — deciding at the "
                    + "baseline '{Baseline}' (fail closed). Seed the '{Successor}' tier, or set a known plan on "
                    + "Settings ▸ Instance grants.",
                    instance.InstanceId, instance.Plan, successor, string.Join(", ", ranks.Ids),
                    PlanTierRanks.BaselinePlan, successor);
            else
                logger.LogWarning(
                    "Instance {InstanceId} stores the plan '{StoredPlan}', which this registry's plan ladder does not "
                    + "know (known: {KnownPlans}) — deciding at the baseline '{Baseline}' (fail closed). Set a known "
                    + "plan on Settings ▸ Instance grants.",
                    instance.InstanceId, instance.Plan, string.Join(", ", ranks.Ids), PlanTierRanks.BaselinePlan);
        }
        else if (successor is not null)
            logger.LogWarning(
                "Instance {InstanceId} stores the RETIRED plan id '{StoredPlan}' — decided as its successor "
                + "'{Successor}'. Set the plan to '{Successor}' on Settings ▸ Instance grants to retire the old id.",
                instance.InstanceId, instance.Plan, successor, successor);
    }

    /// <summary>The registry's plan ladder, or <see cref="PlanTierRanks.Empty"/> on a host that
    /// registers no <see cref="PlanTierLadder"/>. Composed inside the resolution's System scope.</summary>
    private IObservable<PlanTierRanks> Ladder() =>
        hub.ServiceProvider.GetService<PlanTierLadder>()?.Read()
        ?? Observable.Return(PlanTierRanks.Empty);

    /// <summary>The unavailable result for a read that reached no verdict, or null when it did.
    /// <see cref="NodeReadStatus.DeleteInProgress"/> counts as a verdict — the record is going away,
    /// so "unknown key" is the right answer and retrying would only delay it.</summary>
    private InstanceAuthResult? Unavailable(
        NodeReadOutcome read, string path, string what = "Instance key resolution")
    {
        if (read.Status != NodeReadStatus.Unavailable)
            return null;
        var reason = read.Failure?.Message ?? "the read reached no verdict";
        logger.LogWarning(
            "{What}: {Path} was UNAVAILABLE ({Reason}) — answering retryable, "
            + "never 'unknown key', and caching nothing", what, path, reason);
        return InstanceAuthResult.Unavailable($"{path}: {reason}");
    }

    /// <summary>
    /// One INTERROGABLE read by exact path — <see cref="NodeReadStatus.Present"/>,
    /// <see cref="NodeReadStatus.Absent"/> or <see cref="NodeReadStatus.Unavailable"/>, because
    /// "absent" versus "I could not read it" is the entire question this authenticator answers.
    /// </summary>
    private IObservable<NodeReadOutcome> Read(string path) =>
        ReadOverride is { } test ? test(path) : ReadLive(path);

    /// <summary>
    /// The build-principal read: the OWNER's answer, never this process's mirror.
    ///
    /// <para>🚨 <b>A mirror is the wrong instrument for a revocation.</b> <see cref="ReadLive"/>
    /// reads content off the process-wide mirror, which advances when the owner's commit echo
    /// arrives. The write that revokes a principal is acknowledged on the owner's verdict and does
    /// not wait for that echo — so between the acknowledgement and the echo, a mirror read hands back
    /// the ACTIVE principal and a revoked build authenticates. Held deterministically in
    /// <c>BuildPrincipalAuthenticationTest.ARevokedPrincipal_IsRefused_WhileThisProcesssMirrorStillCarriesTheGrant</c>.
    /// The owner applies writes in order and has applied the revoke before acknowledging it, so a
    /// read it answers afterwards carries the revoke, on every replica.</para>
    ///
    /// <para><b>Existence</b> still comes from the parent's live listing, so a token from a
    /// repository nobody trusts never opens a point read on a path that does not exist. The listing
    /// is consulted only where its lag is harmless: a stale "present" ends at the owner, which
    /// answers absent; a stale "absent" refuses. The one case where "absent" ADMITS is a repository
    /// the deployment DECLARES — a node there overrides the declaration, a revoking node included — so
    /// for <paramref name="declaredFallback"/> the owner is asked directly. That set is the
    /// deployment's own configuration, so it cannot be widened by a caller.</para>
    ///
    /// <para>Every non-verdict — a listing or an owner that does not answer within
    /// <see cref="ReadBudget"/>, or a fault — is <see cref="NodeReadStatus.Unavailable"/>: a
    /// retryable 503, never an admission and never "unknown token".</para>
    /// </summary>
    /// <param name="path">The principal's node path.</param>
    /// <param name="declaredFallback">Whether configuration declares this repository, i.e. whether
    /// an absent node would admit.</param>
    private IObservable<NodeReadOutcome> ReadBuildPrincipal(string path, bool declaredFallback)
    {
        if (ReadOverride is { } test)
            return test(path);
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService.RunAsSystem(() =>
        {
            var parent = ParentPath(path);
            var listing = parent is null || declaredFallback
                ? null
                : hub.GetQuery($"instance-key-listing:{parent}", $"path:{parent} scope:children select:path");
            // Its own System scope at ITS subscribe: it subscribes inside the listing's continuation,
            // where the ambient identity is whatever thread delivered that frame.
            return OwnerRead(listing,
                () => accessService.RunAsSystem(() =>
                    hub.GetMeshNodeOutcome(path, ReadBudget, ReadTimeoutBehavior.EmitNull)),
                path, ReadBudget);
        });
    }

    /// <summary>
    /// Folds an optional live listing and an OWNER read into one <see cref="NodeReadOutcome"/>: no
    /// listing, or a listing that shows <paramref name="path"/>, asks the owner; a listing without it
    /// is <see cref="NodeReadStatus.Absent"/>. No answer within <paramref name="budget"/>, or a
    /// fault, is <see cref="NodeReadStatus.Unavailable"/>. Static so the composition is pinned without
    /// a mesh.
    /// </summary>
    /// <param name="listing">The parent's live children listing, or null to ask the owner directly.</param>
    /// <param name="owner">The owner's one-shot read of the path.</param>
    /// <param name="path">The exact node path.</param>
    /// <param name="budget">How long the whole read may take.</param>
    internal static IObservable<NodeReadOutcome> OwnerRead(
        IObservable<IEnumerable<MeshNode>>? listing,
        Func<IObservable<NodeReadOutcome>> owner,
        string path,
        TimeSpan budget)
    {
        var read = listing is null
            ? Observable.Defer(owner)
            : listing
                .Take(1)
                .SelectMany(nodes =>
                    // Ordinal, as in FirstFrame: a case-insensitive match would let a DIFFERENT node
                    // open the point read on a path that does not exist.
                    nodes.Any(node => string.Equals(node.Path, path, StringComparison.Ordinal))
                        ? owner()
                        : Observable.Return(NodeReadOutcome.Absent));

        return read
            .Take(1)
            .Timeout(budget)
            .Catch((TimeoutException elapsed) => Observable.Return(NodeReadOutcome.Unavailable(new TimeoutException(
                $"The owner read of '{path}' produced no answer within {budget.TotalSeconds:F0}s. This is "
                + "NOT 'node not found' — the build is answered retryable.", elapsed))))
            .Catch((Exception ex) => Observable.Return(NodeReadOutcome.Unavailable(ex)));
    }

    /// <summary>
    /// The live read (#3119): listing for EXISTENCE, mirror for CONTENT — the canonical composition
    /// for a node that may not be there (<c>Doc/Architecture/CqrsAndContentAccess</c>, "An OPTIONAL
    /// node").
    ///
    /// <para>Two of the three legs are legitimately absent in normal operation: the index for an
    /// unknown key, and the grant for a freshly registered instance. A point stream on an absent path
    /// is a framework defect — the owner answers a routing NotFound that terminates the stream AND
    /// opens the cache's storm-breaker on that path — so existence is decided by a children listing of
    /// the parent namespace, which is empty-on-absent and live. Both the listing
    /// (<c>Replay(1).AutoConnect(1)</c>, one per namespace) and the point mirror are process-wide
    /// cache entries: hydrated once, kept current by the change feed, read from memory by every later
    /// request.</para>
    ///
    /// <para>Sealed in its own subscribe-scoped System scope on top of the one around
    /// <see cref="Resolve"/>: the second and third legs subscribe inside a continuation of the first,
    /// where the AsyncLocal identity is whatever thread delivered that frame, and both the listing's
    /// RLS wrap and the mirror's read gate capture identity at subscribe time. Idempotent under the
    /// outer scope, and it leaves on the way out of the same Subscribe, so nothing downstream
    /// inherits System.</para>
    /// </summary>
    private IObservable<NodeReadOutcome> ReadLive(string path)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return accessService.RunAsSystem(() =>
        {
            var parent = ParentPath(path);
            // A stable id per parent: the query set is a pure function of the parent, so the cache
            // (keyed on id AND query set) hands back the one shared listing for that namespace.
            var listing = parent is null
                ? null
                : hub.GetQuery($"instance-key-listing:{parent}", $"path:{parent} scope:children select:path");
            return FirstFrame(listing, () => Held(path), path, ReadBudget);
        });
    }

    /// <summary>
    /// The owner's mirror for <paramref name="path"/>; once THIS read has its frame, a standing hold
    /// is taken on it (see <see cref="Hold"/>). Only ever called once the listing has shown the node
    /// exists, so nothing is held for a path that is not there.
    ///
    /// <para>🚨 Held AFTER the read answered, never before: the hold then attaches to an entry that
    /// has just answered, so its replay publishes it at once, and a request whose read times out (a
    /// cold entry that never hydrates, the 503 path) takes NO hold — retries against an owner that
    /// does not answer cannot pile up one pending subscription each.</para>
    /// </summary>
    private IObservable<MeshNode> Held(string path) =>
        ((IObservable<MeshNode>)hub.GetMeshNodeStream(path))
            .Where(node => node is not null)
            .Take(1)
            .Do(_ => Hold(path));

    /// <summary>
    /// 🚨 Keeps the mirror of <paramref name="path"/> live in the process-wide cache
    /// (MeshWeaver#7354): ONE subscription per path, as System, for as long as the node exists.
    /// The cache never idle-releases an entry with a live subscriber, so every later resolution
    /// reads the mirror from memory however long it has been since the last one, and the change
    /// feed keeps it current — a disable or a grant change is still seen by the next request.
    ///
    /// <para>The hold ends by itself: the mirror COMPLETES when the node is deleted (a retired key's
    /// index entry, a removed instance) and FAULTS when its owner fails — the cache evicts a faulted
    /// entry, and the next resolution opens and holds a fresh one. Nothing re-subscribes on its own;
    /// a hold is only ever (re)taken by a request that found the node present.</para>
    /// </summary>
    /// <param name="path">The node path the listing has shown to exist.</param>
    internal void Hold(string path)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        BeforeSubscribe?.Invoke(path);
        var source = HoldOverride is { } test
            ? test(path)
            : hub.ServiceProvider.GetService<AccessService>()
                .RunAsSystem(() => (IObservable<MeshNode>)hub.GetMeshNodeStream(path));
        var slot = new HoldSlot();
        pending[slot] = path;
        // Assigned AFTER Subscribe returns: a mirror that terminates synchronously releases the slot
        // first, and assigning to a disposed SingleAssignmentDisposable disposes the subscription on
        // the spot — so a terminated hold never lingers.
        slot.Subscription.Disposable = source.Subscribe(
            _ =>
            {
                // The hold is PUBLISHED on its mirror's first frame, never at subscribe: see Publish.
                if (slot.TryStamp(() => Interlocked.Increment(ref holdSequence)))
                {
                    pending.TryRemove(slot, out string? _);
                    Publish(path, slot);
                }
            },
            ex =>
            {
                logger.LogDebug(ex,
                    "Instance key resolution: the held mirror of {Path} faulted — released; the next "
                    + "request that finds the node present holds a fresh one", path);
                Release(path, slot);
            },
            () => Release(path, slot));
        // A Dispose that ran while this hold was subscribing missed it.
        if (Volatile.Read(ref disposed) != 0)
            Release(path, slot);
    }

    /// <summary>
    /// 🚨 Installs <paramref name="slot"/> as the hold for <paramref name="path"/> — RENEW, never skip,
    /// and never let a hold on an OLDER mirror win.
    ///
    /// <para>Ordered by the moment each hold's mirror delivered its FIRST frame, stamped when it
    /// arrives. That is the attachment, not the call: a hold that reached a live entry is answered from
    /// its replay at once, a hold that reached a replacement entry is answered when that entry hydrates
    /// — after the old one was evicted — and a hold whose entry faulted before answering is never
    /// stamped and never published. So the later stamp is the newer mirror, whatever order the calls
    /// started in and wherever they paused. Three shapes this closes, each pinned in
    /// <c>RegistryHoldSurvivesALateTerminalTest</c>: skipping because a slot existed (a late terminal
    /// then removed it and left the replacement unheld); replacing unconditionally (an older renewal
    /// paused before publishing disposed a newer one); and ordering by call (a renewal that started
    /// first but attached last was discarded in favour of one on the old mirror).</para>
    ///
    /// <para>The replaced slot is disposed only after this one is installed, so the entry never drops to
    /// zero subscribers; a superseded hold disposes itself; a late terminal releases only its own slot
    /// (pair-exact). A renewal is an in-memory attach to a live cache entry, not a mesh read.</para>
    /// </summary>
    private void Publish(string path, HoldSlot slot)
    {
        BeforePublish?.Invoke(path);
        while (true)
        {
            if (holds.TryGetValue(path, out var current))
            {
                if (current.Sequence > slot.Sequence)
                {
                    slot.Subscription.Dispose();
                    return;
                }
                if (holds.TryUpdate(path, slot, current))
                {
                    current.Subscription.Dispose();
                    break;
                }
            }
            else if (holds.TryAdd(path, slot))
                break;
        }

        // A terminal delivered before the slot was installed, or a Dispose that ran meanwhile, found
        // nothing to remove — remove it now.
        if (slot.Subscription.IsDisposed || Volatile.Read(ref disposed) != 0)
            Release(path, slot);
    }

    /// <summary>Runs before a hold resolves and subscribes to its mirror. Test seam: it lets an
    /// overlapping renewal attach and publish before this one has attached.</summary>
    internal Action<string>? BeforeSubscribe { get; init; }

    /// <summary>
    /// The stream a hold subscribes to, injectable so the renewal can be pinned against a mirror
    /// whose terminal arrives late. Production holds the owner's mirror in the process-wide cache.
    /// </summary>
    internal Func<string, IObservable<MeshNode>>? HoldOverride { get; init; }

    /// <summary>Runs between a hold's first frame and its publication. Test seam: it lets an
    /// overlapping renewal finish while this one is paused before publishing.</summary>
    internal Action<string>? BeforePublish { get; init; }

    /// <summary>Drops <paramref name="slot"/> for <paramref name="path"/>, pair-exact so a newer
    /// hold on the same path is never touched, and disposes its subscription.</summary>
    private void Release(string path, HoldSlot slot)
    {
        holds.TryRemove(new KeyValuePair<string, HoldSlot>(path, slot));
        pending.TryRemove(slot, out _);
        slot.Subscription.Dispose();
    }

    /// <summary>The paths whose mirrors this authenticator currently holds. Test seam.</summary>
    internal IReadOnlyCollection<string> HeldPaths => holds.Keys.ToArray();

    /// <summary>How many holds are subscribed but not yet published. Test seam.</summary>
    internal int PendingHolds => pending.Count;

    /// <summary>Releases every held mirror. Called by the container when the mesh is disposed.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (var (path, slot) in holds)
            Release(path, slot);
        foreach (var (slot, path) in pending)
            Release(path, slot);
    }

    /// <summary>
    /// Folds a live listing and a live mirror into ONE <see cref="NodeReadOutcome"/> for
    /// <paramref name="path"/>: the listing's current snapshot decides present-or-absent, the
    /// mirror's first non-null frame is the content, a mirror that completes without a frame (a
    /// tombstoned delete) is Absent, and no frame within <paramref name="budget"/> — or any fault on
    /// the way — is <see cref="NodeReadStatus.Unavailable"/>, never Absent: a read that reached no
    /// verdict establishes nothing about the key. Static and dependency-free so the classification
    /// is pinned without a mesh.
    /// </summary>
    /// <param name="listing">The parent's live children listing, or null when the path has no parent
    /// (a partition root), in which case the mirror is read directly.</param>
    /// <param name="stream">Opens the owner's mirror for the path. Called only once the listing has
    /// shown the node exists.</param>
    /// <param name="path">The exact node path, for the outcome's diagnostics.</param>
    /// <param name="budget">How long to wait for the first frame.</param>
    internal static IObservable<NodeReadOutcome> FirstFrame(
        IObservable<IEnumerable<MeshNode>>? listing,
        Func<IObservable<MeshNode>> stream,
        string path,
        TimeSpan budget)
    {
        IObservable<NodeReadOutcome> Content() =>
            stream()
                .Where(node => node is not null)
                .Take(1)
                .Select(NodeReadOutcome.Present)
                .DefaultIfEmpty(NodeReadOutcome.Absent);

        var read = listing is null
            ? Content()
            : listing
                .Take(1)
                .SelectMany(nodes =>
                    // Ordinal: mesh paths are case-sensitive, and a case-insensitive match would let a
                    // DIFFERENT node open the point read on a path that does not exist.
                    nodes.Any(node => string.Equals(node.Path, path, StringComparison.Ordinal))
                        ? Content()
                        : Observable.Return(NodeReadOutcome.Absent));

        return read
            .Timeout(budget)
            .Catch((TimeoutException elapsed) => Observable.Return(NodeReadOutcome.Unavailable(new TimeoutException(
                $"The live read of '{path}' produced no frame within {budget.TotalSeconds:F0}s — its "
                + "listing or the owner's mirror has not hydrated yet. This is NOT 'node not found': the "
                + "hydration continues in the process-wide cache, and the retry the 503 asks for reads "
                + "the frame from memory once it lands.",
                elapsed))))
            .Catch((Exception ex) => Observable.Return(NodeReadOutcome.Unavailable(ex)));
    }

    /// <summary>The path's parent, or null for a root-level path.</summary>
    private static string? ParentPath(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? null : path[..slash];
    }

    /// <summary>The node's content as <typeparamref name="T"/> through the framework accessor, which
    /// recovers a payload the cache hub stored as JSON and says why when it cannot.</summary>
    private T? Content<T>(MeshNode? node) where T : class =>
        node.ContentAs<T>(hub.JsonSerializerOptions, logger);
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

    /// <summary>
    /// The authenticated BUILD, when the caller presented a GitHub Actions OIDC token rather than an
    /// instance credential (#2483). A build is a different CALLER CLASS from an installation — it
    /// has no instance record, no plan and no <see cref="PluginGrant"/> — so it is a separate
    /// property rather than an <see cref="AuthenticatedInstance"/> with empty fields, and every
    /// surface that needs an installation keeps refusing it unchanged.
    /// </summary>
    public AuthenticatedBuild? Build { get; init; }

    /// <summary>Why no verdict was reached, or <c>null</c> when one was.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>True when resolution reached NO verdict — retryable, never "unknown key".</summary>
    public bool IsUnavailable => UnavailableReason is not null;

    /// <summary>The resolution completed; <paramref name="instance"/> is the answer (possibly a
    /// definitive <c>null</c>).</summary>
    public static InstanceAuthResult Resolved(AuthenticatedInstance? instance) =>
        new() { Instance = instance };

    /// <summary>The resolution completed and the caller is a trusted BUILD.</summary>
    /// <param name="build">The authenticated build principal.</param>
    /// <returns>A resolved outcome carrying <paramref name="build"/>.</returns>
    public static InstanceAuthResult ResolvedBuild(AuthenticatedBuild build) =>
        new() { Build = build };

    /// <summary>The resolution reached no verdict — answer retryable, and cache nothing.</summary>
    public static InstanceAuthResult Unavailable(string reason) =>
        new() { UnavailableReason = reason };
}

/// <summary>
/// An authenticated BUILD: a GitHub Actions run whose OIDC token verified against GitHub's JWKS,
/// together with the <see cref="Mesh.Security.BuildPrincipal"/> node this mesh holds for its
/// repository (#2483).
///
/// <para>🚨 The claims contribute IDENTITY and CONTEXT, never authority. What the run may do is read
/// from the live node on every request — so a <see cref="BuildPrincipalActions.Revoke"/> written a
/// second ago takes effect now rather than when the token expires. Exactly the property the
/// short-lived-token leg has, and for the same reason.</para>
/// </summary>
/// <param name="Principal">The admin-owned rule the repository resolved to.</param>
/// <param name="Claims">The verified token claims.</param>
/// <param name="PrincipalPath">Where the rule was read from — the node path, or the configuration
/// path of a principal the deployment declares — for the log and the ledger.</param>
public sealed record AuthenticatedBuild(
    BuildPrincipal Principal, GitHubBuildClaims Claims, string PrincipalPath)
{
    /// <summary>The repository this build speaks for, normalized to <c>owner/name</c>.</summary>
    public string Repository => GitHubActionsToken.NormalizeRepository(Claims.Repository);

    /// <summary>Whether this build may perform <paramref name="verb"/> on registry source
    /// <paramref name="source"/> at <paramref name="now"/>.</summary>
    /// <param name="verb">A <see cref="BuildVerbs"/> value.</param>
    /// <param name="source">The registry source name.</param>
    /// <param name="now">The instant to decide at.</param>
    /// <returns>True when the principal allows it.</returns>
    public bool Allows(string verb, string source, DateTimeOffset now) =>
        Principal.Allows(Claims, verb, source, now);

    /// <summary><see cref="Allows(string,string,DateTimeOffset)"/> right now — what a live request
    /// means.</summary>
    /// <param name="verb">A <see cref="BuildVerbs"/> value.</param>
    /// <param name="source">The registry source name.</param>
    /// <returns>True when the principal allows it.</returns>
    public bool Allows(string verb, string source) => Allows(verb, source, DateTimeOffset.UtcNow);

    /// <summary>Why the principal refuses, or null when it allows — for the LOG and the entitlement
    /// ledger only, never for the HTTP body.</summary>
    /// <param name="verb">A <see cref="BuildVerbs"/> value.</param>
    /// <param name="source">The registry source name.</param>
    /// <param name="now">The instant to decide at.</param>
    /// <returns>The refusal reason, or null.</returns>
    public string? Refuse(string verb, string source, DateTimeOffset now) =>
        Principal.Refuse(Claims, verb, source, now);
}

/// <summary>An authenticated caller: which instance presented the key, and what it may pull.</summary>
/// <param name="Instance">The registered instance the key belongs to.</param>
/// <param name="Grant">Its grant. Never null — an instance with no grant node carries an empty
/// grant, which authorizes nothing.</param>
public sealed record AuthenticatedInstance(MeshWeaverInstance Instance, PluginGrant Grant)
{
    /// <summary>
    /// Full path of the <see cref="MeshWeaverInstance"/> node the key resolved to — what the
    /// registry's own key-lifecycle endpoints write to. Null only for a result built without a
    /// resolution (a test's hand-made caller).
    /// </summary>
    public string? InstancePath { get; init; }

    /// <summary>
    /// The key hash the caller authenticated WITH — the instance's current key, or the key a rotation
    /// has staged (both authenticate until the rotation commits, MeshWeaver#2802). Anything that binds
    /// to the caller's key (a sync token's claims) binds to THIS, never to
    /// <see cref="MeshWeaverInstance.KeyHash"/>: a token minted with the staged key but bound to the
    /// current one would stop resolving the moment the commit retires it. Null only for a result built
    /// without a resolution.
    /// </summary>
    public string? PresentedKeyHash { get; init; }

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

    /// <summary>
    /// 🚨 The typed refusal for a package this caller's grant REACHES but its plan does not cover
    /// (#4097) — <see cref="PluginGrant.TierRefusal"/> decided with the caller's ladder and the
    /// record's plan, as a <see cref="PlanTierRefusal"/> the listing can return. Null when the
    /// package is allowed, when no grant entry reaches it (absence — the enumeration defence), or
    /// when the presented token's scope excludes it (a token can only narrow; a package outside its
    /// scope is absent to this token, whatever the grant says).
    /// </summary>
    /// <param name="module">The compiled module the package delivers, carried onto the refusal so
    /// the consumer can key it by module name (<c>Modules:Required</c>, the activation record).</param>
    public PlanTierRefusal? TierRefusal(
        string sourceName, string packageId, string? packageTier, string? module, DateTimeOffset now)
    {
        if (TokenScope is not null && !TokenScope.Covers(sourceName, packageId))
            return null;
        return Grant.TierRefusal(sourceName, packageId, packageTier, Ranks, Instance.Plan, now) is { } plan
            ? new PlanTierRefusal(packageId, module, PlanTierRanks.Canonical(packageTier), plan)
            : null;
    }

    /// <summary><see cref="TierRefusal(string,string,string?,string?,DateTimeOffset)"/> right now.</summary>
    public PlanTierRefusal? TierRefusal(string sourceName, string packageId, string? packageTier, string? module) =>
        TierRefusal(sourceName, packageId, packageTier, module, DateTimeOffset.UtcNow);
}
