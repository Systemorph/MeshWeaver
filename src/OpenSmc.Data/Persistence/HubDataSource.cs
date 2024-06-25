using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Data.Persistence;

public abstract record HubDataSourceBase<TDataSource> : DataSource<TDataSource>
    where TDataSource : HubDataSourceBase<TDataSource>
{
    private readonly ITypeRegistry typeRegistry;
    protected JsonSerializerOptions Options => Hub.JsonSerializerOptions;

    protected HubDataSourceBase(object Id, IWorkspace Workspace)
        : base(Id, Workspace)
    {
        typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
    }
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
        Streams = Streams.Add(Workspace.GetStream(Id, reference));
        base.Initialize(state);
    }
}
