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
    public EntityStore(IEnumerable<KeyValuePair<string, InstancesInCollection>> instances)
    {
        Instances = instances.Where(x => x.Value != null && x.Value.Instances.Count > 0).ToImmutableDictionary();
    }

    public ImmutableDictionary<string, InstancesInCollection> Instances { get; init; }

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

public record EntityReference(string Collection, object Id) : WorkspaceReference<object>
{
    public string Path => $"$['{Collection}']['{Id}']";
    public override string ToString() => Path;

}


public record CollectionReference(string Collection) : WorkspaceReference<InstancesInCollection>
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
