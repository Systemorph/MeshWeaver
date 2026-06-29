using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;

namespace MeshWeaver.Data;

/// <summary>
/// Abstract base for type sources, providing key/collection metadata, reactive initialization,
/// and the self-typed (<typeparamref name="TTypeSource"/>) fluent builder pattern. Concrete
/// sources override <c>UpdateImpl</c> and <c>Initialize</c>.
/// </summary>
/// <typeparam name="TTypeSource">The concrete self type, enabling fluent copy-with returns.</typeparam>
public abstract record TypeSource<TTypeSource> : ITypeSource
    where TTypeSource : TypeSource<TTypeSource>
{


    private readonly ITypeRegistry typeRegistry = null!;
    /// <summary>
    /// Initializes the type source, resolving the type definition for the given CLR type from the
    /// workspace's type registry.
    /// </summary>
    /// <param name="workspace">The workspace whose type registry resolves the type definition.</param>
    /// <param name="type">The CLR entity type this source manages.</param>
    protected TypeSource(IWorkspace workspace, Type type)
    {
        typeRegistry = workspace.Hub.TypeRegistry;
        TypeDefinition = typeRegistry.GetTypeDefinition(type, typeName:type.Name)!;
    }

    /// <summary>
    /// The resolved type definition (collection name, key function, serialization metadata) for this source.
    /// </summary>
    public ITypeDefinition TypeDefinition { get; init; } = null!; 

    /// <summary>
    /// This instance typed as the concrete self type, used by fluent copy-with builders.
    /// </summary>
    protected TTypeSource This => (TTypeSource)this;

    /// <summary>
    /// Extracts this type's collection from the incoming store change and applies the source's update logic.
    /// </summary>
    /// <param name="changeItem">The store change to apply; its value must not be null.</param>
    /// <returns>The updated instance collection for this type.</returns>
    public virtual InstanceCollection Update(ChangeItem<EntityStore> changeItem)
    {
        if(changeItem.Value is null)
            throw new ArgumentNullException(nameof(changeItem), "Change item value cannot be null.");

        var myCollection = changeItem.Value.Reduce(new CollectionReference(TypeDefinition.CollectionName));

        return UpdateImpl(myCollection);
    }

    private IDisposable? workspaceSubscription;

    /// <summary>
    /// Override hook applied to this type's collection during update; the base implementation
    /// returns the collection unchanged.
    /// </summary>
    /// <param name="myCollection">The collection extracted for this type.</param>
    /// <returns>The transformed collection.</returns>
    protected virtual InstanceCollection UpdateImpl(InstanceCollection myCollection) =>
        myCollection;

    ITypeSource ITypeSource.WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            IObservable<IEnumerable<object>>
        > initialization
    ) => WithInitialData(initialization);

    /// <summary>
    /// Returns a copy of this type source whose initial data is produced by the given reactive
    /// loader, which receives the collection reference being initialized.
    /// </summary>
    /// <param name="initialization">Factory mapping the collection reference to an observable sequence of instances.</param>
    /// <returns>An updated type source.</returns>
    public TTypeSource WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            IObservable<IEnumerable<object>>
        > initialization
    ) => This with { InitializationFunction = initialization };

    /// <summary>
    /// Reactive loader producing the initial instances for this type; defaults to an empty sequence.
    /// </summary>
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

    /// <summary>
    /// Disposes the underlying workspace subscription, if any.
    /// </summary>
    public void Dispose()
    {
        workspaceSubscription?.Dispose();
    }

    /// <summary>
    /// The collection name under which instances of this type are stored.
    /// </summary>
    public string CollectionName => TypeDefinition.CollectionName;

    /// <summary>
    /// Returns a copy of this type source that uses the given key function to derive instance keys.
    /// </summary>
    /// <param name="keyFunc">The key function registered for this type's collection.</param>
    /// <returns>An updated type source.</returns>
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
