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
public record EntityStore(ImmutableDictionary<string, InstancesInCollection> Instances);

public record EntireWorkspace : WorkspaceReference<EntityStore>
{
    public override string ToString() => nameof(EntireWorkspace);
}

public record JsonPathReference(string Path) : WorkspaceReference<JsonNode>
{
    public override string ToString() => $"Path:{Path}";

}

public record EntityReference(string Collection, object Id) : WorkspaceReference<object>
{
    public override string ToString() => $"{Collection} : {Id}";

}

public record EntityReference<T>(object Id) : WorkspaceReference<T>
{
    public override string ToString() => $"{typeof(T).FullName} : {Id}";

}

public record CollectionReference(string Collection) : WorkspaceReference<InstancesInCollection>
{
    public override string ToString() => $"Collection : {Collection}";
    public Func<InstancesInCollection, InstancesInCollection> Transformation { get; init; } = x => x;

}

public record CollectionsReference(IReadOnlyCollection<string> Collections)
    : WorkspaceReference<EntityStore>
{
    public override string ToString() => $"Collections : {Collections.Aggregate(string.Empty, (x,y) => $"{x},{y}")}";

}
