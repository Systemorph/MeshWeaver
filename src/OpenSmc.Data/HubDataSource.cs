using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Serialization;

namespace OpenSmc.Data;

public record HubDataSource(object Id) : DataSource<HubDataSource>(Id)
{
    private ISerializationService serializationService;

    protected override Task<WorkspaceState> InitializeAsync(IMessageHub hub, CancellationToken cancellationToken)
    {
        serializationService = hub.ServiceProvider.GetRequiredService<ISerializationService>();
        var collections = TypeSources.Values.Select(ts => (ts.CollectionName, Path:$"$['{ts.CollectionName}']")).ToDictionary(x => x.CollectionName, x => x.Path);
        var subscribeRequest = 
            hub.Post(new StartDataSynchronizationRequest(collections),
                o => o.WithTarget(Id));
        var tcs = new TaskCompletionSource<WorkspaceState>(cancellationToken);
        hub.RegisterCallback(subscribeRequest, response =>
            {
                tcs.SetResult(ParseToWorkspace(response.Message));
                return subscribeRequest.Processed();
            },
            cancellationToken);
        return tcs.Task;
    }

    private WorkspaceState ParseToWorkspace(DataChangedEvent dataChanged)
    {
        var state = (DataSynchronizationState)dataChanged;
        return new(this)
        {
            Data = ConvertToDataByType(state)
        };
    }

    private ImmutableDictionary<string, ImmutableDictionary<object, object>> ConvertToDataByType(DataSynchronizationState state)
    {
        var node = JsonNode.Parse(state.Data);
        if (node is JsonObject obj)
            return obj.Select(x =>
                new KeyValuePair<string, ImmutableDictionary<object, object>>
                (
                    x.Key,
                    ConvertToDictionary((JsonArray)x.Value))
                )
                .ToImmutableDictionary();
        return ImmutableDictionary<string, ImmutableDictionary<object, object>>.Empty;
    }

    private ImmutableDictionary<object, object> ConvertToDictionary(JsonArray array)
    {
        return array
            .Select(DeserializeArrayElements)
            .Where(x => x.Key != null)
            .ToImmutableDictionary();
    }

    private KeyValuePair<object, object> DeserializeArrayElements(JsonNode node)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue("$id", out var id) || id == null)
            return default;

        return new KeyValuePair<object, object>(serializationService.Deserialize(id.ToString()),
            serializationService.Deserialize(node.ToString()));
    }


}