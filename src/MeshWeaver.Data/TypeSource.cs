using MeshWeaver.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Data;

public abstract record TypeSource<TTypeSource> : ITypeSource
    where TTypeSource : TypeSource<TTypeSource>
{


    private readonly ITypeRegistry typeRegistry;
    protected TypeSource(IWorkspace workspace, object dataSource, Type type)
    {
        typeRegistry = workspace.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        TypeDefinition = typeRegistry.GetTypeDefinition(type);
    }

    public ITypeDefinition TypeDefinition { get; init; } 

    protected TTypeSource This => (TTypeSource)this;

    public virtual InstanceCollection Update(ChangeItem<EntityStore> changeItem)
    {
        var myCollection = changeItem.Value.Reduce(new CollectionReference(TypeDefinition.CollectionName));

        return UpdateImpl(myCollection);
    }

    private IDisposable workspaceSubscription;

    protected virtual InstanceCollection UpdateImpl(InstanceCollection myCollection) =>
        myCollection;

    ITypeSource ITypeSource.WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            CancellationToken,
            Task<IEnumerable<object>>
        > initialization
    ) => WithInitialData(initialization);

    public TTypeSource WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            CancellationToken,
            Task<IEnumerable<object>>
        > initialization
    ) => This with { InitializationFunction = initialization };

    protected Func<
        WorkspaceReference<InstanceCollection>,
        CancellationToken,
        Task<IEnumerable<object>>
    > InitializationFunction { get; init; } = (_, _) => Task.FromResult(Enumerable.Empty<object>());

    Task<InstanceCollection> ITypeSource.InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    ) => InitializeAsync(reference, cancellationToken);

    protected virtual async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    )
    {
        return new InstanceCollection(
            await InitializeDataAsync(reference, cancellationToken),
            TypeDefinition.GetKey
        );
    }

    private Task<IEnumerable<object>> InitializeDataAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    ) => InitializationFunction(reference, cancellationToken);

    public void Dispose()
    {
        workspaceSubscription?.Dispose();
    }

    public string CollectionName => TypeDefinition.CollectionName;

    public TTypeSource WithKey(KeyFunction keyFunc)
    {
        return This with { TypeDefinition = typeRegistry.WithKeyFunction(TypeDefinition.CollectionName, keyFunc) };
    }


}
