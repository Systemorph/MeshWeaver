using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenSmc.Data;

public abstract record WorkspaceReference;

public abstract record WorkspaceReference<TReference> : WorkspaceReference
{
    [JsonIgnore] public IObservable<TReference> Observable { get; init; }
}

public record EntireWorkspace : WorkspaceReference<ImmutableDictionary<string, InstancesInCollection>>;
public record JsonPathReference(string Path) : WorkspaceReference<JsonNode>;
public record EntityReference(string Collection, object Id) : WorkspaceReference<object>;
public record EntityReference<T>(object Id) : WorkspaceReference<T>;

public record CollectionReference(string Collection) : WorkspaceReference<InstancesInCollection>;

public record CollectionsReference(IReadOnlyCollection<string> Collections) : WorkspaceReference<ImmutableDictionary<string, InstancesInCollection>>;
