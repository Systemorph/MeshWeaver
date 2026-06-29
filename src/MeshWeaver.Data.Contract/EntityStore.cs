using System.Collections.Immutable;

namespace MeshWeaver.Data;

/// <summary>
/// Immutable in-memory store of workspace data, organised as named
/// <see cref="InstanceCollection"/>s keyed by collection name. Supports reducing the store
/// to the subset described by a <see cref="WorkspaceReference"/>.
/// </summary>
public record EntityStore
{
    /// <summary>Creates an empty entity store.</summary>
    public EntityStore() { }

    /// <summary>Creates an entity store populated from the given collections.</summary>
    /// <param name="collections">The collections to seed the store with, keyed by collection name.</param>
    public EntityStore(IReadOnlyDictionary<string, InstanceCollection> collections) =>
        Collections = collections.ToImmutableDictionary();

    /// <summary>The collections held by this store, keyed by collection name.</summary>
    public ImmutableDictionary<string, InstanceCollection> Collections { get; init; } =
        ImmutableDictionary<string, InstanceCollection>.Empty;

    /// <summary>Optional function mapping a CLR type to its collection name.</summary>
    public Func<Type, string>? GetCollectionName { get; init; } = null!;


    /// <summary>
    /// Reduces this store to the data described by the given reference.
    /// </summary>
    /// <param name="reference">The workspace reference describing the subset to extract.</param>
    /// <returns>The reduced result (collection, instance, sub-store, …) for the reference.</returns>
    public object Reduce(WorkspaceReference reference) => ReduceImpl((dynamic)reference);

    /// <summary>
    /// Reduces this store to the strongly-typed result described by the given reference.
    /// </summary>
    /// <typeparam name="TReference">The result type the reference reduces to.</typeparam>
    /// <param name="reference">The typed workspace reference describing the subset to extract.</param>
    /// <returns>The reduced result of type <typeparamref name="TReference"/>.</returns>
    public TReference Reduce<TReference>(WorkspaceReference<TReference> reference) =>
        (TReference)ReduceImpl((dynamic)reference);

    internal object ReduceImpl(WorkspaceReference reference) =>
        throw new NotSupportedException(
            $"Reducer type {reference.GetType().FullName} not supported"
        );

    internal object? ReduceImpl(EntityReference reference) =>
        GetCollection(reference.Collection)?.GetInstance(reference.Id);

    internal InstanceCollection? ReduceImpl(CollectionReference reference)
    {
        var collection = GetCollection(reference.Name);
        return collection == null ? null : collection with { CollectionName = reference.Name };
    }
    internal EntityStore ReduceImpl(PartitionedWorkspaceReference<EntityStore> reference) =>
        ReduceImpl((dynamic)reference.Reference);
    internal InstanceCollection ReduceImpl(PartitionedWorkspaceReference<InstanceCollection> reference) =>
        ReduceImpl((dynamic)reference.Reference);
    internal object ReduceImpl(PartitionedWorkspaceReference<object> reference) =>
        ReduceImpl((dynamic)reference.Reference);


    internal EntityStore ReduceImpl(CollectionsReference reference) =>
        this with
        {
            Collections = reference
                .Collections.Select(c => new KeyValuePair<string, InstanceCollection?>(
                    c,
                    GetCollection(c)
                ))
                .Where(x => x.Value != null)!
                .ToImmutableDictionary<string,InstanceCollection>()
        };


    /// <summary>
    /// Returns the collection with the given name, or null if not present.
    /// </summary>
    /// <param name="collection">The collection name.</param>
    /// <returns>The matching <see cref="InstanceCollection"/>, or null.</returns>
    public InstanceCollection? GetCollection(string collection) =>
        Collections.GetValueOrDefault(collection);

    /// <summary>
    /// Returns a copy of this store with the named collection removed.
    /// </summary>
    /// <param name="collection">The collection name to remove.</param>
    /// <returns>The updated store.</returns>
    public EntityStore Remove(string collection)
    {
        return this with { Collections = Collections.Remove(collection) };
    }

