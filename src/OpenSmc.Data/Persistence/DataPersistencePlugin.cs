using System.Collections.Immutable;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Data.Persistence;

public record GetDataStateRequest : IRequest<CombinedWorkspaceState>;


public record UpdateDataStateRequest(IReadOnlyCollection<DataChangeRequest> Events);

public record DataPersistencePluginState(CombinedWorkspaceState Workspaces)
{
    /// <summary>
    /// Synchronization requests by address.
    /// </summary>
    public ImmutableDictionary<object, ImmutableDictionary<string, SynchronizeDataRequest>> SynchronizationsByAddress { get; init; } = ImmutableDictionary<object, ImmutableDictionary<string, SynchronizeDataRequest>>.Empty;

    public JToken SerializedWorkspace { get; init; }
}


public class DataPersistencePlugin(IMessageHub hub, DataContext context) :
    MessageHubPlugin<DataPersistencePluginState>(hub),
    IMessageHandler<GetDataStateRequest>,
    IMessageHandlerAsync<UpdateDataStateRequest>,
    IMessageHandler<SynchronizeDataRequest>
{
    public DataContext Context { get; } = context;

    /// <summary>
    /// Upon start, it initializes the persisted state from the DB
    /// </summary>
    /// <returns></returns>
    public override async Task StartAsync()
    {
        await base.StartAsync();
        var loadedWorkspaces =
            (await Context.DataSources
                .Distinct()
                .ToAsyncEnumerable()
                .SelectAwait(async kvp =>
                    new KeyValuePair<object, WorkspaceState>(kvp.Key, await kvp.Value.DoInitialize()))
                .ToArrayAsync())
            .ToImmutableDictionary();
                

        InitializeState(new(new(loadedWorkspaces, Context)));
    }

    IMessageDelivery IMessageHandler<GetDataStateRequest>.HandleMessage(IMessageDelivery<GetDataStateRequest> request)
    {
        Hub.Post(State, o => o.ResponseFor(request));
        return request.Processed();
    }

    public async Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery<UpdateDataStateRequest> request, CancellationToken cancellationToken)
    {
        var events = request.Message.Events;
        await UpdateState(events);
        return request.Processed();

    }

    /// <summary>
    /// Here we need to group everything by data source and then by event, as the workspace might deliver
    /// the content in arbitrary order, mixing data partitions.
    /// </summary>
    /// <param name="requests">Requests to be processed</param>
    /// <returns></returns>
    private async Task UpdateState(IReadOnlyCollection<DataChangeRequest> requests)
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

            await using var transaction = await dataSource.StartTransactionAsync();
            foreach (var e in g.GroupBy(x => x.Event))
            {
                var eventType = e.Key;
                foreach (var typeGroup in e.GroupBy(x => x.Type))
                    workspace = ProcessRequest(eventType, typeGroup.Key, typeGroup.Select(x => x.Instance), dataSource, workspace);
            }
            await transaction.CommitAsync();
            SynchronizeAndUpdate(dataSourceId, workspace);
        }
    }

    private void SynchronizeAndUpdate(object dataSourceId, WorkspaceState workspace)
    {
        UpdateState(s => s with {Workspaces = s.Workspaces.UpdateWorkspace(dataSourceId, workspace) });
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
    private WorkspaceState ProcessRequest(DataChangeRequest request, Type elementType, IEnumerable<object> instances, DataSource dataSource, WorkspaceState workspace)
    {
        if (!dataSource.GetTypeConfiguration(elementType, out var typeConfig))
            return workspace;
        var toBeUpdated = instances.ToDictionary(typeConfig.GetKey);
        var existing = workspace.Data.GetValueOrDefault(elementType) ?? ImmutableDictionary<object, object>.Empty;
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
            Data = workspace.Data.SetItem(typeConfig.ElementType, existingInstances.SetItems(toBeUpdatedInstances)),
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
            Data = workspace.Data.SetItem(typeConfig.ElementType, existingInstances.RemoveRange(toBeUpdatedInstances.Keys)),
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

    IMessageDelivery IMessageHandler<SynchronizeDataRequest>.HandleMessage(IMessageDelivery<SynchronizeDataRequest> request)
    {
        UpdateState(s =>
            s with
            {
                SynchronizationsByAddress = s.SynchronizationsByAddress
                    .SetItem(
                        request.Sender,
                        (s.SynchronizationsByAddress.GetValueOrDefault(request.Sender) ??
                         ImmutableDictionary<string, SynchronizeDataRequest>.Empty)
                        .SetItem(request.Message.Id, request.Message))
            });

        var serializedWorkspace = GetSerializedWorkspace();
        if (!string.IsNullOrEmpty(request.Message.JsonPath))
            serializedWorkspace = serializedWorkspace.SelectToken(request.Message.JsonPath);
        Hub.Post(new DataChanged(Hub.Version) { Changes = serializedWorkspace }, o => o.ResponseFor(request));

        return request.Processed();
    }


    [Inject] private ISerializationService serializationService;
    private JToken GetSerializedWorkspace()
    {
        if (State.SerializedWorkspace != null)
            return State.SerializedWorkspace;

        var ret = State.Workspaces.WorkspacesByKey.Values.Aggregate(ImmutableDictionary<string, ImmutableList<ImmutableDictionary<string, object>>>.Empty,
            (dict, ws) => dict.SetItems(ws.Data.Select(kvp =>
                new KeyValuePair<string, ImmutableList<ImmutableDictionary<string, object>>>(kvp.Key.Name, SerializeEntities(kvp.Value)))));
        var token = JToken.FromObject(ret);
        UpdateState(s => s with { SerializedWorkspace = token });
        return token;
    }

    private ImmutableList<ImmutableDictionary<string, object>> SerializeEntities(ImmutableDictionary<object, object> instancesByKey)
    {
        return instancesByKey.Select(kvp => SerializeEntity(kvp.Key, kvp.Value))
            .ToImmutableList();
    }

    private ImmutableDictionary<string, object> SerializeEntity(object id, object instance)
    {
        var rawJson = serializationService.Serialize(instance);
        var dictionary = JsonConvert.DeserializeObject<ImmutableDictionary<string, object>>(rawJson.Content);
        return dictionary.SetItem("$id", id);
    }
}
