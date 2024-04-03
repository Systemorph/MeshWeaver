using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace OpenSmc.Data;

public abstract record WorkspaceReference;

public abstract record WorkspaceReference<TReference> : WorkspaceReference;

public record EntityStore(ImmutableDictionary<string, InstanceCollection> Instances)
{

    public EntityStore Merge(EntityStore s2) => new(Instances.SetItems(s2.Instances.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value.Merge(Instances.GetValueOrDefault(kvp.Key)))));
}

public record EntireWorkspace : WorkspaceReference<EntityStore>
{
    public string Path => "$";
    public override string ToString() => Path;
}

public record JsonPathReference(string Path) : WorkspaceReference<JsonNode>
{
    public override string ToString() => $"{Path}";
}

public record InstanceReference(object Id) : WorkspaceReference<object>
{
    public virtual string Path => $"$.['{Id}']";

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
    public string Path => $"$[{Collections.Select(c => $"'{c}'").Aggregate((x,y) => $"{x},{y}")}]";
    public override string ToString() => Path;

}

public record PartitionedCollectionsReference(WorkspaceReference<EntityStore> Collections, object Partition)
    : WorkspaceReference<EntityStore>
{
    public string Path => $"{Collections}@{Partition}";
    public override string ToString() => Path;
}

