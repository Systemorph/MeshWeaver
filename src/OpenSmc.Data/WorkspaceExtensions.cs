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
}