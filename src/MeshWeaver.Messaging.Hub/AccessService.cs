using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public class AccessService
{
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

    public AccessService() { }

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
        // When setting a non-null context, also store as persistent fallback.
        // This ensures test code (SetCircuitContext in InitializeAsync) persists
        // across async context boundaries where AsyncLocal doesn't flow.
        // In production Blazor, CircuitAccessHandler always sets the AsyncLocal
        // per inbound activity, so the persistent fallback is never reached.
        if (accessContext != null)
            persistentCircuitContext = accessContext;
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
    /// Temporarily sets the access context to the hub's identity.
    /// Restores the previous AsyncLocal value when disposed (does not affect circuitContext).
    /// Usage: using (accessService.ImpersonateAsHub(hub)) { ... }
    /// </summary>
    public IDisposable ImpersonateAsHub(IMessageHub hub)
    {
        return new ImpersonationScope(this, new AccessContext
        {
            ObjectId = hub.Address.ToFullString(),
            Name = hub.Address.ToString()
        });
    }

    private sealed class ImpersonationScope : IDisposable
    {
        private readonly AccessService service;
        private readonly AccessContext? previousAsyncLocal;

        public ImpersonationScope(AccessService service, AccessContext hubContext)
        {
            this.service = service;
            // Capture only the raw AsyncLocal value, not the circuitContext fallback.
            // On dispose we restore exactly the AsyncLocal value, so circuitContext
            // changes made during the scope are not masked.
            previousAsyncLocal = service.context.Value;
            service.SetContext(hubContext);
        }

        public void Dispose() => service.SetContext(previousAsyncLocal);
    }
}
