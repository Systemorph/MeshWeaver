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
    /// Creates a transient node asynchronously via the mesh service.
    /// </summary>
    public static Task<MeshNode> CreateTransientAsync(
        this IMeshService service, MeshNode node, CancellationToken ct = default)
        => ToTask(service.CreateTransient(node), ct);

    /// <summary>
    /// Converts an observable to a task that completes with the first emitted value.
    /// </summary>
    public static Task<T> ToTask<T>(IObservable<T> observable, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>();
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
