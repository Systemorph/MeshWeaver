using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Claims;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// Scoped CircuitHandler that restores per-circuit user identity on each inbound activity.
/// Resolves the user from AuthenticationStateProvider on circuit open, stores the context,
/// and sets the AsyncLocal circuitContext on AccessService for every inbound event.
/// This ensures each Blazor circuit sees the correct user without cross-circuit contamination.
///
/// Also captures the stable per-circuit id into the circuit-scoped
/// <see cref="ICircuitContextAccessor"/> on circuit open, so <c>PortalApplication</c> can key
/// its portal hub address on one-hub-per-tab. Because this handler is circuit-scoped, the
/// accessor it writes is the same instance the circuit's PortalApplication reads.
/// </summary>
public class CircuitAccessHandler : CircuitHandler
{
    private readonly IMessageHub _hub;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly ICircuitContextAccessor _circuitContextAccessor;
    private readonly ILogger _logger;
    // Dedicated low-volume category for circuit lifecycle (open/close/up/down) so it can be made
    // visible (Information) independently of the chatty MeshWeaver.AccessContext channel — these are
    // the "what's going on in Blazor" signals: a connection-down is the literal transport flake.
    private readonly ILogger _circuitLogger;
    private AccessContext? _userContext;

    /// <summary>
    /// Live only while this circuit opened on a DEGRADED identity — i.e. the mesh user index could
    /// not answer, so the circuit is running on the email-local-part seed with default time zone
    /// and language. It completes on the first determinate answer (which upgrades the context) and
    /// is disposed by <see cref="UpdateUserContext"/> and on circuit close. Null on every circuit
    /// that resolved normally, which is the overwhelming majority.
    /// </summary>
    private IDisposable? _identityRecovery;

    /// <summary>
    /// The language this circuit's browser asked for, negotiated against <see cref="Locales.Supported"/>
    /// — or <see langword="null"/> when it asked for nothing this deployment ships.
    ///
    /// <para>🚨 Captured in the CONSTRUCTOR, deliberately, and this is the load-bearing detail of the
    /// whole seed. Blazor resolves the circuit's <c>CircuitHandler</c>s from inside a
    /// <c>ComponentHub</c> hub method that it <b>awaits</b> — <c>StartCircuit</c> for a classic
    /// Blazor Server payload, and the first <c>UpdateRootComponents</c> for a Blazor Web App, where
    /// handler creation is deliberately deferred so a handler can see the restored application
    /// state. Either way this constructor runs while the <c>/_blazor</c> request carrying the header
    /// is unambiguously alive, on its own execution flow. The circuit lifecycle that follows
    /// (<see cref="OnCircuitOpenedAsync"/>, then <see cref="OnConnectionUpAsync"/>) is dispatched
    /// fire-and-forget from that same method, so reading the accessor THERE would be racing the
    /// request's lifetime for no benefit.</para>
    ///
    /// <para><b>And the seed lands before the FIRST RENDER.</b> The framework runs
    /// <see cref="OnCircuitOpenedAsync"/> / <see cref="OnConnectionUpAsync"/> before it adds and
    /// renders any root component, so the language is on the circuit's identity by the time anything
    /// draws. That ordering is not a nicety: <c>LayoutAreaHost</c> captures the access context in its
    /// CONSTRUCTOR, so a locale arriving mid-circuit would re-render nothing and the fix would look
    /// correct in a unit test while doing nothing in the browser. Pinned end-to-end over a real
    /// SignalR connection by <c>AnonymousCircuitLocaleSeedTest</c>.</para>
    ///
    /// <para><b>Why the header at all.</b> An anonymous visitor has no profile, so
    /// <c>AccessContext.Locale</c> is null for every one of them and every server-rendered string
    /// falls back to English — which makes "the viewer's language wins for chrome" inert for exactly
    /// the audience it was written for (a first-time visitor hitting a paywall is anonymous by
    /// definition). The header is the only statement of language such a visitor makes. It is read
    /// ONCE, here, and seeded onto the identity — it never becomes a second, ambient resolution
    /// mechanism, and <c>CultureInfo.CurrentUICulture</c> is never consulted (a layout-area render
    /// hops the hub scheduler, where an AsyncLocal culture would not survive and one user's UI would
    /// pick up another user's language).</para>
    /// </summary>
    private readonly string? _requestLocale;

