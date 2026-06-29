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
    /// Initializes a new instance of the <c>CircuitAccessHandler</c> class.
    /// </summary>
    /// <param name="hub">The message hub used to resolve the <c>AccessService</c> and mesh services.</param>
    /// <param name="authStateProvider">Provides the authentication state used to resolve the circuit's user.</param>
    /// <param name="circuitContextAccessor">The circuit-scoped accessor that carries the circuit id and user context.</param>
    /// <param name="loggerFactory">Factory used to create the access-context logger.</param>
    public CircuitAccessHandler(
        IMessageHub hub,
        AuthenticationStateProvider authStateProvider,
        ICircuitContextAccessor circuitContextAccessor,
        ILoggerFactory loggerFactory)
    {
        _hub = hub;
        _authStateProvider = authStateProvider;
        _circuitContextAccessor = circuitContextAccessor;
        _logger = loggerFactory.CreateLogger("MeshWeaver.AccessContext");
        _circuitLogger = loggerFactory.CreateLogger("MeshWeaver.Blazor.Circuit");
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

        _userContext = await ResolveUserContextAsync();

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
        // Clear the persistent fallback to prevent stale context
        var accessService = _hub.ServiceProvider.GetService<AccessService>();
        accessService?.ClearPersistentCircuitContext();
        _circuitContextAccessor.SetUserContext(null);
        _userContext = null;
        return Task.CompletedTask;
    }

    private async Task<AccessContext?> ResolveUserContextAsync()
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
                return new AccessContext
                {
                    ObjectId = WellKnownUsers.Anonymous,
                    Name = "Guest",
                    IsVirtual = true
                };

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
                Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
            };

            // Authoritative resolution: the mesh User node's Id. The cache is a
            // hot snapshot — when it has the node, prefer its Id + Name over the
            // email-local-part heuristic. When it misses (circuit opened before
            // the nodeType:User query hydrated), the local-part fallback above
            // still resolves to the correct partition.
            if (!string.IsNullOrEmpty(email))
            {
                var meshUser = TryLoadMeshUser(email);
                if (meshUser != null)
                {
                    context = context with
                    {
                        ObjectId = meshUser.Id,
                        Name = meshUser.Name ?? meshUser.Id
                    };
                }
            }

            return context;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve user context from AuthenticationState");
            return null;
        }
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
        return at > 0 ? email[..at] : email;
    }

    /// <summary>
    /// Synchronous email → mesh User lookup via the hot
    /// <see cref="MeshWeaver.Blazor.Infrastructure.UserIdentityCache"/>. The cache
    /// subscribes to <see cref="IMeshService.Query{T}"/> at startup so the
    /// lookup never bridges back to <c>Task</c> — <c>await FirstAsync()</c> on a
    /// hub-touching observable deadlocks the hub pump.
    /// </summary>
    private MeshNode? TryLoadMeshUser(string email)
    {
        try
        {
            var cache = _hub.ServiceProvider.GetService<MeshWeaver.Blazor.Infrastructure.UserIdentityCache>();
            return cache?.TryGetByEmail(email);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load mesh user for email {Email}", email);
            return null;
        }
    }
}
