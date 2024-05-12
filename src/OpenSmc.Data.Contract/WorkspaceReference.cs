using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace OpenSmc.Data;

public abstract record WorkspaceReference;

public abstract record WorkspaceReference<TReference> : WorkspaceReference;

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
            Collections = Collections.SetItems(
                Collections
                    .Select(kvp => new KeyValuePair<string, InstanceCollection>(
                        kvp.Key,
                        kvp.Value.Merge(updated.Collections.GetValueOrDefault(kvp.Key))
                    ))
                    .Concat(updated.Collections.Where(kvp => !Collections.ContainsKey(kvp.Key)))
                    .Where(x => x.Value.Instances.Count > 0)
                    .ToImmutableDictionary()
            )
        };

    public EntityStore Merge(WorkspaceReference reference, InstanceCollection updated)
    {
        //TODO Roland Bürgi 2024-05-12: Generalize this so that it could be used for any reference
        var reduced = (InstanceCollection)Reduce(reference);
        var collectionName = reference switch
        {
            CollectionReference collectionReference => collectionReference.Collection,
            _
                => throw new NotSupportedException(
                    $"reducer type {reference.GetType().FullName} not supported"
                )
        };
        return this with
        {
            Collections = Collections.SetItem(
                ((CollectionReference)reference).Collection,
                reduced == null ? updated : reduced.Merge(updated)
            )
        };
    }

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
                => Update(collectionReference.Collection, c => c.Merge((InstanceCollection)value)),
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
        GetCollection(reference.Collection);

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
}

public record JsonPathReference(string Path) : WorkspaceReference<JsonNode>
{
    public override string ToString() => $"{Path}";
}

public record InstanceReference(object Id) : WorkspaceReference<object>
{
    public virtual string Path => $"$.['{Id}']";

    public override string ToString() => Path;
}

public record EntityReference(string Collection, object Id) : InstanceReference(Id)
{
    public override string Path => $"$.['{Collection}']['{Id}']";

    public override string ToString() => Path;
}

public record CollectionReference(string Collection) : WorkspaceReference<InstanceCollection>
{
    public string Path => $"$['{Collection}']";

    public override string ToString() => Path;
}

public record CollectionsReference(IReadOnlyCollection<string> Collections)
    : WorkspaceReference<EntityStore>
{
    public string Path => $"$[{Collections.Select(c => $"'{c}'").Aggregate((x, y) => $"{x},{y}")}]";

    public override string ToString() => Path;

    public virtual bool Equals(CollectionsReference other) =>
        other != null && Collections.SequenceEqual(other.Collections);

    public override int GetHashCode() => Collections.Aggregate(17, (a, b) => a ^ b.GetHashCode());
}

public record PartitionedCollectionsReference(
    WorkspaceReference<EntityStore> Collections,
    object Partition
) : WorkspaceReference<EntityStore>
{
    public string Path => $"{Collections}@{Partition}";

    public override string ToString() => Path;
}
