using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Infrastructure;

/// <summary>
/// Circuit-scoped holder for the Blazor interactive circuit's stable id AND the
/// resolved circuit user.
///
/// <para>Registered <c>Scoped</c>. In Blazor Server every interactive circuit gets
/// exactly ONE DI scope, and both the circuit's <c>CircuitHandler</c> instances and
/// every interactive component's <c>[Inject]</c> dependencies are resolved from that
/// single scope. So within one circuit there is exactly one
/// <see cref="CircuitContextAccessor"/> instance, shared by the circuit handler that
/// writes <see cref="CircuitId"/> / <see cref="UserContext"/> and by
/// <see cref="PortalApplication"/> that reads them.</para>
///
/// <para><see cref="CircuitId"/> is set exactly once, from <c>Circuit.Id</c>, by the
/// circuit handler in <c>OnCircuitOpenedAsync</c> — which the framework runs before any
/// component in the circuit is initialized. It is therefore stable for the whole lifetime
/// of the tab and unique per tab (Blazor mints a fresh <c>Circuit.Id</c> per circuit).</para>
///
/// <para><see cref="UserContext"/> is the per-circuit user identity. It is set by the
/// circuit handler when it resolves the authenticated user (and updated after onboarding).
/// This is the SOURCE that the per-circuit portal hub stamps on every message it posts.
/// Without it, the portal hub posts on its own action-block thread — where the
/// <c>AccessService</c>'s AsyncLocals are wiped and the mesh-wide
/// <c>persistentCircuitContext</c> has been cleared by the inbound-activity finally block —
/// so its <c>SubscribeRequest</c>s for layout / agent / model streams carry a NULL
/// <c>AccessContext</c>, RLS denies, and the agent registry returns <c>[]</c>. Per-circuit
/// (not mesh-wide), so two circuits never clobber each other's user. See
/// <c>feedback_access_context_always_set</c>.</para>
///
/// <para>On non-circuit DI scopes (SSR / prerender HTTP requests, middleware, the root
/// service provider) no circuit handler ever runs, so <see cref="CircuitId"/> and
/// <see cref="UserContext"/> stay <see langword="null"/>. That null is the discriminator
/// the portal uses to fall back to the user-identity address. There is no other writer,
/// so the value can never differ between two reads within the same scope.</para>
/// </summary>
public interface ICircuitContextAccessor
{
    /// <summary>
    /// The stable circuit id for this scope, or <see langword="null"/> when this scope
    /// is not an interactive Blazor circuit (SSR / prerender / middleware / root).
    /// </summary>
    string? CircuitId { get; }

    /// <summary>
    /// The resolved user identity for this circuit, or <see langword="null"/> when this
    /// scope is not an interactive Blazor circuit, or before the circuit handler has
    /// resolved the user. Read by <see cref="PortalApplication"/> so the per-circuit
    /// portal hub can stamp the circuit user on every post regardless of which thread
    /// the post lands on.
    /// </summary>
    AccessContext? UserContext { get; }

    /// <summary>
    /// Records the circuit id. Called once per circuit by the circuit handler on open.
    /// Idempotent: a second call with the same id is a no-op; the value is never mutated
    /// after it is first set.
    /// </summary>
    void SetCircuitId(string circuitId);

    /// <summary>
    /// Records (or updates) the circuit user. Called by the circuit handler when it
    /// resolves the authenticated user on open and again after onboarding refines the
    /// identity. Unlike <see cref="SetCircuitId"/> this is NOT write-once — onboarding
    /// legitimately replaces an anonymous/seed identity with the resolved username.
    /// </summary>
    void SetUserContext(AccessContext? userContext);
}

/// <inheritdoc />
public sealed class CircuitContextAccessor : ICircuitContextAccessor
{
    public string? CircuitId { get; private set; }

    public AccessContext? UserContext { get; private set; }

    public void SetCircuitId(string circuitId)
    {
        // Write-once. The circuit handler opens a circuit exactly once, but guard anyway
        // so a re-entrant or duplicate call can never flip the id mid-circuit (which would
        // re-introduce the multi-portal bug).
        CircuitId ??= circuitId;
    }

    public void SetUserContext(AccessContext? userContext) => UserContext = userContext;
}
