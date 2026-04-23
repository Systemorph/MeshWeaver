namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Placeholder namespace anchor. The `*Async` convenience shims on
/// <see cref="IMeshService"/> (CreateNodeAsync, UpdateNodeAsync, DeleteNodeAsync,
/// CreateTransientAsync) have been removed — see
/// <c>Doc/Architecture/AsynchronousCalls</c>. Call the Observable methods on
/// <see cref="IMeshService"/> directly. Bridges to <c>Task</c> at genuine
/// async/await boundaries (tests, one-shot CLI exporters) go via
/// <see cref="System.Reactive.Threading.Tasks.TaskObservableExtensions.ToTask{TResult}(System.IObservable{TResult}, System.Threading.CancellationToken)"/>.
/// </summary>
internal static class MeshServiceExtensions
{
}
