using System.Text.Json;

namespace MeshWeaver.Data.Persistence;


public record UnpartitionedHubDataSource(object Id, IWorkspace Workspace) : UnpartitionedDataSource<UnpartitionedHubDataSource,ITypeSource>(Id, Workspace)
{
    protected JsonSerializerOptions Options => Hub.JsonSerializerOptions;
    public override UnpartitionedHubDataSource WithType<T>(Func<ITypeSource, ITypeSource> typeSource) =>
        WithType<T>(x => (TypeSourceWithType<T>)typeSource.Invoke(x));

    public UnpartitionedHubDataSource WithType<T>(
        Func<TypeSourceWithType<T>, TypeSourceWithType<T>> typeSource
    ) => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Workspace, Id)));

    protected override ISynchronizationStream<EntityStore> CreateStream(StreamIdentity identity) => 
        Workspace.GetRemoteStream(Id, GetReference());
}
