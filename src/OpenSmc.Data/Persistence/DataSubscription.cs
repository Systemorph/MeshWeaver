using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data.Persistence;

public class DataSubscription : IDisposable
{
    private readonly ISerializationService serializationService;
    private readonly IMessageHub hub;
    private readonly IMessageDelivery<SubscribeDataRequest> request;
    private readonly IDisposable subscription;

    private JsonNode LastSynchronized { get; set; }

    public DataSubscription(
        IMessageHub hub,
        IMessageDelivery<SubscribeDataRequest> request,
        IObservable<WorkspaceState> stateStream, 
        WorkspaceState init)
    {
        serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
        this.hub = hub;
        this.request = request;
        var stream = stateStream
            .StartWith(init)
            .Select(ws => ws.Reduce((dynamic)request.Message.Reference))
            .DistinctUntilChanged()
            .Replay(1)
            .RefCount();

        subscription = stream.Subscribe(SendPatch);
    }

    private void SendPatch(dynamic reference)
    {
        JsonNode node = reference as JsonNode ?? JsonNode.Parse(serializationService.SerializeToString(reference));
        var dataChanged =
            LastSynchronized == null
                ? new DataChangedEvent(hub.Version, new(node.ToJsonString()), ChangeType.Full)
                : new DataChangedEvent(hub.Version, new(JsonSerializer.Serialize(LastSynchronized.CreatePatch(node))),
                    ChangeType.Patch);
        hub.Post(dataChanged, o => o.ResponseFor(request));
        LastSynchronized = node;
    }


    public ImmutableDictionary<string, WorkspaceReference> Collections { get; init; } = ImmutableDictionary<string, WorkspaceReference>.Empty;

    public void Dispose()
    {
        subscription.Dispose();
    }
}