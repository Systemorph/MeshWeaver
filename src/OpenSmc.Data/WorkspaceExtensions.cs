using System.Reactive.Linq;

namespace OpenSmc.Data;

public static class WorkspaceExtensions
{
    public static IReadOnlyCollection<T> GetData<T>(this WorkspaceState state)
        => state.Reduce(new CollectionReference(typeof(T).FullName))?.Instances.Values.Cast<T>().ToArray();
    public static IReadOnlyCollection<T> GetData<T>(this IWorkspace workspace)
        => workspace.State.GetData<T>();
    public static T GetData<T>(this WorkspaceState state, object id)
        => (T)state.Reduce(new EntityReference(typeof(T).FullName, id));
    public static T GetData<T>(this IWorkspace workspace, object id)
        => workspace.State.GetData<T>(id);
    public static IObservable<T> GetObservable<T>(this IWorkspace workspace, object id)
        => workspace.Stream.StartWith(workspace.State).Select(ws => ws.GetData<T>(id)).Replay(1).RefCount();
    public static IObservable<IReadOnlyCollection<T>> GetObservable<T>(this IWorkspace workspace)
        => workspace.Stream.StartWith(workspace.State).Select(ws => ws.GetData<T>()).Replay(1).RefCount();

    public static WorkspaceReference Observe(this IWorkspace workspace, WorkspaceReference reference)
        => ObserveImpl(workspace, (dynamic)reference);

    private static WorkspaceReference<TReference> ObserveImpl<TReference>(this IWorkspace workspace, WorkspaceReference<TReference> reference)
    {
        return reference with
        {
            Stream = workspace.Stream
                .Select(ws => ws.Reduce(reference))
                .DistinctUntilChanged()
                .Replay(1)
                .RefCount()
        };

    }
}