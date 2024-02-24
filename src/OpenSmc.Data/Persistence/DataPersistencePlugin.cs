using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Path;
using OpenSmc.Messaging;
using OpenSmc.Serialization;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Data.Persistence;



public record DataPersistencePluginState
{
    /// <summary>
    /// Synchronization requests by address.
    /// </summary>
    public ImmutableDictionary<object, DataSubscription> SubscriptionsByAddress { get; init; } = ImmutableDictionary<object, DataSubscription>.Empty;

    public JsonNode SerializedWorkspace { get; init; }
}

public record DataSubscription
{
    public ImmutableDictionary<string, DataSubscriptionItem> Collections { get; init; } = ImmutableDictionary<string, DataSubscriptionItem>.Empty;
}

public record DataSubscriptionItem(string Path)
{
    public JsonNode LastSynchronized { get; init; }
};

public class DataPersistencePlugin(IMessageHub hub, DataContext context) :
    MessageHubPlugin<DataPersistencePluginState>(hub),
    IMessageHandler<StartDataSynchronizationRequest>,
    IMessageHandler<DataChangedEvent>,
    IMessageHandler<DataChangeRequest>
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
        foreach (var type in Context.DataSources.Values.SelectMany(x => x.MappedTypes))
            typeRegistry.WithType(type);

        initializeTask = InitializeAllDataSources(cancellationToken);
        return Task.CompletedTask;
    }

    public override bool IsDeferred(IMessageDelivery delivery)
    {
        if (delivery.Message is DataChangedEvent)
            return false;
        return base.IsDeferred(delivery);
    }

    public override Task Initialized => initializeTask;


    private async Task InitializeAllDataSources(CancellationToken cancellationToken)
    {
        await Context.InitializeAsync(cancellationToken);
        InitializeState(new DataPersistencePluginState());
    }




    //private async Task UpdateDataSource(CancellationToken cancellationToken, object dataSourceId, IEnumerable<ChangeDescriptor> descriptors)
    //{
    //    var dataSource = Context.GetDataSource(dataSourceId);
    //    if (dataSource == null)
    //        throw new ArgumentException($"Data source {dataSourceId} is unknown.", nameof(dataSourceId));

    //    await using var transaction = await dataSource.StartTransactionAsync(cancellationToken);
    //    dataSource.Update(descriptors);
    //    await transaction.CommitAsync(cancellationToken);
    //}



    IMessageDelivery IMessageHandler<StartDataSynchronizationRequest>.HandleMessage(IMessageDelivery<StartDataSynchronizationRequest> request) 
        => StartSynchronization(request);

    private IMessageDelivery StartSynchronization(IMessageDelivery<StartDataSynchronizationRequest> request)
    {
        var address = request.Sender;

        DataSubscription subscription = (State.SubscriptionsByAddress.TryGetValue(address, out subscription)
            ? subscription
            : new());

        subscription = subscription
            with
            {
                // add up all items
                Collections = subscription.Collections.SetItems
                (
                    request.Message.JsonPaths
                        .Select(x => new KeyValuePair<string, DataSubscriptionItem>(x.Key, new(x.Value)))
                )
            };

        UpdateState(s =>
            s with
            {
                SubscriptionsByAddress =
                s.SubscriptionsByAddress.SetItem(address,subscription)
                    
            });

        UpdateSubscription(address, subscription, SynchronizationMode.Full);
        return request.Processed();
    }

    private void UpdateSubscriptions()
    {
        foreach (var (address, subscription) in State.SubscriptionsByAddress)
            UpdateSubscription(address, subscription, SynchronizationMode.Delta);
    }


    public enum SynchronizationMode{Full, Delta}
    private void UpdateSubscription(object address, DataSubscription subscription, SynchronizationMode mode)
    {
        var serializedWorkspace = GetSerializedWorkspace();
        List<CollectionChange> changes = new();
        foreach (var (collection, item) in subscription.Collections.ToArray())
        {
            var evaluated = JsonPath.Parse(item.Path).Evaluate(serializedWorkspace);
            var match = evaluated.Matches switch
            {
                { Count: 1 } => evaluated.Matches[0].Value,
                { Count: > 1 } => new JsonArray(evaluated.Matches.Select(x => x.Value).ToArray()),
                _ => null
            };
            if (match != null)
            {
                var change = item.LastSynchronized == null
                    ? new CollectionChange(collection, match, CollectionChangeType.Full)
                    : GetPatch(collection, item, match);
                if (change != null)
                {

                    subscription = subscription with
                    {
                        Collections =
                        subscription.Collections.SetItem(collection, item with { LastSynchronized = match })
                    };
                    changes.Add(change);
                }
            }

        }
        if (changes.Any())
            Hub.Post(new DataChangedEvent(Hub.Version, changes), o => o.WithTarget(address));


        UpdateState(s =>
            s with
            {
                SubscriptionsByAddress = s.SubscriptionsByAddress
                    .SetItem(address, subscription)
            });

    }

    private static CollectionChange GetPatch(string collection, DataSubscriptionItem item, JsonNode match)
    {
        var patch = item.LastSynchronized.CreatePatch(match);
        if(!patch.Operations.Any())
            return null;
        return new CollectionChange(collection, patch, CollectionChangeType.Patch);
    }


    private JsonNode GetSerializedWorkspace()
    {
        var ret = State.SerializedWorkspace;
        if (ret == null)
        {
            UpdateState(s => s with { SerializedWorkspace = ret = CreateSynchronizedWorkspace() });
        }

        return ret;
    }

    private JsonNode CreateSynchronizedWorkspace()
        =>
            Context.GetSerializedWorkspace();



    public IMessageDelivery HandleMessage(IMessageDelivery<DataChangedEvent> request)
    {
        // this happens during initialization
        if (State == null)
            return request;

        var dataSourceId = request.Sender;
        var @event = request.Message;
        return UpdateState(request, dataSourceId, @event);
    }

    private IMessageDelivery UpdateState(IMessageDelivery request, object dataSourceId, DataChangedEvent @event)
    {
        var dataSource = Context.GetDataSource(dataSourceId);
        if (dataSource == null)
            return request.Ignored();
        dataSource.Synchronize(@event);


        return request.Processed();
    }




    public IMessageDelivery HandleMessage(IMessageDelivery<DataChangeRequest> request)
    {
        throw new NotImplementedException();
    }
}
