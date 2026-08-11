using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

/// <summary>
/// Holds and propagates the current user's <see cref="AccessContext"/> for the
/// messaging layer. The request-scoped <see cref="Context"/> and the
/// session-scoped <see cref="CircuitContext"/> are both stored as
/// <c>AsyncLocal</c> so that each in-flight message delivery or Blazor circuit
/// observes the correct identity without cross-contamination. Provides
/// scoped impersonation helpers (<see cref="SwitchAccessContext"/>,
/// <see cref="ImpersonateAsHub"/>, <see cref="ImpersonateAsSystem"/>) that
/// restore the previous value on dispose, and guards against hub-shaped
/// addresses ever being set as a user identity.
/// </summary>
public class AccessService
{
    /// <summary>
    /// Returns true if <paramref name="objectId"/> is a hub-shaped address
    /// (sync/…, mesh/…, node/…, activity/…, portal/…). These should NEVER
    /// be set on <see cref="AccessService.Context"/> or
    /// <see cref="AccessService.CircuitContext"/> — hub addresses are not
    /// user identities, and any leak into AccessContext produces the
    /// "CreatedBy=sync/xxx" symptom the user explicitly flagged on 2026-05-22.
    /// Used by <see cref="SetContext"/> / <see cref="SetCircuitContext"/> to
    /// log an error + stack trace so the leak source can be hunted down.
    /// </summary>
    public static bool LooksLikeHubPrincipal(string? objectId) =>
        !string.IsNullOrEmpty(objectId)
        && (objectId.StartsWith("sync/", StringComparison.OrdinalIgnoreCase)
            || objectId.StartsWith("mesh/", StringComparison.OrdinalIgnoreCase)
            || objectId.StartsWith("node/", StringComparison.OrdinalIgnoreCase)
            || objectId.StartsWith("activity/", StringComparison.OrdinalIgnoreCase)
            || objectId.StartsWith("portal/", StringComparison.OrdinalIgnoreCase));

    private readonly AsyncLocal<AccessContext?> context = new();

    /// <summary>
    /// Per-circuit user context, scoped via AsyncLocal.
    /// Set by CircuitAccessHandler at the start of each Blazor inbound activity
    /// and cleared in its finally block. This ensures each circuit's events
    /// see the correct user without cross-circuit contamination.
    /// In Orleans grains, this is always null (identity flows per-message only).
    /// </summary>
    private readonly AsyncLocal<AccessContext?> circuitContext = new();

    /// <summary>
    /// Standing identity for a SINGLE-IDENTITY host — a process that serves exactly one
    /// user for its whole lifetime: the MAUI device client and the xUnit test host. Those
    /// hosts have no Blazor circuit to set <see cref="circuitContext"/> per inbound activity,
    /// and an <c>AsyncLocal</c> written in an <c>async</c> setup method is discarded when that
    /// method returns — so they need a standing value that survives every hop.
    ///
    /// <para>🚨 A MULTI-USER SERVER MUST NEVER WRITE THIS. It is process-wide and
    /// last-writer-wins, so on a shared portal it would hand one user's identity to every
    /// other user's context-less read. That was exactly the cross-user identity bleed fixed
    /// here: <see cref="SetCircuitContext"/> used to write this field on every Blazor inbound
    /// activity and on every thread-hub activation, making <see cref="CircuitContext"/> resolve
    /// to "whoever touched the process last" on any thread where the AsyncLocal was unset.
    /// The only writer is <see cref="SetHostIdentity"/>, which no server code path calls —
    /// on a portal this stays <see langword="null"/> forever and identity resolution fails
    /// closed instead of falling back to a stranger.</para>
    ///
    /// <para>Per-hub standing identity (owner injection) is a DIFFERENT concept and lives in
    /// <see cref="standingIdentities"/> — scoped to the owning hub, never process-wide.</para>
    /// </summary>
    private AccessContext? hostIdentity;

