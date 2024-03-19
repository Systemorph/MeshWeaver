using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public class PatchSubscriber(IMessageHub hub, IMessageDelivery<SubscribeRequest> request, ISerializationService serializationService, IReadOnlyDictionary<string, ITypeSource> typeSources) : IObserver<WorkspaceState>
{
    private JsonNode LastSynchronized { get; set; }
    private readonly JsonSerializerOptions options = serializationService.Options(typeSources);
    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(WorkspaceState workspace)
    {
        var value = workspace.Reduce(request.Message.Reference);
        if (value == null)
            return;



        var node = value switch
                           {
                               JsonNode n => n,
                               EntityStore store => JsonSerializer.SerializeToNode(store, options),
                               _ => JsonNode.Parse(serializationService.SerializeToString(value))
                           };


        var dataChanged = LastSynchronized == null
            ? new DataChangedEvent(hub.Version, new RawJson(node.ToJsonString()), ChangeType.Full)
            : new DataChangedEvent(hub.Version, new(JsonSerializer.Serialize(LastSynchronized.CreatePatch(node))), ChangeType.Patch);

        hub.Post(dataChanged, o => o.ResponseFor(request));
        LastSynchronized = node;

    }
}