using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Path;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace OpenSmc.Data.Persistence;

public record GetDataStateRequest : IRequest<CombinedWorkspaceState>;


public record UpdateDataStateRequest(IReadOnlyCollection<DataChangeRequest> Events);

public record DataPersistencePluginState(CombinedWorkspaceState Workspaces)
{
    /// <summary>
    /// Synchronization requests by address.
    /// </summary>
    public ImmutableDictionary<object, DataSubscription> SubscriptionsByAddress { get; init; } = ImmutableDictionary<object, DataSubscription>.Empty;

    public JsonNode SerializedWorkspace { get; init; }
}

public record DataSubscription
{
    public ImmutableDictionary<string, string> Paths { get; init; } = ImmutableDictionary<string, string>.Empty;
    public JsonNode LastSynchronized { get; init; }
}

public class DataPersistencePlugin(IMessageHub hub, DataContext context) :
    MessageHubPlugin<DataPersistencePluginState>(hub),
    IMessageHandler<GetDataStateRequest>,
    IMessageHandlerAsync<UpdateDataStateRequest>,
    IMessageHandler<StartDataSynchronizationRequest>,
    IMessageHandler<DataSynchronizationState>
{
    public DataContext Context { get; } = context;

    /// <summary>
    /// Upon start, it initializes the persisted state from the DB
    /// </summary>
    /// <returns></returns>
    ///
    private Task initializeTask;

    [Inject] private ITypeRegistry typeRegistry;
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var type in Context.DataTypes)
            typeRegistry.WithType(type);

        initializeTask = InitializeAllDataSources(cancellationToken);
        return Task.CompletedTask;
    }

    public override bool IsDeferred(IMessageDelivery delivery)
    {
        if (delivery.Message is DataSynchronizationState)
            return false;
        return base.IsDeferred(delivery);
    }

    public override Task Initialized => initializeTask;


    private async Task InitializeAllDataSources(CancellationToken cancellationToken)
    {
        var dict = await Context.DataSources
            .ToAsyncEnumerable()
                .SelectAwait(async kvp =>
                    new KeyValuePair<object, WorkspaceState>(kvp.Key, await kvp.Value.InitializeAsync(Hub, cancellationToken)))
            .ToArrayAsync(cancellationToken);
        InitializeState(new DataPersistencePluginState(new(dict.ToImmutableDictionary(), Context)));
    }

    IMessageDelivery IMessageHandler<GetDataStateRequest>.HandleMessage(IMessageDelivery<GetDataStateRequest> request)
    {
        Hub.Post(State.Workspaces, o => o.ResponseFor(request));
        return request.Processed();
    }

    public async Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery<UpdateDataStateRequest> request, CancellationToken cancellationToken)
    {
        var events = request.Message.Events;
        await UpdateState(events, cancellationToken);
        return request.Processed();

    }

    /// <summary>
    /// Here we need to group everything by data source and then by event, as the workspace might deliver
    /// the content in arbitrary order, mixing data partitions.
    /// </summary>
    /// <param name="requests">Requests to be processed</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task UpdateState(IReadOnlyCollection<DataChangeRequest> requests, CancellationToken cancellationToken)
    {
        foreach (var g in requests
                     .SelectMany(ev => ev.Elements
                         .Select(instance => new
                         {
                             Event = ev,
                             Type = instance.GetType(),
                             DataSource = Context.GetDataSourceId(instance),
                             Instance = instance
                         }))
                     .GroupBy(x => x.DataSource))
        {
            var dataSourceId = g.Key;
            if (dataSourceId == null)
                continue;
            var dataSource = Context.GetDataSource(dataSourceId);
            var workspace = State.Workspaces.GetWorkspace(dataSourceId);

            await using var transaction = await dataSource.StartTransactionAsync(cancellationToken);
            foreach (var e in g.GroupBy(x => x.Event))
            {
                var eventType = e.Key;
                foreach (var typeGroup in e.GroupBy(x => x.Type))
                    workspace = ProcessRequest(eventType, typeGroup.Key, typeGroup.Select(x => x.Instance), dataSource, workspace);
            }
            await transaction.CommitAsync(cancellationToken);
            UpdateAndSynchronize(dataSourceId, workspace);
        }
    }

    private void UpdateAndSynchronize(object dataSourceId, WorkspaceState workspace)
    {
        UpdateState(s =>
            s with
            {
                Workspaces = s.Workspaces.UpdateWorkspace(dataSourceId, workspace),
                SerializedWorkspace = null
            });

        UpdateSubscriptions();
    }

    private void UpdateSubscriptions()
    {
        foreach (var (address, subscription) in State.SubscriptionsByAddress)
        {
            var dataChanged = UpdateSubscription(address, subscription, SynchronizationMode.Delta);
            if (dataChanged != null)
                Hub.Post(dataChanged, o => o.WithTarget(address));
        }
    }

    /// <summary>
    /// This processes a single update or delete request request
    /// </summary>
    /// <param name="request">Request to be processed</param>
    /// <param name="elementType">Type of the entities</param>
    /// <param name="instances">Instances to be updated / deleted</param>
    /// <param name="dataSource">The data source to which these instances belong</param>
    /// <param name="workspace">The current state of the workspace</param>
    /// <returns></returns>
    private WorkspaceState ProcessRequest(DataChangeRequest request, Type elementType, IEnumerable<object> instances, IDataSource dataSource, WorkspaceState workspace)
    {
        if (!dataSource.GetTypeConfiguration(elementType, out var typeConfig))
            return workspace;
        var toBeUpdated = instances.ToDictionary(typeConfig.GetKey);
        var existing = workspace.Data.GetValueOrDefault(typeConfig.CollectionName) ?? ImmutableDictionary<object, object>.Empty;
        switch (request)
        {
            case UpdateDataRequest:
                workspace = Update(workspace, typeConfig, existing, toBeUpdated);
                break;
            case DeleteDataRequest:
                workspace = Delete(workspace, typeConfig, existing, toBeUpdated);
                break;
        }

        return workspace;
    }

    private WorkspaceState Update(WorkspaceState workspace, TypeSource typeConfig, ImmutableDictionary<object, object> existingInstances, IDictionary<object, object> toBeUpdatedInstances)
    {

        var grouped = toBeUpdatedInstances.GroupBy(e => existingInstances.ContainsKey(e.Key), e => e.Value).ToDictionary(x => x.Key, x => x.ToArray());

        var newInstances = grouped.GetValueOrDefault(false);
        if(newInstances?.Length > 0)
           DoAdd(typeConfig.ElementType, newInstances, typeConfig);
        var existing = grouped.GetValueOrDefault(true);
        if(existing?.Length > 0)
            DoUpdate(typeConfig.ElementType, existing, typeConfig);

        return workspace with
        {
            Data = workspace.Data.SetItem(typeConfig.CollectionName, existingInstances.SetItems(toBeUpdatedInstances)),
            Version = Hub.Version
        };

    }

    private void DoAdd(Type type, IEnumerable<object> instances, TypeSource typeConfig)
    {
        AddElementsMethod.MakeGenericMethod(type).InvokeAsAction(instances, typeConfig);
    }

    private void DoUpdate(Type type, IEnumerable<object> instances, TypeSource typeConfig)
    {
        UpdateElementsMethod.MakeGenericMethod(type).InvokeAsAction(instances, typeConfig);
    }


    private WorkspaceState Delete(WorkspaceState workspace, TypeSource typeConfig, ImmutableDictionary<object, object> existingInstances, IDictionary<object, object> toBeUpdatedInstances)
    {
        var toBeDeleted = toBeUpdatedInstances.Select(i => existingInstances.GetValueOrDefault(i.Key)).Where(x => x !=null).ToArray();
        DeleteElementsMethod.MakeGenericMethod(typeConfig.ElementType).InvokeAsAction(toBeDeleted, typeConfig);
        return workspace with
        {
            Data = workspace.Data.SetItem(typeConfig.CollectionName, existingInstances.RemoveRange(toBeUpdatedInstances.Keys)),
            Version = Hub.Version
        };
    }


    private static readonly MethodInfo AddElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => AddElements<object>(null, null));
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static void AddElements<T>(IEnumerable<object> items, TypeSource<T> config) where T : class
        => config.Add(items.Cast<T>().ToArray());


    private static readonly MethodInfo UpdateElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => UpdateElements<object>(null, null));
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static void UpdateElements<T>(IEnumerable<object> items, TypeSource<T> config) where T : class
    => config.Update(items.Cast<T>().ToArray());

    private static readonly MethodInfo DeleteElementsMethod = ReflectionHelper.GetStaticMethodGeneric(() => DeleteElements<object>(null, null));
    // ReSharper disable once UnusedMethodReturnValue.Local
    private static void DeleteElements<T>(IEnumerable<object> items, TypeSource<T> config) where T : class => config.Delete(items.Cast<T>().ToArray());

    IMessageDelivery IMessageHandler<StartDataSynchronizationRequest>.HandleMessage(IMessageDelivery<StartDataSynchronizationRequest> request)
    {
        StartSynchronization(request);

        return request.Processed();
    }

    private void StartSynchronization(IMessageDelivery<StartDataSynchronizationRequest> request)
    {
        var address = request.Sender;

        DataSubscription subscription = State.SubscriptionsByAddress.TryGetValue(address, out subscription)
            ? subscription with
            {
                // add up all paths
                Paths = subscription.Paths.SetItems(request.Message.JsonPaths)
            }
            : new DataSubscription()
            {
                Paths = request.Message.JsonPaths.ToImmutableDictionary()
            };

        UpdateState(s =>
            s with
            {
                SubscriptionsByAddress =
                s.SubscriptionsByAddress.SetItem(address,subscription)
                    
            });

        var dataChangedEvent = UpdateSubscription(address, subscription, SynchronizationMode.Full);
        if (dataChangedEvent != null)
            Hub.Post(dataChangedEvent, o => o.ResponseFor(request));

    }
    public enum SynchronizationMode{Full, Delta}
    private DataChangedEvent UpdateSubscription(object address, DataSubscription subscription, SynchronizationMode mode)
    {
        var lastSynchronized = subscription.LastSynchronized;
        var paths = subscription.Paths;
        var synchronizedWorkspace = GetSynchronizedWorkspace(paths);

        subscription = subscription with
        {
            Paths = paths,
            LastSynchronized = synchronizedWorkspace
        };


        UpdateState(s =>
            s with
            {
                SubscriptionsByAddress = s.SubscriptionsByAddress
                    .SetItem(address, subscription)
            });

        DataChangedEvent dataChanged =
            lastSynchronized == null || mode == SynchronizationMode.Full
                ? new DataSynchronizationState(Hub.Version, synchronizedWorkspace.ToString())
                : new DataSynchronizationPatch(Hub.Version, synchronizedWorkspace.CreatePatch(lastSynchronized).ToString());
        return dataChanged;
    }

    private JsonNode GetSynchronizedWorkspace(ImmutableDictionary<string, string> paths)
    {
        var serializedWorkspace = GetSerializedWorkspace();
        var ret = paths
            .Select(x =>
                new KeyValuePair<string, JsonNode>(x.Key,
                    Convert(x, serializedWorkspace)))
            .Where(x => x is { Key: not null, Value: not null })
            .ToImmutableDictionary();
        return JsonNode.Parse(JsonSerializer.Serialize(ret));
    }

    private static JsonNode Convert(KeyValuePair<string, string> x, JsonNode serializedWorkspace)
    {
        var pathApplied = JsonPath.Parse(x.Value).Evaluate(serializedWorkspace);
        if (pathApplied.Matches == null)
            return null;
        if (pathApplied.Matches.Count < 2)
            return pathApplied.Matches.FirstOrDefault()?.Value;
        // TODO V10: Here we need to make one array out of many arrays / objects (21.02.2024, Roland Bürgi)
        throw new NotSupportedException();
    }

    private JsonNode GetSerializedWorkspace()
    {
        var ret = State.SerializedWorkspace;
        if (ret == null)
        {
            ret = CreateSynchronizedWorkspace();
            UpdateState(s => s with { SerializedWorkspace = ret });
        }

        return ret;
    }
    private JsonNode CreateSynchronizedWorkspace()
    {
        var ret =
            State.Workspaces.WorkspacesByKey.Values.Aggregate
            (
                ImmutableDictionary<string, ImmutableList<ImmutableDictionary<string, object>>>.Empty,
                (dict, ws)
                    => dict.SetItems
                    (
                        ws.Data.Select
                        (
                            kvp =>
                                new KeyValuePair<string, ImmutableList<ImmutableDictionary<string, object>>>
                                (
                                    kvp.Key,
                                    SerializeEntities(kvp.Key, kvp.Value)
                                )
                        )
                    )
            );

        return JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(ret));
    }


    [Inject] private ISerializationService serializationService;

    private ImmutableList<ImmutableDictionary<string, object>> SerializeEntities(string collection, ImmutableDictionary<object, object> instancesByKey)
    {
        return instancesByKey.Select(kvp => SerializeEntity(collection, kvp.Key, kvp.Value))
            .ToImmutableList();
    }

    private ImmutableDictionary<string, object> SerializeEntity(string collection, object id, object instance)
    {
        var rawJson = serializationService.Serialize(instance);
        var dictionary = JsonConvert.DeserializeObject<ImmutableDictionary<string, object>>(rawJson.Content);
        return dictionary
            .SetItem(ReservedProperties.Id, id)
            .SetItem(ReservedProperties.Type, collection);
    }

    public IMessageDelivery HandleMessage(IMessageDelivery<DataSynchronizationState> request)
    {
        if (State == null)
            return request;
        UpdateState(s => s with
        {
            Workspaces = s.Workspaces.UpdateWorkspace(request.Sender, ConvertToWorkspace(request))
        });
        return request.Processed();
    }

    private WorkspaceState ConvertToWorkspace(IMessageDelivery<DataSynchronizationState> request)
    {
        if (!Context.DataSources.TryGetValue(request.Sender, out var dataSource))
            return null;

        var state = request.Message;
        return new(dataSource)
        {
            Version = state.Version,
            Data = Deserialize(state)
        };
    }

    private ImmutableDictionary<string, ImmutableDictionary<object, object>> Deserialize(DataSynchronizationState state)
    {
        var results = JsonSerializer.Deserialize<ImmutableDictionary<string, JsonNode>>(state.Data);
        return results
            .Select(kvp =>
                new KeyValuePair<string, ImmutableDictionary<object, object>>(kvp.Key, ParseIdAndObject(kvp.Value)))
            .ToImmutableDictionary();
    }

    private ImmutableDictionary<object, object> ParseIdAndObject(JsonNode token)
    {
        if (token is JsonArray array)
            return array.OfType<JsonObject>().Select(ParseIdAndObjectOfSingleInstance)
                .Where(x => !Equals(x, default(KeyValuePair<object,object>)))
                .ToImmutableDictionary();

        if (token is JsonObject jsonObject)
        {
            var kvp = ParseIdAndObjectOfSingleInstance(jsonObject);
            var ret = ImmutableDictionary<object, object>.Empty;
            if (kvp.Key != null)
                ret = ret.Add(kvp.Key, kvp.Value);

            return ret;
        }

        return null;
    }

    private KeyValuePair<object,object> ParseIdAndObjectOfSingleInstance(JsonObject node)
    {
        if (node.TryGetPropertyValue(ReservedProperties.Id, out var id) && id != default)
            return new(serializationService.Deserialize(id.ToString()),
                serializationService.Deserialize(node.ToString()));
        return default;
    }
}
