using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenSmc.Data;

public abstract record WorkspaceReference : IObservable<object>
{
    public abstract IDisposable Subscribe(IObserver<object> observer);
}

public abstract record WorkspaceReference<TReference> : WorkspaceReference
{
    [JsonIgnore] public IObservable<TReference> Stream { get; init; }

    public override IDisposable Subscribe(IObserver<object> observer)
        => Stream.Subscribe((IObserver<TReference>)observer);

}
public record EntityStore
{
    public EntityStore(IEnumerable<KeyValuePair<string, InstanceCollection>> instances)
    {
        Instances = instances
            //.Where(x => x.Value != null && x.Value.Instances.Count > 0)
            .ToImmutableDictionary();
    }

    public ImmutableDictionary<string, InstanceCollection> Instances { get; init; }

    public EntityStore Merge(EntityStore s2) => new(Instances.SetItems(s2.Instances.Select(kvp => new KeyValuePair<string, InstanceCollection>(kvp.Key, kvp.Value.Merge(Instances.GetValueOrDefault(kvp.Key))))));
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

public record PartitionedCollectionsReference(IReadOnlyCollection<string> Collections, object Partition)
    : CollectionsReference(Collections);