    /// <summary>
    /// Per-hub standing identity — "owner injection": a per-node / thread / activity hub
    /// carries its node OWNER as the identity for work that runs on it after the live
    /// <c>AsyncLocal</c> has been wiped (a deferred sync write, an Rx continuation).
    /// Keyed by the owning hub's address, so hub A's owner can never be observed as hub B's
    /// caller — the bug that a single process-wide field produced.
    ///
    /// <para>Instance field on a mesh-scoped singleton, so its lifetime IS the mesh's and it
    /// dies with it (no <c>static</c>, no <c>Clear()</c> for test isolation).</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, AccessContext> standingIdentities = new();

    private readonly ILogger? _logger;

    /// <summary>
    /// Creates an access service with no logger. Hub-shaped-principal leak
    /// detection still runs but its diagnostics are silently dropped.
    /// </summary>
    public AccessService() { }

    /// <summary>
    /// Creates an access service that logs context transitions and leak
    /// diagnostics through the <c>MeshWeaver.AccessContext</c> category.
    /// </summary>
    /// <param name="loggerFactory">Factory used to create the diagnostic logger; may be null, in which case no logging occurs.</param>
    public AccessService(ILoggerFactory? loggerFactory)
    {
        _logger = loggerFactory?.CreateLogger("MeshWeaver.AccessContext");
    }

    /// <summary>
    /// Gets the current request-scoped access context (AsyncLocal only).
    /// Set by the delivery pipeline per-message. Returns null when no
    /// message delivery is active. Use <see cref="CircuitContext"/> for
    /// the persistent Blazor session context when needed.
    /// </summary>
    public AccessContext? Context => context.Value;

    /// <summary>
    /// Gets the circuit-level context: the AsyncLocal set by CircuitAccessHandler for the
    /// duration of each Blazor inbound activity, falling back to the
    /// <see cref="hostIdentity"/> of a single-identity host (MAUI / test host).
    ///
    /// <para>🚨 On a multi-user server <see cref="hostIdentity"/> is never written, so this
    /// resolves to the AsyncLocal ONLY and returns <see langword="null"/> off the circuit's own
    /// call tree — every hub action-block thread, Rx/IIoPool hop and background service. That
    /// null is deliberate: identity resolution FAILS CLOSED rather than handing back whichever
    /// user happened to touch the process last. Code that needs an identity across such a hop
    /// must carry it explicitly — <c>delivery.AccessContext</c>, <c>ICircuitContextAccessor.UserContext</c>
    /// (per-circuit), or <see cref="GetStandingIdentity"/> (per-hub owner injection).</para>
    ///
    /// In Orleans grains, this is always null (identity flows per-message only).
    /// </summary>
    public AccessContext? CircuitContext => circuitContext.Value ?? hostIdentity;

    /// <summary>
    /// Sets the request-specific context (AsyncLocal).
    /// Used during message delivery to temporarily set context.
    /// </summary>
    public void SetContext(AccessContext? accessContext)
    {
        var prev = context.Value?.ObjectId;
        context.Value = accessContext;
        if (LooksLikeHubPrincipal(accessContext?.ObjectId))
            _logger?.LogError(
                "SetContext: hub-shaped principal {ObjectId} set as AccessContext — must never happen. " +
                "Source stack:\n{Stack}",
                accessContext!.ObjectId, new StackTrace(skipFrames: 1, fNeedFileInfo: true).ToString());
        if (prev != accessContext?.ObjectId)
            _logger?.LogDebug("SetContext: {Previous} -> {Current}", prev ?? "(null)", accessContext?.ObjectId ?? "(null)");
    }

