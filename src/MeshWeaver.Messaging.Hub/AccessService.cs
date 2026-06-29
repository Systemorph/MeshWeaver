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
    /// Persistent fallback for circuit context. Used in test scenarios where
    /// SetCircuitContext is called in InitializeAsync but the AsyncLocal value
    /// doesn't flow to the test method (ExecutionContext copy-on-write).
    /// In production Blazor, CircuitAccessHandler always sets the AsyncLocal
    /// via CreateInboundActivityHandler, so this fallback is never reached.
    /// </summary>
    private AccessContext? persistentCircuitContext;

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
    /// Gets the circuit-level context. Returns the AsyncLocal value if set
    /// (by CircuitAccessHandler per inbound activity), otherwise falls back
    /// to the persistent context (set by test setup or initial authentication).
    /// In Orleans grains, this is always null (identity flows per-message only).
    /// </summary>
    public AccessContext? CircuitContext => circuitContext.Value ?? persistentCircuitContext;

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
    /// Sets the circuit-level context (AsyncLocal).
    /// Called by CircuitAccessHandler to restore per-circuit identity
    /// at the start of each Blazor inbound activity.
    /// When setting a non-null context, also stores as persistent fallback
    /// for scenarios where AsyncLocal doesn't flow (test setup, non-Blazor).
    /// </summary>
    public void SetCircuitContext(AccessContext? accessContext)
    {
        var prev = circuitContext.Value?.ObjectId;
        circuitContext.Value = accessContext;
        // Sync the persistent fallback so SetCircuitContext(null) truly clears identity.
        // This ensures test code (SetCircuitContext in InitializeAsync) persists
        // across async context boundaries where AsyncLocal doesn't flow.
        // In production Blazor, CircuitAccessHandler always sets the AsyncLocal
        // per inbound activity, so the persistent fallback is never reached.
        persistentCircuitContext = accessContext;

        if (LooksLikeHubPrincipal(accessContext?.ObjectId))
            _logger?.LogError(
                "SetCircuitContext: hub-shaped principal {ObjectId} set as CircuitContext — must never happen. " +
                "Source stack:\n{Stack}",
                accessContext!.ObjectId, new StackTrace(skipFrames: 1, fNeedFileInfo: true).ToString());
        if (prev != accessContext?.ObjectId)
            _logger?.LogDebug("SetCircuitContext: {Previous} -> {Current}", prev ?? "(null)", accessContext?.ObjectId ?? "(null)");
    }

    /// <summary>
    /// Clears the persistent circuit context fallback.
    /// Called by CircuitAccessHandler on circuit close to prevent stale context.
    /// </summary>
    public void ClearPersistentCircuitContext()
    {
        if (persistentCircuitContext != null)
        {
            _logger?.LogDebug("ClearPersistentCircuitContext: {Previous} -> (null)", persistentCircuitContext.ObjectId);
            persistentCircuitContext = null;
        }
    }

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
