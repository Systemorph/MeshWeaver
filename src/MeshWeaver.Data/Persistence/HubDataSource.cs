using System.Text.Json;

namespace MeshWeaver.Data.Persistence;

public abstract record HubDataSourceBase<TDataSource>(object Id, IWorkspace Workspace)
    : DataSource<TDataSource>(Id, Workspace)
    where TDataSource : HubDataSourceBase<TDataSource>
{
    protected JsonSerializerOptions Options => Hub.JsonSerializerOptions;
}

public record HubDataSource : HubDataSourceBase<HubDataSource>
{
    protected override HubDataSource WithType<T>(Func<ITypeSource, ITypeSource> typeSource) =>
        WithType<T>(x => (TypeSourceWithType<T>)typeSource.Invoke(x));

    public HubDataSource WithType<T>(
        Func<TypeSourceWithType<T>, TypeSourceWithType<T>> typeSource
    ) => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Workspace, Id)));

    public HubDataSource(object Id, IWorkspace Workspace)
        : base(Id, Workspace) { }

    public override void Initialize(WorkspaceState state)
    {
        var reference = new CollectionsReference(
            TypeSources.Values.Select(ts => ts.CollectionName).ToArray()
        );
        Streams = Streams.Add(Workspace.GetRemoteStream(Id, reference));
        base.Initialize(state);
    }
}