    /// <summary>
    /// Sets the circuit-level context — the <c>AsyncLocal</c> ONLY.
    /// Called by CircuitAccessHandler to establish per-circuit identity at the start of each
    /// Blazor inbound activity (and to clear it in the matching <c>finally</c>).
    ///
    /// <para>🚨 This deliberately does NOT write any process-wide field. It used to also assign
    /// <c>persistentCircuitContext</c>, which made every circuit activity publish its user
    /// process-wide: on a shared portal, user B's keystroke became the answer to user A's (and
    /// an anonymous visitor's) context-less identity reads on hub/Rx threads — the cross-user
    /// bleed. A single-identity host that genuinely has no circuit uses
    /// <see cref="SetHostIdentity"/>; a hub that needs its node owner as a standing identity
    /// uses <see cref="SetStandingIdentity"/>.</para>
    /// </summary>
    public void SetCircuitContext(AccessContext? accessContext)
    {
        var prev = circuitContext.Value?.ObjectId;
        circuitContext.Value = accessContext;

        if (LooksLikeHubPrincipal(accessContext?.ObjectId))
            _logger?.LogError(
                "SetCircuitContext: hub-shaped principal {ObjectId} set as CircuitContext — must never happen. " +
                "Source stack:\n{Stack}",
                accessContext!.ObjectId, new StackTrace(skipFrames: 1, fNeedFileInfo: true).ToString());
        if (prev != accessContext?.ObjectId)
            _logger?.LogDebug("SetCircuitContext: {Previous} -> {Current}", prev ?? "(null)", accessContext?.ObjectId ?? "(null)");
    }

    /// <summary>
    /// Sets the standing identity of a SINGLE-IDENTITY host — a process that serves exactly one
    /// user for its entire lifetime. The only legitimate callers are the MAUI device client
    /// (<c>MauiProgram</c>, the signed-in device user) and the xUnit test host
    /// (<c>TestUsers.DevLogin</c>). Both lack a Blazor circuit, so nothing sets the AsyncLocal
    /// per inbound activity, and an AsyncLocal written in an <c>async</c> setup method is
    /// discarded when that method returns.
    ///
    /// <para>🚨 NEVER call this from a multi-user server. The value is process-wide and
    /// last-writer-wins; on a shared portal it hands one user's identity to every other user's
    /// context-less read. Per-circuit identity is <see cref="SetCircuitContext"/> (AsyncLocal)
    /// plus <c>ICircuitContextAccessor.UserContext</c>; per-hub owner identity is
    /// <see cref="SetStandingIdentity"/>.</para>
    /// </summary>
    /// <param name="accessContext">The single user this process serves, or null to clear.</param>
    public void SetHostIdentity(AccessContext? accessContext)
    {
        var prev = hostIdentity?.ObjectId;
        hostIdentity = accessContext;
        // Keep the AsyncLocal in step for the calling flow, so a host that sets its identity
        // and immediately continues synchronously observes it through Context-less paths too.
        circuitContext.Value = accessContext;

        if (LooksLikeHubPrincipal(accessContext?.ObjectId))
            _logger?.LogError(
                "SetHostIdentity: hub-shaped principal {ObjectId} set as host identity — must never happen. " +
                "Source stack:\n{Stack}",
                accessContext!.ObjectId, new StackTrace(skipFrames: 1, fNeedFileInfo: true).ToString());
        if (prev != accessContext?.ObjectId)
            _logger?.LogDebug("SetHostIdentity: {Previous} -> {Current}", prev ?? "(null)", accessContext?.ObjectId ?? "(null)");
    }

    /// <summary>
    /// Clears the single-identity host's standing identity (see <see cref="SetHostIdentity"/>).
    /// </summary>
    public void ClearHostIdentity()
    {
        if (hostIdentity != null)
        {
            _logger?.LogDebug("ClearHostIdentity: {Previous} -> (null)", hostIdentity.ObjectId);
            hostIdentity = null;
        }
        circuitContext.Value = null;
    }

