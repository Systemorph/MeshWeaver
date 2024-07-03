using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Data;

public abstract record TypeSource<TTypeSource> : ITypeSource
    where TTypeSource : TypeSource<TTypeSource>
{
    protected TypeSource(IWorkspace workspace, Type ElementType, object DataSource)
    {
        this.ElementType = ElementType;
        this.DataSource = DataSource;
        Workspace = workspace;
        var typeRegistry = Workspace
            .Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
            .WithType(ElementType);
        CollectionName = typeRegistry.TryGetTypeName(ElementType, out var typeName)
            ? typeName
            : ElementType.FullName;

        typeRegistry.WithType(ElementType);
        Key = typeRegistry.GetKeyFunction(CollectionName);
    }


    public virtual object GetKey(object instance) =>
        Key.Function?.Invoke(instance)
        ?? throw new DataSourceConfigurationException(
            "No key mapping is defined. Please specify in the configuration of the data sources source.");

    protected KeyFunction Key { get; init; }

    protected TTypeSource This => (TTypeSource)this;
    protected IWorkspace Workspace { get; }

    public virtual InstanceCollection Update(ChangeItem<EntityStore> workspace)
    {
        var myCollection = workspace.Value.Reduce(new CollectionReference(CollectionName));

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

    public Type ElementType { get; init; }
    public object DataSource { get; init; }
    public string CollectionName { get; init; }

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
            GetKey
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
}
