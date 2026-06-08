using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;

namespace MeshWeaver.Data;

public abstract record TypeSource<TTypeSource> : ITypeSource
    where TTypeSource : TypeSource<TTypeSource>
{


    private readonly ITypeRegistry typeRegistry = null!;
    protected TypeSource(IWorkspace workspace, Type type)
    {
        typeRegistry = workspace.Hub.TypeRegistry;
        TypeDefinition = typeRegistry.GetTypeDefinition(type, typeName:type.Name)!;
    }

    public ITypeDefinition TypeDefinition { get; init; } = null!; 

    protected TTypeSource This => (TTypeSource)this;

    public virtual InstanceCollection Update(ChangeItem<EntityStore> changeItem)
    {
        if(changeItem.Value is null)
            throw new ArgumentNullException(nameof(changeItem), "Change item value cannot be null.");

        var myCollection = changeItem.Value.Reduce(new CollectionReference(TypeDefinition.CollectionName));

        return UpdateImpl(myCollection);
    }

    private IDisposable? workspaceSubscription;

    protected virtual InstanceCollection UpdateImpl(InstanceCollection myCollection) =>
        myCollection;

    ITypeSource ITypeSource.WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            IObservable<IEnumerable<object>>
        > initialization
    ) => WithInitialData(initialization);

    public TTypeSource WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            IObservable<IEnumerable<object>>
        > initialization
    ) => This with { InitializationFunction = initialization };

    protected Func<
        WorkspaceReference<InstanceCollection>,
        IObservable<IEnumerable<object>>
    > InitializationFunction { get; init; } = _ => Observable.Return(Enumerable.Empty<object>());

    IObservable<InstanceCollection> ITypeSource.Initialize(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    ) => Initialize(reference, cancellationToken);

    /// <summary>
    /// Reactive initialization — pure <see cref="IObservable{T}"/> composition. The
    /// <see cref="InitializationFunction"/> is itself reactive (no <c>Task</c>, no
    /// <c>Observable.FromAsync</c>); subclasses that touch real I/O override this and bridge the
    /// leaf reactively. <paramref name="cancellationToken"/> is unused — the subscription's
    /// lifetime is the cancellation.
    /// </summary>
    protected virtual IObservable<InstanceCollection> Initialize(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    ) => InitializationFunction(reference)
        .Select(items => new InstanceCollection(items, TypeDefinition.GetKey));

    public void Dispose()
    {
        workspaceSubscription?.Dispose();
    }

    public string CollectionName => TypeDefinition.CollectionName;

    public TTypeSource WithKey(KeyFunction keyFunc)
    {
        return This with { TypeDefinition = typeRegistry.WithKeyFunction(TypeDefinition.CollectionName, keyFunc) };
    }

    /// <summary>
    /// Access restrictions specific to this type.
    /// Evaluated after global restrictions.
    /// </summary>
    public ImmutableList<AccessRestrictionEntry> AccessRestrictions { get; init; } =
        ImmutableList<AccessRestrictionEntry>.Empty;
}
