namespace MeshWeaver.Blazor.Infrastructure;

/// <summary>
/// Circuit-scoped holder for the Blazor interactive circuit's stable id.
///
/// <para>Registered <c>Scoped</c>. In Blazor Server every interactive circuit gets
/// exactly ONE DI scope, and both the circuit's <c>CircuitHandler</c> instances and
/// every interactive component's <c>[Inject]</c> dependencies are resolved from that
/// single scope. So within one circuit there is exactly one
/// <see cref="CircuitContextAccessor"/> instance, shared by the circuit handler that
/// writes <see cref="CircuitId"/> and by <see cref="PortalApplication"/> that reads it.</para>
///
/// <para><see cref="CircuitId"/> is set exactly once, from <c>Circuit.Id</c>, by the
/// circuit handler in <c>OnCircuitOpenedAsync</c> — which the framework runs before any
/// component in the circuit is initialized. It is therefore stable for the whole lifetime
/// of the tab and unique per tab (Blazor mints a fresh <c>Circuit.Id</c> per circuit).</para>
///
/// <para>On non-circuit DI scopes (SSR / prerender HTTP requests, middleware, the root
/// service provider) no circuit handler ever runs, so <see cref="CircuitId"/> stays
/// <see langword="null"/>. That null is the discriminator the portal uses to fall back to
/// the user-identity address. There is no other writer, so the value can never differ
/// between two reads within the same scope.</para>
/// </summary>
public interface ICircuitContextAccessor
{
    /// <summary>
    /// The stable circuit id for this scope, or <see langword="null"/> when this scope
    /// is not an interactive Blazor circuit (SSR / prerender / middleware / root).
    /// </summary>
    string? CircuitId { get; }

    /// <summary>
    /// Records the circuit id. Called once per circuit by the circuit handler on open.
    /// Idempotent: a second call with the same id is a no-op; the value is never mutated
    /// after it is first set.
    /// </summary>
    void SetCircuitId(string circuitId);
}

/// <inheritdoc />
public sealed class CircuitContextAccessor : ICircuitContextAccessor
{
    public string? CircuitId { get; private set; }

    public void SetCircuitId(string circuitId)
    {
        // Write-once. The circuit handler opens a circuit exactly once, but guard anyway
        // so a re-entrant or duplicate call can never flip the id mid-circuit (which would
        // re-introduce the multi-portal bug).
        CircuitId ??= circuitId;
    }
}