    /// <summary>
    /// Initializes a new instance of the <c>CircuitAccessHandler</c> class.
    /// </summary>
    /// <param name="hub">The message hub used to resolve the <c>AccessService</c> and mesh services.</param>
    /// <param name="authStateProvider">Provides the authentication state used to resolve the circuit's user.</param>
    /// <param name="circuitContextAccessor">The circuit-scoped accessor that carries the circuit id and user context.</param>
    /// <param name="loggerFactory">Factory used to create the access-context logger.</param>
    /// <param name="httpContextAccessor">
    /// Access to the <c>/_blazor</c> request that is establishing this circuit, used ONLY to read
    /// <c>Accept-Language</c> (see <see cref="_requestLocale"/>). Optional so a host that does not
    /// register it still builds circuits — such a host simply has no request-derived language and
    /// falls back to English, exactly as before.
    /// </param>
    public CircuitAccessHandler(
        IMessageHub hub,
        AuthenticationStateProvider authStateProvider,
        ICircuitContextAccessor circuitContextAccessor,
        ILoggerFactory loggerFactory,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _hub = hub;
        _authStateProvider = authStateProvider;
        _circuitContextAccessor = circuitContextAccessor;
        _logger = loggerFactory.CreateLogger("MeshWeaver.AccessContext");
        _circuitLogger = loggerFactory.CreateLogger("MeshWeaver.Blazor.Circuit");
        _requestLocale = Locales.Negotiate(
            httpContextAccessor?.HttpContext?.Request.Headers.AcceptLanguage.ToString());
    }

    /// <summary>
    /// Gets the resolved user context for this circuit.
    /// Can be used by Blazor components that need to update the context (e.g., onboarding).
    /// </summary>
    public AccessContext? UserContext => _userContext;

    /// <summary>
    /// Updates the stored circuit context (e.g., after onboarding resolves the username).
    /// Also sets the AsyncLocal circuitContext for immediate use in the current scope.
    /// </summary>
    public void UpdateUserContext(AccessContext context)
    {
        // An explicit write SUPERSEDES a pending degraded-identity repair. Onboarding calls this
        // with an identity it just established authoritatively; letting the index-watcher fire
        // afterwards would re-derive the same user from a staler source, and could clobber a
        // refinement onboarding made deliberately. Disposing is also what stops the watcher
        // outliving the reason it existed.
        _identityRecovery?.Dispose();
        _identityRecovery = null;
        _userContext = context;
        // Carry the resolved user on the per-circuit accessor so the per-circuit portal
        // hub stamps it on every post (the SOURCE of the never-null AccessContext invariant).
        // Onboarding legitimately refines the identity here (anonymous/seed → username), so
        // this overwrites the on-open value — SetUserContext is intentionally not write-once.
        _circuitContextAccessor.SetUserContext(context);
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        accessService?.SetCircuitContext(context);
        _logger.LogDebug("Circuit context updated: user={UserId}", context.ObjectId);
    }

    /// <inheritdoc />
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        // Capture the stable per-circuit id BEFORE anything else. The framework runs this
        // once per circuit, before any component is initialized, so PortalApplication (resolved
        // when the first interactive component injects it) always sees the id already set.
        // Circuit.Id is unique per circuit and constant for the circuit's lifetime.
        _circuitContextAccessor.SetCircuitId(circuit.Id);

        var resolved = await ResolveUserContextAsync();
        _userContext = resolved.Context;

        // Carry the resolved user on the per-circuit accessor. PortalApplication reads it
        // when it builds the per-circuit portal hub so the hub stamps the circuit user on
        // every post — independent of the mesh-wide AsyncLocal/persistent fallback, which is
        // cleared per inbound activity (the root cause of the null-AccessContext subscribes
        // that returned the empty agent registry). ResolveUserContextAsync never returns null
        // for an open circuit (anonymous VUser at minimum), so the portal always has a user.
        _circuitContextAccessor.SetUserContext(_userContext);

        if (_userContext != null)
        {
            // Set immediately for any code that runs during circuit initialization
            var accessService = _hub.ServiceProvider.GetService<AccessService>();
            accessService?.SetCircuitContext(_userContext);
        }

