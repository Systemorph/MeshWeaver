using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Path;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Persistence;

public record HubDataSource(object Id, IMessageHub Hub) : DataSource<HubDataSource>(Id, Hub)
{
    protected override Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<ITransaction>(new DelegateTransaction(() => Commit(), Rollback));
    }




    private readonly bool isExternalDataSource = !Id.Equals(Hub.Address);
    public DataChangeResponse Commit()
    {
        var newWorkspace = GetSerializedWorkspace();
        var dataChanged = CurrentWorkspace == null
            ? new DataChangedEvent(Hub.Version, newWorkspace, ChangeType.Full)
            : new DataChangedEvent(Hub.Version, JsonSerializer.Serialize(CurrentWorkspace.CreatePatch(newWorkspace)), ChangeType.Patch);

        if (isExternalDataSource)
            CommitTransactionExternally(dataChanged);

        CurrentWorkspace = newWorkspace;
        UpdateSubscriptions();
        return new DataChangeResponse(Hub.Version, DataChangeStatus.Committed, dataChanged);
    }

    public override IReadOnlyCollection<DataChangeRequest> Change(DataChangeRequest request) 
        => request is not PatchChangeRequest patch 
            ? base.Change(request) 
            : Change(patch).ToArray();

    private IEnumerable<DataChangeRequest> Change(PatchChangeRequest patch)
    {
        var change = patch.Change;
        CurrentWorkspace = ParseWorkspace(ChangeType.Patch, change.ToString());

        foreach (var (collection, node) in CurrentWorkspace)
        {
            var typeSource = GetTypeSource(collection);
            foreach (var update in typeSource.Update(serializationService.ConvertToData((JsonArray)node), true))
                yield return update;
        }
    }


    private void CommitTransactionExternally(DataChangedEvent dataChanged)
    {
        var request = Hub.Post(new PatchChangeRequest(dataChanged.Change), o => o.WithTarget(Id));
        Hub.RegisterCallback(request, HandleCommitResponse);
    }

    private IMessageDelivery HandleCommitResponse(IMessageDelivery<DataChangeResponse> response)
    {
        if (response.Message.Status == DataChangeStatus.Committed)
            return response.Processed();

        // TODO V10: Here we have to put logic to revert the state if commit has failed. (26.02.2024, Roland Bürgi)
        return response.Ignored();
    }

    private readonly ISerializationService serializationService =
        Hub.ServiceProvider.GetRequiredService<ISerializationService>();

    public JsonObject GetSerializedWorkspace()
    {
        return serializationService.SerializeState(GetData());
    }


    protected override HubDataSource WithType<T>(Func<ITypeSource, ITypeSource> typeSource)
        => WithType<T>(x => (TypeSourceWithType<T>)typeSource.Invoke(x));

    public HubDataSource WithType<T> (Func<TypeSourceWithType<T>, TypeSourceWithType<T>> typeSource)
        => WithTypeSource(typeof(T), typeSource.Invoke(new TypeSourceWithType<T>(Id, Hub)));


    private readonly ITypeRegistry typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        typeRegistry.WithTypes(TypeSources.Values.Select(t => t.ElementType));
        var collections = 
            SyncAll ? null
                :
            TypeSources
            .Values
            .Select(ts => (ts.CollectionName, Path: $"$['{ts.CollectionName}']")).ToDictionary(x => x.CollectionName, x => x.Path);
        var startDataSynchronizationRequest = new SubscribeDataRequest(collections);

        var subscribeRequest =
            Hub.Post(startDataSynchronizationRequest,
                o => o.WithTarget(Id));

        var tcs = new TaskCompletionSource(cancellationToken);
        Hub.RegisterCallback(subscribeRequest, response =>
            {
                Synchronize(response.Message);
                tcs.SetResult();
                
                return subscribeRequest.Processed();
            },
            cancellationToken);
        return tcs.Task;
    }


    public IReadOnlyCollection<T> Get<T>() where T : class
        => GetTypeSource(typeof(T))?.GetData()?.Select(e => (T)e.Entity).ToArray() ?? Array.Empty<T>();
    public T Get<T>(object id) where T : class
        => (T)GetTypeSource(typeof(T))?.GetData(id);


    #region Subscriptions
    /// <summary>
    /// Synchronization requests by address.
    /// </summary>
    public ImmutableDictionary<object, DataSubscription> SubscriptionsByAddress { get; set; } = ImmutableDictionary<object, DataSubscription>.Empty;

    public DataChangedEvent Subscribe(SubscribeDataRequest request, object address)
    {
        DataSubscription subscription = (SubscriptionsByAddress.TryGetValue(address, out subscription)
            ? subscription
            : new());

        if (request.JsonPaths == null)
            subscription = subscription with { Collections = null };

        else
            subscription = subscription
                with
                {
                    // add up all items
                    Collections = subscription.Collections?.SetItems
                    (
                        request.JsonPaths
                    )
                };

        SubscriptionsByAddress = SubscriptionsByAddress.SetItem(address, subscription);


        var changes = UpdateSubscription(subscription, ChangeType.Patch);
        return changes;
    }

    private DataChangedEvent UpdateSubscription(DataSubscription subscription, ChangeType mode)
    {

        var serializedSubscription =
            subscription.Collections == null || subscription.Collections.Count == 0
            ? CurrentWorkspace
            : AssembleWorkspace(subscription);

        var change = subscription.LastSynchronized == null || mode == ChangeType.Full
            ? new DataChangedEvent(Hub.Version, serializedSubscription, ChangeType.Full)
            : CreatePatch(subscription, serializedSubscription);


        subscription.LastSynchronized = serializedSubscription;

        return change;
    }

    private JsonObject AssembleWorkspace(DataSubscription subscription)
    {
        var serializedSubscription = new JsonObject();

        foreach (var (collection, path) in subscription.Collections.ToArray())
        {
            var jsonPath = JsonPath.Parse(path);
            var evaluated = jsonPath.Evaluate(CurrentWorkspace);
            var match = evaluated.Matches switch
            {
                { Count: 1 } => evaluated.Matches[0].Value,
                { Count: > 1 } => new JsonArray(evaluated.Matches.Select(x => x.Value).ToArray()),
                _ => null
            };
            if (match != null)
            {
                serializedSubscription.Add(collection, match.DeepClone());
            }

        }

        return serializedSubscription;
    }

    private DataChangedEvent CreatePatch(DataSubscription subscription, JsonObject serializedSubscription)
    {
        var patch = subscription.LastSynchronized.CreatePatch(serializedSubscription);
        if (!patch.Operations.Any())
            return null;
        return new(Hub.Version, JsonSerializer.Serialize(patch), ChangeType.Patch);
    }



    private JsonObject CurrentWorkspace { get; set; }
    private void UpdateSubscriptions()
    {
        foreach (var (address, subscription) in SubscriptionsByAddress)
        {
            var change = UpdateSubscription(subscription, ChangeType.Patch);
            if (change != null)
                Hub.Post(change, o => o.WithTarget(address));
        }
    }
    #endregion

    public void Initialize(IReadOnlyDictionary<string, IReadOnlyCollection<EntityDescriptor>> allData)
    {
        CurrentWorkspace = serializationService.SerializeState(allData);
        foreach (var typeSource in TypeSources.Values)
            typeSource.Initialize(allData.GetValueOrDefault(typeSource.CollectionName) ??
                                  Array.Empty<EntityDescriptor>());
    }

    public void Synchronize(DataChangedEvent @event)
    {
        var change = @event.Change?.ToString();
        var type = @event.Type;
        if (string.IsNullOrEmpty(change))
            return;

        CurrentWorkspace = ParseWorkspace(type, change);

        UpdateWorkspace(CurrentWorkspace
            .ToDictionary(
                x => x.Key,
                x => serializationService.ConvertToData((JsonArray)x.Value))
        );
    }

    private JsonObject ParseWorkspace(ChangeType type, string change)
    {
        var currentWorkspace = type switch
        {
            ChangeType.Full =>
                (JsonObject)JsonNode.Parse(change),
            ChangeType.Patch =>
                (JsonObject)JsonSerializer.Deserialize<JsonPatch>(change)
                    .Apply(CurrentWorkspace.DeepClone())
                    .Result,
            _ => throw new ArgumentOutOfRangeException()
        };

        if (currentWorkspace == null)
            throw new ArgumentException("Cannot deserialize workspace");
        return currentWorkspace;
    }

    public void UpdateWorkspace(IReadOnlyDictionary<string, IReadOnlyCollection<EntityDescriptor>> descriptors)
    {
        foreach (var typeSource in TypeSources.Values)
            typeSource.Initialize(descriptors.GetValueOrDefault(typeSource.CollectionName) ?? Array.Empty<EntityDescriptor>());
    }


    public void Rollback()
    {
    }

    public void DeleteById(Type type, object[] ids)
    {
        if (!TypeSources.TryGetValue(type, out var ts))
            throw new ArgumentException($"Type {type.FullName} is not mapped in data source {Id}", nameof(type));
        ts.DeleteByIds(ids);
    }

    internal bool SyncAll { get; init; }

    public HubDataSource SynchronizeAll(bool synchronizeAll = true)
        => this with { SyncAll = synchronizeAll };
}