    /// <summary>
    /// Determines whether this store holds the same collections (by key and value) as another.
    /// </summary>
    /// <param name="other">The store to compare against.</param>
    /// <returns>True if the stores are equal; otherwise false.</returns>
    public virtual bool Equals(EntityStore? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(other, this))
            return true;

        return other.Collections.Count == Collections.Count
               && other.Collections.All(x => Collections.TryGetValue(x.Key, out var value)
                                             && value.Equals(x.Value));
    }

    /// <summary>Returns a hash code derived from the contained collections.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
        => Collections.Values
            .Select(x => x.GetHashCode())
            .Aggregate((x, y) => x ^ y);


    internal IEnumerable<EntityUpdate> ComputeChanges(string collection, InstanceCollection updated)
    {
        var oldValues = Collections.GetValueOrDefault(collection);
        if (oldValues == null)
            yield return new EntityUpdate(collection, null!, updated);
        else
        {
            foreach (var u in updated.Instances)
            {
                var existing = oldValues.GetInstance(u.Key);
                if (!u.Value.Equals(existing))
                    yield return new EntityUpdate(collection, u.Key, u.Value) { OldValue = existing };
            }

            foreach (var kvp in oldValues.Instances.Where(i => !updated.Instances.ContainsKey(i.Key)))
            {
                yield return new EntityUpdate(collection, kvp.Key, null!) { OldValue = kvp.Value };
            }
        }
    }

    /// <summary>
    /// Removes the instances present in <paramref name="entityStore"/> from this store and returns
    /// the resulting store together with the computed per-entity updates.
    /// </summary>
    /// <param name="entityStore">The store describing the instances to delete.</param>
    /// <param name="changedBy">Identifier of the actor performing the change.</param>
    /// <returns>The updated store paired with the change set.</returns>
    public EntityStoreAndUpdates DeleteWithUpdates(EntityStore entityStore, string changedBy)
    {
        var store = Remove(entityStore);
        return new EntityStoreAndUpdates(store, store.Collections.SelectMany(u => ComputeChanges(u.Key, u.Value)), changedBy);
    }

    private EntityStore Remove(EntityStore toBeRemoved) =>
        this with
        {
            Collections = Collections
                .Select(x => toBeRemoved.Collections.TryGetValue(x.Key, out var tbr)
                    ? new KeyValuePair<string, InstanceCollection>(x.Key, x.Value.Remove(tbr.Instances.Keys))
                    : x).ToImmutableDictionary()
        };

    /// <summary>
    /// Returns a copy of this store with the named collection set (added or replaced).
    /// </summary>
    /// <param name="collection">The collection name.</param>
    /// <param name="instances">The collection contents to set.</param>
    /// <returns>The updated store.</returns>
    public EntityStore WithCollection(string collection, InstanceCollection instances) =>
        this with
        {
            Collections = Collections.SetItem(collection, instances)
        };
}

/// <summary>
/// Pairs an updated <see cref="EntityStore"/> with the set of entity updates that produced it.
/// </summary>
/// <param name="Store">The resulting store after the change.</param>
/// <param name="Updates">The per-entity updates that were applied.</param>
/// <param name="ChangedBy">Identifier of the actor that made the change, if known.</param>
public record EntityStoreAndUpdates(EntityStore Store, IEnumerable<EntityUpdate> Updates, string? ChangedBy)
{
    /// <summary>
    /// Creates an instance with an empty update set.
    /// </summary>
    /// <param name="Store">The resulting store after the change.</param>
    /// <param name="ChangedBy">Identifier of the actor that made the change.</param>
    public EntityStoreAndUpdates(EntityStore Store, string ChangedBy) : this(Store, [], ChangedBy)
    {
    }
}

/// <summary>
/// Describes a single change to an entity within a collection.
/// </summary>
/// <param name="Collection">The name of the collection the entity belongs to.</param>
/// <param name="Id">The identifier of the entity, or null when the whole collection is added.</param>
/// <param name="Value">The new value of the entity, or null when the entity is deleted.</param>
public record EntityUpdate(string Collection, object? Id, object? Value)
{
    /// <summary>The previous value of the entity before the change, if any.</summary>
    public object? OldValue { get; init; }
}
