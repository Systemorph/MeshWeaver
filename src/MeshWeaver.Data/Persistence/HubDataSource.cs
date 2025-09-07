using System.Text.Json;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Persistence;


public record UnpartitionedHubDataSource(Address Address, IWorkspace Workspace) : UnpartitionedDataSource<UnpartitionedHubDataSource,ITypeSource>(Address, Workspace)
{
    protected JsonSerializerOptions Options => Hub.JsonSerializerOptions;
    public override UnpartitionedHubDataSource WithType<T>(Func<ITypeSource, ITypeSource>? typeSource) =>
        WithType<T>(x => (TypeSourceWithType<T>)(typeSource ?? (y => y)).Invoke(x));

    public UnpartitionedHubDataSource WithType<T>(
        Func<TypeSourceWithType<T>, TypeSourceWithType<T>> typeSource
    ) => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Workspace, Id)));

    protected override ISynchronizationStream<EntityStore>? CreateStream(StreamIdentity identity)
    {
        return CreateStream(identity, x => x);
    }

    protected override ISynchronizationStream<EntityStore>? CreateStream(StreamIdentity identity, Func<StreamConfiguration<EntityStore>, StreamConfiguration<EntityStore>> config) => 
        Workspace.GetRemoteStream(Address, GetReference());
}
