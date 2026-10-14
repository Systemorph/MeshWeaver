namespace MeshWeaver.Messaging;

/// <summary>
/// The one teardown a HOST runs when it deactivates the activation that carries a hub — an Orleans
/// grain deactivating, a host retiring the hub it owns — and that also wants the turn currently on
/// the hub's action block aborted rather than drained.
///
/// <para><b>The order is the whole point (#6648).</b> <see cref="System.IDisposable.Dispose"/> runs
/// FIRST, and only then <see cref="IMessageHub.CancelCurrentExecution"/>. Cancelling a hub's
/// execution token fires its registrations SYNCHRONOUSLY inside
/// <see cref="System.Threading.CancellationTokenSource.Cancel()"/>. For a hub that has not finished
/// starting, the turn holding that token is its own <c>InitializeHubRequest</c>, whose
/// <c>HandleInitialize</c> races the BuildupActions against it and classifies the resulting
/// <see cref="System.OperationCanceledException"/> by asking <c>IsShuttingDown</c>. Cancelling
/// before disposing therefore asks that question while the answer is still <c>false</c>: the
/// routine end of an activation was logged at Error as "initialization failed … Hub is now in
/// FAILED state" and the hub latched FAILED on its way out — measured as one Error line per
/// mid-initialization hub on every grain-deactivation wave of an Orleans-hosted portal.
/// <c>Dispose()</c> sets the disposal flag before it cancels a not-yet-started hub's bring-up
/// itself, so disposing first makes the classification true by construction; the cancel that
/// follows still aborts the in-flight turn of a STARTED hub, which <c>Dispose()</c> deliberately
/// leaves to finish (its <c>ShutdownRequest</c> runs with no token, so the cancel cannot reach it).</para>
/// </summary>
public static class HubDeactivation
{
    /// <summary>
    /// Begins <paramref name="hub"/>'s teardown and then aborts whatever turn it is running — the
    /// abort also when starting the teardown throws (the exception still propagates).
    /// Returns as soon as the teardown has STARTED; join <see cref="IMessageHub.DisposalCompleted"/>
    /// to observe its end.
    /// </summary>
    /// <param name="hub">The hub whose activation is ending.</param>
    public static void DisposeAbortingCurrentTurn(this IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        // The abort runs even when the teardown faults while starting: a host that catches the
        // throw and finishes deactivating (MessageHubGrain.OnDeactivateAsync does) must not leave
        // the hub's turn running after its activation is gone.
        try
        {
            hub.Dispose();
        }
        finally
        {
            hub.CancelCurrentExecution();
        }
    }
}