    /// <summary>
    /// Records the standing identity of <paramref name="hub"/> — "owner injection". A per-node,
    /// thread or activity hub carries its node OWNER as the identity for work that runs on it
    /// after the live AsyncLocal has been wiped (a deferred data-source sync write, an Rx
    /// continuation). Scoped to the owning hub's address, so one hub's owner can never be
    /// observed as another hub's caller.
    /// </summary>
    /// <param name="hub">The hub whose standing identity is being set.</param>
    /// <param name="accessContext">The owner identity, or null to clear it.</param>
    public void SetStandingIdentity(IMessageHub hub, AccessContext? accessContext)
    {
        var key = hub.Address.ToFullString();
        if (accessContext is null)
        {
            standingIdentities.TryRemove(key, out _);
            return;
        }
        if (LooksLikeHubPrincipal(accessContext.ObjectId))
        {
            _logger?.LogError(
                "SetStandingIdentity: hub-shaped principal {ObjectId} set as the standing identity of {Hub} — " +
                "must never happen. Source stack:\n{Stack}",
                accessContext.ObjectId, key, new StackTrace(skipFrames: 1, fNeedFileInfo: true).ToString());
            return;
        }
        standingIdentities[key] = accessContext;
        _logger?.LogDebug("SetStandingIdentity: {Hub} -> {Current}", key, accessContext.ObjectId);
    }

    /// <summary>
    /// Returns the standing owner identity recorded for <paramref name="hub"/> by
    /// <see cref="SetStandingIdentity"/>, or null when none was established.
    /// </summary>
    public AccessContext? GetStandingIdentity(IMessageHub hub)
        => standingIdentities.TryGetValue(hub.Address.ToFullString(), out var ctx) ? ctx : null;

    /// <summary>
    /// Temporarily switches the access context. Restores the previous value when disposed.
    /// Usage: using (accessService.SwitchAccessContext(newContext)) { ... }
    /// </summary>
    public IDisposable SwitchAccessContext(AccessContext? newContext)
        => new AccessContextScope(this, newContext);

    /// <summary>
    /// Temporarily sets the access context to the hub's identity.
    /// Restores the previous AsyncLocal value when disposed (does not affect circuitContext).
    /// Usage: using (accessService.ImpersonateAsHub(hub)) { ... }
    /// </summary>
    public IDisposable ImpersonateAsHub(IMessageHub hub)
    {
        return new AccessContextScope(this, new AccessContext
        {
            ObjectId = hub.Address.ToFullString(),
            Name = hub.Address.ToString(),
            IsHub = true
        });
    }

    /// <summary>
    /// Temporarily sets the access context to the well-known System identity.
    /// SecurityService grants <c>Permission.All</c> to System unconditionally,
    /// so this scope is the right answer for infrastructure operations that
    /// need to bypass RLS — e.g., the per-token hub reading its own
    /// MeshNode during token validation, where the caller is, by design,
    /// unauthenticated. Restores the previous AsyncLocal value on dispose.
    /// </summary>
    /// <remarks>
    /// Use sparingly. Wrapping general application code in this scope is a
    /// security smell — the right shape for normal user flows is to plumb
    /// the user's <c>AccessContext</c> through the message-level
    /// <c>delivery.AccessContext</c>, not to bypass RLS.
    /// </remarks>
    public IDisposable ImpersonateAsSystem()
    {
        // The literal must match `MeshWeaver.Mesh.Security.WellKnownUsers.System`;
        // we don't reference that constant here because Messaging.Hub sits below
        // Mesh.Contract in the project graph and adding the dep would invert it.
        const string SystemObjectId = "system-security";
        return new AccessContextScope(this, new AccessContext
        {
            ObjectId = SystemObjectId,
            Name = SystemObjectId
        });
    }

    private sealed class AccessContextScope : IDisposable
    {
        private readonly AccessService service;
        private readonly AccessContext? previousAsyncLocal;

        public AccessContextScope(AccessService service, AccessContext? newContext)
        {
            this.service = service;
            previousAsyncLocal = service.context.Value;
            service.SetContext(newContext);
        }

        public void Dispose() => service.SetContext(previousAsyncLocal);
    }
}
