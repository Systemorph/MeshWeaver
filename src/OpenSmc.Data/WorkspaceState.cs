using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Path;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data.Persistence;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public record WorkspaceState
{
    private readonly ISerializationService serializationService;
    private IMessageHub Hub { get; }
    public ImmutableDictionary<string, InstancesInCollection> Instances { get; init; }
    private ImmutableDictionary<Type, string> CollectionsByType { get; init; }
    private ImmutableDictionary<Type, ITypeSource> TypeSourcesByType { get; init; }
    private ImmutableDictionary<string, Type> TypeSourcesByCollection { get; init; }
    public WorkspaceState(IMessageHub hub, IReadOnlyDictionary<string,InstancesInCollection> Instances, IReadOnlyDictionary<Type, ITypeSource> typeSources)
        :this(hub, typeSources)
    {
        this.Instances = Instances.ToImmutableDictionary();
        TypeSourcesByType = typeSources.ToImmutableDictionary();
        CollectionsByType = Instances.Where(x => x.Value.ElementType != null).ToImmutableDictionary(x => x.Value.ElementType, x => x.Key);
        TypeSourcesByCollection = CollectionsByType.ToImmutableDictionary(x => x.Value, x => x.Key);
    }

    private WorkspaceState(IMessageHub hub, IReadOnlyDictionary<Type, ITypeSource> typeSources)
    {
        Hub = hub;
        Version = hub.Version;
        serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
        CollectionsByType = typeSources.ToImmutableDictionary(x => x.Key, x => x.Value.CollectionName);
        TypeSourcesByCollection = typeSources.Values.ToImmutableDictionary(x => x.CollectionName, x => x.ElementType);
        TypeSourcesByType = typeSources.ToImmutableDictionary();
    }


    public WorkspaceState(IMessageHub hub, DataChangedEvent dataChanged,
        IReadOnlyDictionary<Type, ITypeSource> typeSources)
        : this(hub, typeSources)
    {
        if (dataChanged.Type != ChangeType.Full || string.IsNullOrWhiteSpace(dataChanged.Change?.Content))
            throw new ArgumentException("Cannot construct Worspace based on delta state.");
        Current = JsonNode.Parse(dataChanged.Change.Content);
        if (Current is JsonObject obj)
            Instances = obj.ToImmutableDictionary(x => x.Key,
                x => 
                    new InstancesInCollection(
                    serializationService.DeserializeToEntities(x.Key, x.Value as JsonArray)
                    .ToImmutableDictionary
                    (
                        y => y.Id,
                        y => y.Entity
                    ))
                    {
                        GetKey = TypeSourcesByCollection.TryGetValue(x.Key, out var type) &&  typeSources.TryGetValue(type, out var ts) ? ts.GetKey : null,
                        ElementType = TypeSourcesByCollection.GetValueOrDefault(x.Key)
                    })
;
    }

    public long Version { get; init; }
    private object Current { get; init; }
    public object Reduce(WorkspaceReference reference)
        => ReduceImpl((dynamic)reference);
    public TReference Reduce<TReference>(WorkspaceReference<TReference> reference)
        => (TReference)ReduceImpl((dynamic)reference);


    private object ReduceImpl(WorkspaceReference reference)
    {
        throw new NotSupportedException($"Reducing with type {reference.GetType().FullName} is not supported.");
    }

    private JsonNode ReduceImpl(JsonPathReference reducer)
    {
        var node = GetCurrentJsonNode();

        var jsonPath = JsonPath.Parse(reducer.Path);
        var evaluated = jsonPath.Evaluate(node);
        var match = evaluated.Matches switch
        {
            { Count: 1 } => evaluated.Matches[0].Value,
            { Count: > 1 } => new JsonArray(evaluated.Matches.Select(x => x.Value).ToArray()),
            _ => null
        };
        return match;
    }

    private JsonNode GetCurrentJsonNode()
    {
        if (Current is JsonNode node)
            return node;
        if (Current != null)
            return JsonNode.Parse(serializationService.SerializeToString(Current));

        return new JsonObject(Instances.Select(x =>
            new KeyValuePair<string, JsonNode>(x.Key, serializationService.SerializeState(Instances))));
    }

    private object ReduceImpl(EntityReference reference)
    {
        return Instances.GetValueOrDefault(reference.Collection)?.GetData(reference.Id);
    }
    private object ReduceImpl<T>(EntityReference<T> reference)
    {
        if (!CollectionsByType.TryGetValue(typeof(T), out var collection))
            return null;
        return Instances.GetValueOrDefault(collection)?.GetData(reference.Id);
    }
    private ImmutableDictionary<string, InstancesInCollection> ReduceImpl(EntireWorkspace _) => Instances;
    private InstancesInCollection ReduceImpl(CollectionReference reference) => Instances.GetValueOrDefault(reference.Collection);

    private ImmutableDictionary<string,InstancesInCollection> ReduceImpl(CollectionsReference reference) =>
        reference
            .Collections
            .Select(c => new KeyValuePair<string,InstancesInCollection>(c, Instances.GetValueOrDefault(c)))
            .Where(x => x.Value != null)
            .ToImmutableDictionary();




    public WorkspaceState Change(DataChangeRequest request)
        => request switch
        {
            DataChangeRequestWithElements requestWithElements => Change(requestWithElements),
            PatchChangeRequest patch => Change(patch),
            _ => throw new ArgumentOutOfRangeException($"No implementation found for {request.GetType().FullName}")

        };



    public WorkspaceState Synchronize(DataChangedEvent @event)
    {
        var change = @event.Change?.Content;
        var type = @event.Type;
        if (string.IsNullOrEmpty(change))
            return this;

        var newWorkspace = ParseWorkspace(type, change);
        if (newWorkspace is not JsonObject obj)
            throw new NotSupportedException();

        var newInstances =
            obj.ToImmutableDictionary(
                x => x.Key,
                x => new InstancesInCollection(
                    serializationService
                    .ConvertToData((JsonArray)x.Value)
                    .ToImmutableDictionary(y => y.Id, y => y.Entity)
            ));

        return this with
        {
            Version = Hub.Version,
            Current = newWorkspace,
            Instances = newInstances
        };
    }

    private JsonNode ParseWorkspace(ChangeType type, string change)
    {
        var current = GetJsonNode(Current);
        var currentWorkspace = type switch
        {
            ChangeType.Full =>
                JsonNode.Parse(change),
            ChangeType.Patch =>
                JsonSerializer.Deserialize<JsonPatch>(change)
                    .Apply(current.DeepClone())
                    .Result,
            _ => throw new ArgumentOutOfRangeException()
        };

        if (currentWorkspace == null)
            throw new ArgumentException("Cannot deserialize workspace");
        return currentWorkspace;
    }

    private JsonNode GetJsonNode(object current)
    {
        return current == null
            ? null
            : current as JsonNode 
              ?? JsonNode.Parse(serializationService.SerializeToString(current));
    }


    protected virtual WorkspaceState Change(DataChangeRequestWithElements request)
        =>
            this with
            {
                Instances = Instances.SetItems(GetChanges(request.Elements)),
                Current = null,
                Version = Hub.Version
            };

    private IEnumerable<KeyValuePair<string, InstancesInCollection>> GetChanges(IReadOnlyCollection<object> instances)
    {
        foreach (var g in instances.GroupBy(x => x.GetType()))
        {
            var typeProvider = TypeSourcesByType.GetValueOrDefault(g.Key);
            if (typeProvider != null)
                yield return new KeyValuePair<string, InstancesInCollection>
                (
                    typeProvider.CollectionName,
                    new(g.ToImmutableDictionary(typeProvider.GetKey, x => x))
                );
        }
    }

    public IEnumerable<Type> MappedTypes => CollectionsByType.Keys;

    private WorkspaceState Change(PatchChangeRequest patch)
    {
        var change = patch.Change;
        var newWorkspace = ParseWorkspace(ChangeType.Patch, change.ToString());

        return this with
        {
            Current = newWorkspace,
        };
    }

    public void Rollback()
    {
        throw new NotImplementedException();
    }


    public WorkspaceState Merge(WorkspaceState other)
        => this with
        {
            Current = null,
            Instances = Instances.SetItems(other.Instances),
            Version = Math.Max(Version, other.Version),
            TypeSourcesByType = TypeSourcesByType.SetItems(other.TypeSourcesByType),
            TypeSourcesByCollection = TypeSourcesByCollection.SetItems(other.TypeSourcesByCollection),
            CollectionsByType = CollectionsByType.SetItems(other.CollectionsByType),
        };
}