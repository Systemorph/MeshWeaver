using System.Collections.Immutable;

namespace MeshWeaver.Data;

public record EntityStore
{
    public EntityStore() { }

    public EntityStore(IReadOnlyDictionary<string, InstanceCollection> collections) =>
        Collections = collections.ToImmutableDictionary();

    public ImmutableDictionary<string, InstanceCollection> Collections { get; init; } =
        ImmutableDictionary<string, InstanceCollection>.Empty;

    public Func<Type, string> GetCollectionName { get; init; }

    public EntityStore Merge(EntityStore updated) => Merge(updated, UpdateOptions.Default);

    public EntityStore Merge(EntityStore updated, Func<UpdateOptions, UpdateOptions> options) =>
        this with
        {
            Collections = Collections.SetItems(
                options.Invoke(new()).Snapshot
                    ? updated.Collections
                    : updated.Collections.Select(c => new KeyValuePair<string, InstanceCollection>(
                        c.Key,
                        Collections.GetValueOrDefault(c.Key)?.Merge(c.Value) ?? c.Value
                    ))
            )
        };

    public EntityStore Merge(EntityStore updated, UpdateOptions options) =>
        this with
        {
            Collections = Collections.SetItems(
                options.Snapshot
                    ? updated.Collections
                    : updated.Collections.Select(c => new KeyValuePair<string, InstanceCollection>(
                        c.Key,
                        Collections.GetValueOrDefault(c.Key)?.Merge(c.Value) ?? c.Value
                    ))
            )
        };

    public EntityStore Update(
        string collection,
        Func<InstanceCollection, InstanceCollection> update
    ) =>
        this with
        {
            Collections = Collections.SetItem(
                collection,
                update.Invoke(Collections.GetValueOrDefault(collection) ?? new InstanceCollection())
            )
        };

    public EntityStore Update(WorkspaceReference reference, object value) =>
        Update(reference, value, x => x);

    public EntityStore Update(
        WorkspaceReference reference,
        object value,
        Func<UpdateOptions, UpdateOptions> options
    )
    {
        return reference switch
        {
            EntityReference entityReference
                => Update(entityReference.Collection, c => c.Update(entityReference.Id, value)),
            CollectionReference collectionReference
                => Update(collectionReference.Name, _ => (InstanceCollection)value),
            CollectionsReference 
                => this with
                {
                    Collections = Collections.SetItems(((EntityStore)value).Collections)
                },
            PartitionedCollectionsReference partitioned
                => Update(partitioned.Reference, value, options),
            WorkspaceReference<EntityStore> 
                => Merge((EntityStore)value, options),

            _
                => throw new NotSupportedException(
                    $"reducer type {reference.GetType().FullName} not supported"
                )
        };
    }

    public IEnumerable<T> GetData<T>()
        => GetCollection(GetCollectionName?.Invoke(typeof(T))?? typeof(T).FullName).Get<T>();
    public object Reduce(WorkspaceReference reference) => ReduceImpl((dynamic)reference);

    public TReference Reduce<TReference>(WorkspaceReference<TReference> reference) =>
        (TReference)ReduceImpl((dynamic)reference);

    internal object ReduceImpl(WorkspaceReference reference) =>
        throw new NotSupportedException(
            $"Reducer type {reference.GetType().FullName} not supported"
        );

    internal object ReduceImpl(EntityReference reference) =>
        GetCollection(reference.Collection)?.GetData(reference.Id);

    internal InstanceCollection ReduceImpl(CollectionReference reference) =>
        GetCollection(reference.Name);


    internal EntityStore ReduceImpl(CollectionsReference reference) =>
        this with
        {
            Collections = reference
                .Collections.Select(c => new KeyValuePair<string, InstanceCollection>(
                    c,
                    GetCollection(c)
                ))
                .Where(x => x.Value != null)
                .ToImmutableDictionary()
        };

    internal EntityStore ReduceImpl(PartitionedCollectionsReference reference) =>
        ReduceImpl(reference.Reference);
    internal EntityStore ReduceImpl(StreamReference reference) =>
        ReduceImpl((dynamic)reference.Reference);

    public InstanceCollection GetCollection(string collection) =>
        Collections.GetValueOrDefault(collection);

    public EntityStore Remove(string collection)
    {
        return this with { Collections = Collections.Remove(collection) };
    }

    public virtual bool Equals(EntityStore other)
    {
        if(other is null)
            return false;

        if (ReferenceEquals(other,this))
            return true;

        return other.Collections.Count == Collections.Count
            && other.Collections.All(x => Collections.TryGetValue(x.Key, out var value) 
                                          && value.Equals(x.Value));
    }

    public override int GetHashCode()
    => Collections.Values
        .Select(x => x.GetHashCode())
        .Aggregate((x, y) => x ^ y);
}
public record EntityStoreAndUpdates(IEnumerable<EntityStoreUpdate> Changes, EntityStore Store);

public record EntityStoreUpdate(string Collection, object Id, object Value)
{
    public object OldValue { get; init; }
}
