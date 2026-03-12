namespace MeshWeaver.Messaging;

public class AccessService
{
    private readonly AsyncLocal<AccessContext?> context = new();

    /// <summary>
    /// Persistent user context for the circuit/session.
    /// This persists across SignalR calls within the same Blazor circuit.
    /// </summary>
    private AccessContext? circuitContext;

    /// <summary>
    /// Gets the current access context.
    /// First checks the AsyncLocal (for request-specific overrides),
    /// then falls back to the circuit-level context.
    /// </summary>
    public AccessContext? Context => context.Value ?? circuitContext;

    /// <summary>
    /// Sets the request-specific context (AsyncLocal).
    /// Used during message delivery to temporarily set context.
    /// </summary>
    public void SetContext(AccessContext? accessContext)
    {
        context.Value = accessContext;
    }

    /// <summary>
    /// Sets the persistent circuit-level context.
    /// This should be called during initial authentication to persist
    /// the user context across SignalR calls within the circuit.
    /// </summary>
    public void SetCircuitContext(AccessContext? accessContext)
    {
        circuitContext = accessContext;
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
