namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Task-returning extension methods for IMeshService CRUD operations.
/// These provide backward-compatible await-based API on top of the Observable methods.
/// All ~180 existing callers (await meshService.CreateNodeAsync(...)) resolve here
/// without any code changes.
/// </summary>
public static class MeshServiceExtensions
{
    /// <summary>
    /// Creates a node asynchronously via the mesh service.
    /// </summary>
    public static Task<MeshNode> CreateNodeAsync(
        this IMeshService service, MeshNode node, CancellationToken ct = default)
        => ToTask(service.CreateNode(node), ct);

    /// <summary>
    /// Updates a node asynchronously via the mesh service.
    /// </summary>
    public static Task<MeshNode> UpdateNodeAsync(
        this IMeshService service, MeshNode node, CancellationToken ct = default)
        => ToTask(service.UpdateNode(node), ct);

    /// <summary>
    /// Deletes a node asynchronously via the mesh service.
    /// </summary>
    public static Task DeleteNodeAsync(
        this IMeshService service, string path, CancellationToken ct = default)
        => ToTask<bool>(service.DeleteNode(path), ct);

    /// <summary>
    /// Converts an observable to a task that completes with the first emitted value.
    ///
    /// <para>🚨 This is a hand-rolled observable→<see cref="Task"/> bridge, and it is retained
    /// debt rather than a pattern to copy. <c>MeshServiceHasNoTaskShimGuard</c> holds it to exactly
    /// this one assembly and these three <c>*Async</c> verbs; the exit is to port the in-mesh
    /// callers to <c>CreateNode(...).Subscribe(...)</c> and then move the shim beside the test-only
    /// bridge in <c>MeshWeaver.Fixture</c> (MeshWeaver.Reinsurance issue #102). New code waits
    /// through <c>MeshWeaver.Messaging.ReactiveCompletion.ObserveCompletion</c> instead.</para>
    ///
    /// <para>🚨 <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> is LOAD-BEARING,
    /// not tidiness. Without it — which is how this method was written until 2026-08-30 —
    /// <c>TrySetResult</c> fires from inside <see cref="SingleObserver{T}.OnNext"/>, i.e. from
    /// INSIDE the Rx pipeline on whichever thread signalled, and resumes the awaiting caller
    /// <b>inline</b> on that thread: a hub's action block, a grain's turn scheduler, an Rx
    /// trampoline. The caller then finishes its work there, holding a scheduler that the work it is
    /// about to wait on needs — the deadlock mechanism behind #2377 and #2301. It is sticky too:
    /// <c>await</c> captures <see cref="TaskScheduler.Current"/> absent a
    /// <see cref="System.Threading.SynchronizationContext"/>, so every later <c>await</c> in the
    /// same method schedules onto it as well. That mattered here more than anywhere: every caller
    /// left in the fleet is an <c>await</c> on hub-reachable in-mesh layout-area code, which is
    /// exactly the position where an inline resumption wedges a turn.</para>
    ///
    /// <para>⚠️ Known, deliberately unchanged: <see cref="SingleObserver{T}.OnCompleted"/> does not
    /// settle, so a source that completes WITHOUT emitting leaves this task pending forever. Fixing
    /// it is a behaviour change (the wait would start yielding <c>default</c>) and belongs with the
    /// port above, not with the inline-resumption fix.</para>
    /// </summary>
    public static Task<T> ToTask<T>(IObservable<T> observable, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = observable.Subscribe(new SingleObserver<T>(tcs));
        if (ct.CanBeCanceled)
            ct.Register(() => { tcs.TrySetCanceled(); sub.Dispose(); });
        return tcs.Task;
    }

    private sealed class SingleObserver<T>(TaskCompletionSource<T> tcs) : IObserver<T>
    {
        public void OnNext(T value) => tcs.TrySetResult(value);
        public void OnError(Exception error) => tcs.TrySetException(error);
        public void OnCompleted() { }
    }
}