        // Information (was Debug): one line per circuit — cheap, and the anchor for correlating a
        // user's session with the connection up/down churn below and any anonymous-gate redirect.
        _circuitLogger.LogInformation("Circuit opened: id={CircuitId}, user={UserId}",
            circuit.Id, _userContext?.ObjectId ?? "(anonymous)");

        // 🚨 The circuit opened on a DEGRADED identity. Start the repair LAST, after _userContext
        // and the accessor are set: the chain re-asks at subscribe time, so it can complete
        // synchronously right here, and anything that assigned _userContext afterwards would undo
        // the upgrade it just made.
        if (resolved.UnresolvedEmail is { } pendingEmail)
            StartIdentityRecovery(pendingEmail);
    }

    /// <summary>
    /// The SignalR transport for this circuit dropped (network blip, tab backgrounded, server
    /// pressure, deploy). Logged at Warning because a repeated up/down churn for one user IS the
    /// "flaky" symptom — this is the server-side trace that was previously missing entirely
    /// (OnConnectionDownAsync was never overridden, so a flapping circuit left no record).
    /// </summary>
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken ct)
    {
        _circuitLogger.LogWarning("Circuit connection DOWN: id={CircuitId}, user={UserId}",
            circuit.Id, _userContext?.ObjectId ?? "(anonymous)");
        return Task.CompletedTask;
    }

    /// <summary>The SignalR transport re-established (reconnect succeeded). Pairs with
    /// <see cref="OnConnectionDownAsync"/> so a down→up gap is measurable in the logs.</summary>
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct)
    {
        _circuitLogger.LogInformation("Circuit connection UP: id={CircuitId}, user={UserId}",
            circuit.Id, _userContext?.ObjectId ?? "(anonymous)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async activityContext =>
        {
            var accessService = _hub.ServiceProvider.GetService<AccessService>();
            if (_userContext != null)
                accessService?.SetCircuitContext(_userContext);
            try
            {
                await next(activityContext);
            }
            finally
            {
                accessService?.SetCircuitContext(null);
            }
        };
    }

    /// <inheritdoc />
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        _circuitLogger.LogInformation("Circuit closed: id={CircuitId}, user={UserId}",
            circuit.Id, _userContext?.ObjectId ?? "(anonymous)");
        // Nothing process-wide to clear: SetCircuitContext writes only this flow's AsyncLocal,
        // and the durable per-circuit identity is the scoped accessor cleared just below (which
        // dies with the circuit's DI scope anyway).
        _circuitContextAccessor.SetUserContext(null);
        _userContext = null;
        // The degraded-identity watcher (if this circuit ever had one) dies with the circuit —
        // otherwise it would hold a subscription on the mesh user index for a circuit that no
        // longer exists, which is exactly the per-circuit accumulation that wedged the portal.
        _identityRecovery?.Dispose();
        _identityRecovery = null;

        // 🚨 Dispose the per-circuit portal hub (portal/<circuitId>) — this is the ONE
        // exactly-once teardown point for it. Circuit close is terminal for the id (Blazor
        // never reuses a closed circuit; reconnects keep the circuit alive and don't land
        // here), and PortalApplication.Dispose deliberately does NOT dispose the hub (many
        // wrapper instances share it within a circuit). Without this, every closed circuit
        // (tab close, refresh, transport death, each E2E browser context) leaves a ZOMBIE
        // portal hub whose hosted sync-stream children keep heartbeating and fanning out
        // DataChangedEvents forever — the accumulation that saturated the routing thread,
        // starved live circuits' SignalR keepalive (the chat-vanish), and pegged the e2e
        // portal at ~1.2 cores with 7k leaked sync hubs (2026-07-01). Disposing the portal
        // hub disposes its sync children; each child's teardown posts UnsubscribeRequest to
        // its owner node, so the owner-side mirrors close too.
        var portalHub = _hub.GetHostedHub(
            AddressExtensions.CreatePortalAddress(circuit.Id), HostedHubCreation.Never);
        if (portalHub is not null)
        {
            _circuitLogger.LogInformation(
                "Disposing per-circuit portal hub {Address} on circuit close", portalHub.Address);
            portalHub.Dispose();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// One circuit-open identity resolution. <paramref name="UnresolvedEmail"/> is non-null ONLY on
    /// the degraded leg — the mesh user index could not answer, so <paramref name="Context"/> is the
    /// email-local-part seed with default time zone and language, and the identity must be re-asked
    /// rather than kept. It is a separate field, not something inferred from the context, because
    /// "seeded" and "resolved" are otherwise indistinguishable by inspection: the seed's ObjectId is
    /// usually the same string the real answer produces, and it is Name / TimeZoneId / Locale that
    /// silently differ.
    /// </summary>
    private readonly record struct ResolvedIdentity(AccessContext? Context, string? UnresolvedEmail);

    private async Task<ResolvedIdentity> ResolveUserContextAsync()
    {
        try
        {
            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            if (user?.Identity?.IsAuthenticated != true)
                // 🔒 Unauthenticated → an anonymous VIRTUAL user (vuser == anonymous), NEVER null.
                // Returning null here was the read-protection hole: a null circuit context was
                // silently elevated to `system-security` (Permission.All) at the sync-stream
                // subscribe seam (JsonSynchronizationStream), so logged-out visitors could READ
                // private Spaces by URL (e.g. /AgenticPension/). As a concrete anonymous VUser the
                // circuit's reads flow through normal RLS: DENIED on private nodes, and still
                // allowed on PublicRead content (Doc/login) because `publicGrant` is OR'd in for
                // every identity (PermissionEvaluator). Shaped like VirtualUserMiddleware's guest.
                return new ResolvedIdentity(
                    new AccessContext
                    {
                        ObjectId = WellKnownUsers.Anonymous,
                        Name = "Guest",
                        IsVirtual = true,
                        // 🚨 THE PAYWALL CASE. This visitor has no profile and never will until they
                        // sign up, so the request header is the only thing that can make the portal
                        // speak their language. Null here (the previous behaviour) is what served
                        // English chrome to every anonymous viewer, whatever their browser asked for.
                        Locale = _requestLocale
                    },
                    // Nothing to re-ask: an unauthenticated circuit has no email, and the anonymous
                    // VUser is the ANSWER here, not a degraded stand-in for one.
                    null);

            var email = user.FindFirstValue(ClaimTypes.Email)
                        ?? user.FindFirstValue("email")
                        ?? user.FindFirstValue("preferred_username")
                        ?? string.Empty;

            // ObjectId must be the username (= the user's partition key), never
            // the email. Seed it from the email's local part so that even if the
            // UserIdentityCache hasn't hydrated yet, routing targets the user's
            // partition (`rbuergi`) instead of the raw email
            // (`rbuergi@systemorph.com` → "No node found at ...").
            var context = new AccessContext
            {
                Name = user.FindFirstValue(ClaimTypes.Name)
                       ?? user.FindFirstValue("name")
                       ?? string.Empty,
                ObjectId = UsernameFromEmail(email),
                Email = email,
                Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
                // The browser's request is a SEED, never an override: ApplyMeshUser below keeps a
                // profile's language when the profile states one and only falls back to this. So a
                // signed-in user's stored preference still wins, and a signed-in user who has never
                // stated one finally gets their browser's language instead of unconditional English.
                Locale = _requestLocale
            };

            // Authoritative resolution: the mesh User node's Id. The cache is a
            // hot snapshot — when it has the node, prefer its Id + Name over the
            // email-local-part heuristic. When it misses (circuit opened before
            // the nodeType:User query hydrated), the local-part fallback above
            // still resolves to the correct partition.
            if (!string.IsNullOrEmpty(email))
            {
                var lookup = TryLoadMeshUser(email);
                if (lookup.IsUnavailable)
                {
                    // 🚨 NOT "this user has no node" (#974). On this leg the circuit keeps the
                    // email-local-part ObjectId AND silently falls back to UTC + English, because
                    // TimeZoneId/Locale are read off the profile below. Naming the condition is
                    // what lets an operator see why a German user got an English portal for the
                    // first seconds after a restart, instead of hunting a phantom profile bug.
                    //
                    // 🚨 And it is only "for the first seconds" BECAUSE of the re-ask below. An
                    // unavailable index is a question that has not been answered yet, never a
                    // verdict — so the seed must not become the circuit's permanent identity. The
                    // caller hands this email to StartIdentityRecovery, which waits on the index's
                    // own live query emission and then re-runs ApplyMeshUser: the SAME projection
                    // the success path a few lines down applies, reached one lap later.
                    _logger.LogWarning(
                        "CircuitAccessHandler: mesh user index UNAVAILABLE for {Email} ({Reason}) — "
                        + "seeding the partition key from the email local-part and defaulting time "
                        + "zone / language until the index answers. This is NOT evidence the user "
                        + "is unknown.",
                        email, lookup.UnavailableReason);
                    return new ResolvedIdentity(context, email);
                }
                if (lookup.Node is { } meshUser)
                    context = ApplyMeshUser(context, meshUser, _hub.JsonSerializerOptions);
            }

            return new ResolvedIdentity(context, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve user context from AuthenticationState");
            return default;
        }
    }

    /// <summary>
    /// Projects a resolved mesh <c>User</c> node onto a seeded context. A thin alias for
    /// <see cref="MeshUserProjection.Apply"/> — the ONE place the authoritative identity is read,
    /// shared by the circuit-open success path, by the degraded-identity repair, AND by the SSR
    /// request path (<c>UserContextMiddleware</c>) so none of the three can drift apart.
    ///
    /// <para>Resolves the viewer's display time zone from their profile so it rides on the
    /// AccessContext to every render path — the Blazor circuit AND server-side hub layout areas —
    /// and stored-UTC timestamps display in the viewer's local time via
    /// <c>AccessService.ToDisplayTime</c>. Unset (never signed in on a browser yet) → stays null
    /// → renders UTC.</para>
    ///
    /// <para>The viewer's UI language is resolved from the same profile read, for the same reason
    /// and along the same path: it rides on the AccessContext so <c>AccessService.Localize</c>
    /// translates identically on the circuit and on server-side hub renders. Unset → stays null →
    /// renders English. 🚨 This is why an unanswered lookup must not be latched: defaulting to
    /// English because an index was slow gives a German viewer an English portal for the whole
    /// circuit, which is precisely what resolving explicitly off <c>AccessContext.Locale</c>
    /// (rather than an ambient culture) exists to prevent.</para>
    ///
    /// <para>Public and static so the projection is testable on its own — it is a pure function of
    /// (seed, node), with no circuit, hub or authentication state involved.</para>
    /// </summary>
    /// <param name="seed">The context built from the authentication claims.</param>
    /// <param name="meshUser">The authoritative mesh <c>User</c> node for that identity.</param>
    /// <param name="options">Serializer options used to read the node's <c>User</c> content.</param>
    public static AccessContext ApplyMeshUser(
        AccessContext seed,
        MeshNode meshUser,
        System.Text.Json.JsonSerializerOptions options)
        => MeshUserProjection.Apply(seed, meshUser, options);

    /// <summary>
    /// The repair for a circuit that opened on a degraded identity: waits for the mesh user index
    /// to reach a VERDICT for <paramref name="email"/> and, if it finds the node, re-projects the
    /// context through <see cref="ApplyMeshUser"/> and publishes it through the existing
    /// <see cref="UpdateUserContext"/> hook.
    ///
    /// <para>🚨 No timer, no poll, no retry loop, no widened timeout. The index is filled by a live
    /// <c>nodeType:User</c> query, so the thing being waited for is ALREADY an observable event;
    /// the unavailable leg simply falls through to it (<see cref="UserIdentityCache.IndexChanged"/>)
    /// and re-asks the same question. A definitive "no such user" ends the wait too — the seed is
    /// the right answer to that, and onboarding owns the identity from there via its own
    /// <see cref="UpdateUserContext"/> call.</para>
    /// </summary>
    private void StartIdentityRecovery(string email)
    {
        UserIdentityCache? cache;
        try
        {
            cache = _hub.ServiceProvider.GetService<UserIdentityCache>();
        }
        catch (Exception ex)
        {
            // Same edge TryLoadMeshUser guards: resolving from a hub whose DI scope is mid-disposal
            // throws. Nothing to repair against, and the circuit is going away anyway.
            _logger.LogWarning(ex,
                "CircuitAccessHandler: cannot watch the mesh user index for {Email}", email);
            return;
        }

        if (cache is null)
            // No index registered at all is a static configuration fact, not a transient outage —
            // there is nothing that could ever answer, so there is nothing to wait for.
            return;

        // The chain re-asks at subscribe time, so it can complete INSIDE this Subscribe call — in
        // which case UpdateUserContext runs before the assignment below and the handle it clears is
        // still null. Harmless: Take(1) disposes the subscription on completion, so what gets
        // assigned afterwards is an inert handle. The disposal in UpdateUserContext is for the case
        // it exists for — a repair still PENDING when onboarding writes an identity of its own.
        _identityRecovery = cache.WhenDetermined(email)
            .Subscribe(
                lookup =>
                {
                    // A determinate MISS is a real answer: this email has no mesh User node. The
                    // seeded context stays, and it is correct.
                    if (lookup.Node is not { } meshUser)
                        return;
                    // The circuit may have closed, or onboarding may have written a context, while
                    // the index was filling. _userContext is the live value in both cases.
                    if (_userContext is not { } current)
                        return;
                    UpdateUserContext(ApplyMeshUser(current, meshUser, _hub.JsonSerializerOptions));
                    // Information, and it PAIRS with the Warning above: an operator who sees the
                    // degradation must be able to see whether it healed. Bounded by that Warning's
                    // own volume — at most one of each per degraded circuit.
                    _circuitLogger.LogInformation(
                        "Circuit identity re-resolved from the mesh user index: user={UserId}, "
                        + "locale={Locale}, timeZone={TimeZone}",
                        _userContext?.ObjectId, _userContext?.Locale, _userContext?.TimeZoneId);
                },
                ex => _logger.LogWarning(ex,
                    "CircuitAccessHandler: the mesh user index faulted while re-resolving {Email}; "
                    + "the circuit keeps its seeded identity", email));
    }

    /// <summary>
    /// Derives the username (= the user's mesh partition key) from an email
    /// address. Post-v10 each user owns a partition keyed by username and the
    /// User node sits at its root (<c>path = username</c>). The portal's
    /// convention is <c>username == email local-part</c> (e.g.
    /// <c>rbuergi@systemorph.com → rbuergi</c>), so the local part is the
    /// correct partition match when the <see cref="MeshWeaver.Blazor.Infrastructure.UserIdentityCache"/> hasn't
    /// hydrated yet. Falls back to the input unchanged when there's no <c>@</c>.
    /// </summary>
    private static string UsernameFromEmail(string email)
    {
        if (string.IsNullOrEmpty(email))
            return email;
        var at = email.IndexOf('@');
        // 🚨 LOWERCASE — the same mapping onboarding applies (BootstrapController lowercases the
        // username; post-v10 partition ids are lowercase). Preserving the email's casing here made
        // the circuit ObjectId 'Roland' while the User node is 'roland' whenever the identity
        // cache had not hydrated — the home page then rendered a TYPELESS hub ("Area not found:
        // Activity"), and navigating it materialized a stub root that poisoned later resolutions.
        return (at > 0 ? email[..at] : email).ToLowerInvariant();
    }

    /// <summary>
    /// Synchronous email → mesh User lookup via the hot
    /// <see cref="MeshWeaver.Blazor.Infrastructure.UserIdentityCache"/>. The cache
    /// subscribes to <see cref="IMeshService.Query{T}"/> at startup so the
    /// lookup never bridges back to <c>Task</c> — <c>await FirstAsync()</c> on a
    /// hub-touching observable deadlocks the hub pump.
    /// </summary>
    private MeshWeaver.Blazor.Infrastructure.UserIdentityLookup TryLoadMeshUser(string email)
    {
        try
        {
            var cache = _hub.ServiceProvider.GetService<MeshWeaver.Blazor.Infrastructure.UserIdentityCache>();
            // No cache registered at all is a static configuration fact — there is nothing to
            // resolve from — not a transient outage, so it is Unknown, not Unavailable. Same
            // distinction UserRoleResolver draws for "no role source at all" (#970).
            return cache is null
                ? MeshWeaver.Blazor.Infrastructure.UserIdentityLookup.Unknown
                : cache.Lookup(email);
        }
        catch (Exception ex)
        {
            // 🚨 SWEEP (#974): the service-provider resolve throws on a hub mid-disposal. That is
            // an availability condition; returning `null` here made it indistinguishable from
            // "this user has no mesh node".
            _logger.LogWarning(ex, "Failed to load mesh user for email {Email}", email);
            return MeshWeaver.Blazor.Infrastructure.UserIdentityLookup.Unavailable(
                $"user index lookup faulted: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
