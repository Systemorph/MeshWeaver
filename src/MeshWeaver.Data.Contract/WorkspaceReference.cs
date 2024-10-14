using System.Text.Json;

namespace MeshWeaver.Data;

public abstract record WorkspaceReference;


// ReSharper disable once UnusedTypeParameter
public abstract record WorkspaceReference<TReference> : WorkspaceReference;

public record JsonPointerReference(string Pointer) : WorkspaceReference<JsonElement?>
{
    public override string ToString() => Pointer;
}

public record InstanceReference(object Id) : WorkspaceReference<object>
{
    public virtual string Pointer => $"$.['{Id}']";

    public override string ToString() => Pointer;
}

public record EntityReference(string Collection, object Id) : InstanceReference(Id)
{
    public override string Pointer => $"/{Collection}/'{Id}'";

    public override string ToString() => Pointer;
}

public record CollectionReference(string Name) : WorkspaceReference<InstanceCollection>
{
    public string Pointer => $"/{Name}";

    public override string ToString() => Pointer;
}

public record AggregateWorkspaceReference(params WorkspaceReference<EntityStore>[] References)
    : WorkspaceReference<EntityStore>;

public record CollectionsReference(IReadOnlyCollection<string> Collections)
    : WorkspaceReference<EntityStore>
{
    public CollectionsReference(params string[] Collections) : this((IReadOnlyCollection<string>)Collections)
    { }

    public override string ToString() => string.Join(',',Collections);

    public virtual bool Equals(CollectionsReference other) =>
        other != null && Collections.SequenceEqual(other.Collections);

    public override int GetHashCode() => Collections.Aggregate(17, (a, b) => a ^ b.GetHashCode());
}

public record JsonElementReference : WorkspaceReference<JsonElement>;

public record CombinedStreamReference(params StreamIdentity[] References) : WorkspaceReference<EntityStore>;
public record StreamIdentity(object Owner, object Partition) : WorkspaceReference<EntityStore>;

public record PartitionedCollectionsReference(object Partition, CollectionsReference Reference) : WorkspaceReference<EntityStore>;
public record PartitionedCollectionReference(object Partition, CollectionReference Reference) : WorkspaceReference<InstanceCollection>;
public record PartitionedEntityReference(object Partition, EntityReference Entity) : WorkspaceReference<object>;
