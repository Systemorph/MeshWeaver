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

}
