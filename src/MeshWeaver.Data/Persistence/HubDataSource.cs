using System.Text.Json;
using MeshWeaver.Data.Serialization;

namespace MeshWeaver.Data.Persistence;

public abstract record HubDataSourceBase<TDataSource>(object Id, IWorkspace Workspace)
    : DataSource<TDataSource>(Id, Workspace)
    where TDataSource : HubDataSourceBase<TDataSource>
{
    protected JsonSerializerOptions Options => Hub.JsonSerializerOptions;
}

public record HubDataSource(object Id, IWorkspace Workspace) : HubDataSourceBase<HubDataSource>(Id, Workspace)
{
    protected override HubDataSource WithType<T>(Func<ITypeSource, ITypeSource> typeSource) =>
        WithType<T>(x => (TypeSourceWithType<T>)typeSource.Invoke(x));

    public HubDataSource WithType<T>(
        Func<TypeSourceWithType<T>, TypeSourceWithType<T>> typeSource
    ) => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Workspace, Id)));

    protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity) => 
        Workspace.GetRemoteStream(Id, GetReference());
}
