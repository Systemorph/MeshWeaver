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

    

    public DataChangedEvent Commit()
    {
        var newWorkspace = GetSerializedWorkspace();
        var dataChanged = CurrentWorkspace == null
            ? new DataChangedEvent(Hub.Version, newWorkspace, ChangeType.Full)
            : new DataChangedEvent(Hub.Version, CurrentWorkspace.CreatePatch(newWorkspace), ChangeType.Patch);

        CurrentWorkspace = newWorkspace;
        UpdateSubscriptions();
        return dataChanged;
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


    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        var collections = TypeSources
            .Values
            .Select(ts => (ts.CollectionName, Path: $"$['{ts.CollectionName}']")).ToDictionary(x => x.CollectionName, x => x.Path);
        var startDataSynchronizationRequest = new StartDataSynchronizationRequest(collections);



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

    public DataChangedEvent Subscribe(StartDataSynchronizationRequest request, object address)
    {
        DataSubscription subscription = (SubscriptionsByAddress.TryGetValue(address, out subscription)
            ? subscription
            : new());

        subscription = subscription
            with
        {
            // add up all items
            Collections = subscription.Collections.SetItems
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
        var serializedSubscription = new JsonObject();

        foreach (var (collection, path) in subscription.Collections.ToArray())
        {
            var evaluated = JsonPath.Parse(path).Evaluate(CurrentWorkspace);
            var match = evaluated.Matches switch
            {
                { Count: 1 } => evaluated.Matches[0].Value,
                { Count: > 1 } => new JsonArray(evaluated.Matches.Select(x => x.Value).ToArray()),
                _ => null
            };
            if (match != null)
            {
                serializedSubscription.Add(collection, match);
            }

        }

        var change = subscription.LastSynchronized == null || mode == ChangeType.Full
            ? new DataChangedEvent(Hub.Version, serializedSubscription, ChangeType.Full)
            : CreatePatch(subscription, serializedSubscription);


        subscription.LastSynchronized = serializedSubscription;

        return change;
    }

    private DataChangedEvent CreatePatch(DataSubscription subscription, JsonObject serializedSubscription)
    {
        var patch = subscription.LastSynchronized.CreatePatch(serializedSubscription);
        if (!patch.Operations.Any())
            return null;
        return new(Hub.Version, patch, ChangeType.Patch);
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

    public void Initialize(IEnumerable<EntityDescriptor> allData)
    {
        CurrentWorkspace = serializationService.SerializeState(allData);
    }

    public void Synchronize(DataChangedEvent @event)
    {
        var change = @event.Change?.ToString();
        var type = @event.Type;
        if (string.IsNullOrEmpty(change))
            return;

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
        CurrentWorkspace = currentWorkspace;

        UpdateWorkspace(CurrentWorkspace
            .ToDictionary(
                x => x.Key,
                x => serializationService.ConvertToData((JsonArray)x.Value))
        );
    }

    public void UpdateWorkspace(IReadOnlyDictionary<string, IEnumerable<EntityDescriptor>> descriptors)
    {
        foreach (var typeSource in TypeSources.Values)
            typeSource.Initialize(descriptors.GetValueOrDefault(typeSource.CollectionName) ?? Array.Empty<EntityDescriptor>());
    }


    public void Rollback()
    {
    }
}
