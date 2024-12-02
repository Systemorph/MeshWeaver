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


    public object Reduce(WorkspaceReference reference) => ReduceImpl((dynamic)reference);

    public TReference Reduce<TReference>(WorkspaceReference<TReference> reference) =>
        (TReference)ReduceImpl((dynamic)reference);

    internal object ReduceImpl(WorkspaceReference reference) =>
        throw new NotSupportedException(
            $"Reducer type {reference.GetType().FullName} not supported"
        );

    internal object ReduceImpl(EntityReference reference) =>
        GetCollection(reference.Collection)?.GetInstance(reference.Id);

    internal InstanceCollection ReduceImpl(CollectionReference reference) =>
        GetCollection(reference.Name);
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
                .Collections.Select(c => new KeyValuePair<string, InstanceCollection>(
                    c,
                    GetCollection(c)
                ))
                .Where(x => x.Value != null)
                .ToImmutableDictionary()
        };


    public InstanceCollection GetCollection(string collection) =>
        Collections.GetValueOrDefault(collection);

    public EntityStore Remove(string collection)
    {
        return this with { Collections = Collections.Remove(collection) };
    }

    public virtual bool Equals(EntityStore other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(other, this))
            return true;

        return other.Collections.Count == Collections.Count
               && other.Collections.All(x => Collections.TryGetValue(x.Key, out var value)
                                             && value.Equals(x.Value));
    }

    public override int GetHashCode()
        => Collections.Values
            .Select(x => x.GetHashCode())
            .Aggregate((x, y) => x ^ y);


    internal IEnumerable<EntityUpdate> ComputeChanges(string collection, InstanceCollection updated)
    {
        var oldValues = Collections.GetValueOrDefault(collection);
        if (oldValues == null)
            yield return new EntityUpdate(collection, null, updated);
        else
        {
            foreach (var u in updated.Instances)
            {
                var existing = oldValues.GetInstance(u.Key);
                if(u.Value is null)
                    if(existing is null)
                        continue;
                    else
                        yield return new EntityUpdate(collection, u.Key, null) { OldValue = existing };
                else if (!u.Value.Equals(existing))
                    yield return new EntityUpdate(collection, u.Key, u.Value) { OldValue = existing };
            }

            foreach (var kvp in oldValues.Instances.Where(i => !updated.Instances.ContainsKey(i.Key)))
            {
                yield return new EntityUpdate(collection, kvp.Key, null) { OldValue = kvp.Value };
            }
        }
    }

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

    public EntityStore WithCollection(string collection, InstanceCollection instances) =>
        this with
        {
            Collections = Collections.SetItem(collection, instances)
        };
}

public record EntityStoreAndUpdates(EntityStore Store, IEnumerable<EntityUpdate> Updates, string ChangedBy)
{
    public EntityStoreAndUpdates(EntityStore Store, string ChangedBy) : this(Store, [], ChangedBy)
    {
    }
}

public record EntityUpdate(string Collection, object Id, object Value)
{
    public object OldValue { get; init; }
}
