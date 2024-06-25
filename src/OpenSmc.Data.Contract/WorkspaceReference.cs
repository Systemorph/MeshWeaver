using System.Text.Json;

namespace OpenSmc.Data;

public abstract record WorkspaceReference;

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

public record CollectionsReference(IReadOnlyCollection<string> Collections)
    : WorkspaceReference<EntityStore>
{

    public override string ToString() => string.Join(',',Collections);

    public virtual bool Equals(CollectionsReference other) =>
        other != null && Collections.SequenceEqual(other.Collections);

    public override int GetHashCode() => Collections.Aggregate(17, (a, b) => a ^ b.GetHashCode());
}

public record JsonElementReference : WorkspaceReference<JsonElement>;

public record StreamReference(object Partition, object Reference) : WorkspaceReference<EntityStore>;
