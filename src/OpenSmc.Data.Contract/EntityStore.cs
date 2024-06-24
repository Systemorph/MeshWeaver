using System.Collections.Immutable;
using System.Security.Cryptography;

namespace OpenSmc.Data;

public record EntityStore
{
    public EntityStore() { }

    public EntityStore(IReadOnlyDictionary<string, InstanceCollection> collections) =>
        Collections = collections.ToImmutableDictionary();

    public ImmutableDictionary<string, InstanceCollection> Collections { get; init; } =
        ImmutableDictionary<string, InstanceCollection>.Empty;

    public EntityStore Merge(EntityStore updated) =>
        this with
        {
            Collections = updated
                .Collections.Concat(Collections.Where(x => !updated.Collections.ContainsKey(x.Key)))
                .ToImmutableDictionary()
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

    public EntityStore Update(WorkspaceReference reference, object value)
    {
        return reference switch
        {
            EntityReference entityReference
                => Update(entityReference.Collection, c => c.Update(entityReference.Id, value)),
            CollectionReference collectionReference
                => Update(collectionReference.Name, _ => (InstanceCollection)value),
            CollectionsReference collectionsReference
                => this with
                {
                    Collections = Collections.SetItems(((EntityStore)value).Collections)
                },
            PartitionedCollectionsReference partitionedReference
                => Update(partitionedReference.Reference, value),
            WorkspaceReference<EntityStore> collectionsReference => Merge((EntityStore)value),

            _
                => throw new NotSupportedException(
                    $"reducer type {reference.GetType().FullName} not supported"
                )
        };
    }

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

    internal InstanceCollection ReduceImpl(PartitionedCollectionsReference reference) =>
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
}
