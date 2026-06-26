using System.Reactive.Linq;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;

namespace MeshWeaver.Data;

/// <summary>
/// A type source for entities partitioned by a key derived from each instance, assigning every
/// instance to a partition via the supplied partition function.
/// </summary>
/// <typeparam name="T">The entity type managed by this source.</typeparam>
/// <typeparam name="TPartition">The partition key type.</typeparam>
/// <param name="Workspace">The workspace this type source belongs to.</param>
/// <param name="PartitionFunction">Maps an instance of <typeparamref name="T"/> to its partition key.</param>
/// <param name="DataSource">Identifier of the owning data source.</param>
public record PartitionedTypeSourceWithType<T, TPartition>(
    IWorkspace Workspace,
    Func<T, TPartition> PartitionFunction,
    object DataSource
) : TypeSourceWithType<T>(Workspace, DataSource), IPartitionedTypeSource
{
    /// <summary>
    /// Returns the partition key for the given instance by invoking the partition function.
    /// </summary>
    /// <param name="instance">The entity instance to partition.</param>
    /// <returns>The partition key, or a new object when the function returns null.</returns>
    public object GetPartition(object instance) => PartitionFunction.Invoke((T)instance) ?? new object();
}

/// <summary>
/// Concrete in-memory type source for entity type <typeparamref name="T"/>, keyed by the type's
/// registered key function. Supports configurable update and strongly-typed initial-data delegates.
/// </summary>
/// <typeparam name="T">The entity type managed by this source.</typeparam>
/// <param name="Workspace">The workspace this type source belongs to.</param>
/// <param name="DataSource">Identifier of the owning data source.</param>
public record TypeSourceWithType<T>(IWorkspace Workspace, object DataSource)
    : TypeSourceWithType<T, TypeSourceWithType<T>>(Workspace, DataSource)
{
    /// <summary>
    /// Applies the configured update action to the incoming collection before it is stored.
    /// </summary>
    /// <param name="instances">The collection to transform.</param>
    /// <returns>The transformed collection.</returns>
    protected override InstanceCollection UpdateImpl(InstanceCollection instances) =>
        UpdateAction.Invoke(instances);

    /// <summary>
    /// Transformation applied to each incoming collection during update; defaults to the identity function.
    /// </summary>
    protected Func<InstanceCollection, InstanceCollection> UpdateAction { get; init; } = i => i;

    /// <summary>
    /// Returns a copy of this type source with the given update transformation applied on each update.
    /// </summary>
    /// <param name="update">Function that transforms the instance collection.</param>
    /// <returns>An updated type source.</returns>
    public TypeSourceWithType<T> WithUpdate(Func<InstanceCollection, InstanceCollection> update) =>
        This with
        {
            UpdateAction = update
        };

    /// <summary>
    /// Configures the initial data from a reactive, strongly-typed source.
    /// </summary>
    /// <param name="initialData">Factory returning an observable sequence of instances.</param>
    /// <returns>An updated type source.</returns>
    public TypeSourceWithType<T> WithInitialData(
        Func<IObservable<IEnumerable<T>>> initialData
    ) => WithInitialData(_ => initialData().Select(x => x.Cast<object>()));

    /// <summary>
    /// Configures the initial data from a reactive, strongly-typed source that receives the
    /// collection reference being initialized.
    /// </summary>
    /// <param name="initialData">Factory mapping the collection reference to an observable sequence of instances.</param>
    /// <returns>An updated type source.</returns>
    public TypeSourceWithType<T> WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            IObservable<IEnumerable<T>>
        > initialData
    ) => WithInitialData(r => initialData(r).Select(x => x.Cast<object>()));

    /// <summary>
    /// Configures the initial data from a fixed, strongly-typed collection.
    /// </summary>
    /// <param name="initialData">The instances to seed the type source with.</param>
    /// <returns>An updated type source.</returns>
    public TypeSourceWithType<T> WithInitialData(IEnumerable<T> initialData) =>
        WithInitialData(_ => Observable.Return(initialData.Cast<object>()));

}

/// <summary>
/// Base record for type sources bound to a CLR entity type <typeparamref name="T"/>, providing the
/// fluent builder surface (query, key, access restrictions) returned as the concrete self type
/// <typeparamref name="TTypeSource"/> for chaining.
/// </summary>
/// <typeparam name="T">The entity type managed by this source.</typeparam>
/// <typeparam name="TTypeSource">The concrete self type, enabling fluent copy-with returns.</typeparam>
/// <param name="Workspace">The workspace this type source belongs to.</param>
/// <param name="DataSource">Identifier of the owning data source.</param>
public abstract record TypeSourceWithType<T, TTypeSource>(IWorkspace Workspace, object DataSource)
    : TypeSource<TTypeSource>(Workspace, typeof(T))
    where TTypeSource : TypeSourceWithType<T, TTypeSource>
{
    /// <summary>
    /// Returns a copy of this type source configured to resolve a single instance by key through
    /// the given query function.
    /// </summary>
    /// <param name="query">Function mapping a key string to an instance, or null if not found.</param>
    /// <returns>An updated type source.</returns>
    public TTypeSource WithQuery(Func<string, T?> query) => This with { QueryFunction = query };

    /// <summary>
    /// Optional function that resolves a single instance by its key string; null when unset.
    /// </summary>
    protected Func<string, T?>? QueryFunction { get; init; }

    /// <summary>
    /// Returns a copy of this type source that derives each instance's key from the given selector.
    /// </summary>
    /// <typeparam name="TProp">The key property type.</typeparam>
    /// <param name="keyFunc">Selector extracting the key value from an instance.</param>
    /// <returns>An updated type source.</returns>
    public TTypeSource WithKey<TProp>(Func<T, TProp> keyFunc)
        => WithKey(new KeyFunction(o => keyFunc.Invoke((T)o)!, typeof(TProp)));

    /// <summary>
    /// Adds an access restriction for this type using async evaluation.
    /// </summary>
    /// <param name="restriction">Async restriction delegate to evaluate</param>
    /// <param name="name">Optional name for logging/debugging</param>
    /// <returns>Updated type source with the restriction added</returns>
    public TTypeSource WithAccessRestriction(
        AccessRestrictionDelegate restriction,
        string? name = null)
    {
        return This with
        {
            AccessRestrictions = AccessRestrictions.Add(new AccessRestrictionEntry(restriction, name))
        };
    }

    /// <summary>
    /// Adds a strongly-typed access restriction for row-level operations.
    /// The entity is cast to T before being passed to the restriction.
    /// </summary>
    /// <param name="restriction">Strongly-typed restriction function</param>
    /// <param name="name">Optional name for logging/debugging</param>
    /// <returns>Updated type source with the restriction added</returns>
    /// <example>
    /// <code>
    /// .WithType&lt;OwnedEntity&gt;(type => type
    ///     .WithTypedAccessRestriction((action, entity, ctx) =>
    ///         action == "Read" || entity.OwnerId == ctx.UserContext?.ObjectId,
    ///         "OwnerOnly"))
    /// </code>
    /// </example>
    public TTypeSource WithTypedAccessRestriction(
        Func<string, T, AccessRestrictionContext, bool> restriction,
        string? name = null)
    {
        return WithAccessRestriction(
            (action, ctx, accessCtx) =>
                Observable.Return(ctx is T instance
                    ? restriction(action, instance, accessCtx)
                    : true), // Allow if not the right type (shouldn't happen for instance-level checks)
            name);
    }
}
