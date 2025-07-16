using System.Reactive.Linq;
using System.Text.Json;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Activities;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data.Serialization;

public static class JsonSynchronizationStream
{
    internal static ISynchronizationStream CreateExternalClient<TReduced, TReference>(
        this IWorkspace workspace,
        Address owner,
        TReference reference
    )
    where TReference : WorkspaceReference
    {
        var hub = workspace.Hub;
        if (hub.IsDisposing)
            throw new ObjectDisposedException($"Hub {hub.Address} is disposing, cannot create stream.");

        var logger = hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(JsonSynchronizationStream));
        // link to deserialized world. Will also potentially link to workspace.
        var partition = reference is IPartitionedWorkspaceReference p ? p.Partition : null;

        TaskCompletionSource<TReduced>? tcs2 = null;
        var reduced = new SynchronizationStream<TReduced>(
                new(owner, partition),
                hub,
                reference,
                workspace.ReduceManager.ReduceTo<TReduced>(),
                config => GetJsonConfig(config)
                    .WithClientId(config.Stream.StreamId)
            );
        reduced.Initialize(ct => (tcs2 = new(ct)).Task,
            ex =>
            {
                logger.LogWarning(ex, "An error occurred updating data source {Stream}", reduced.StreamId);
                return Task.CompletedTask;
            });


        if(typeof(TReduced) == typeof(JsonElement))
            reduced.RegisterForDisposal(
                reduced
                    .ToDataChanged < TReduced, PatchDataChangeRequest > (c => reduced.ClientId.Equals(c.ChangedBy))
                    .Where(x => x is not null)
                    .Subscribe(e =>
                    {
                        logger.LogDebug("Stream {streamId} sending change notification to owner {owner}",
                            reduced.StreamId, reduced.Owner);
                        hub.Post(e, o => o.WithTarget(reduced.Owner));
                    })
            );

        else
            reduced.RegisterForDisposal(
                reduced
                    .ToDataChangeRequest(c => reduced.ClientId.Equals(c.ChangedBy))
                    .Where(x => x.Creations.Any() || x.Deletions.Any() || x.Updates.Any())
                    .Subscribe(e =>
                    {
                        logger.LogDebug("Stream {streamId} sending change notification to owner {owner}",
                            reduced.StreamId, reduced.Owner);
                        e = e with { ClientId = reduced.StreamId };
                        hub.Post(e, o => o.WithTarget(reduced.Owner));
                    })
            );


        var request = 
            hub.Post(new SubscribeRequest(reduced.StreamId, reference), o => o.WithTarget(owner));
        var task = hub.RegisterCallback(request!, c =>
        {
            logger.LogInformation("Retrieved {reference} from {owner}.", reduced.Reference, reduced.Owner);
            return c;
        });
        reduced.BindToTask(task);
        var first = true;
        reduced.RegisterForDisposal(
            reduced.Hub.Register<DataChangedEvent>(delivery =>
                {
                    if (first)
                    {
                        first = false;
                        var jsonElement = JsonDocument.Parse(delivery.Message.Change.Content).RootElement;
                        ((ISynchronizationStream)reduced).Set<JsonElement?>(jsonElement);
                        tcs2?.SetResult(jsonElement.Deserialize<TReduced>(reduced.Hub.JsonSerializerOptions)!);
                        return request!.Processed();
                    }
                    reduced.DeliverMessage(delivery);
                    return delivery.Forwarded();
                },
                d => reduced.StreamId.Equals(d.Message.StreamId)
            )
        );
        reduced.RegisterForDisposal(
            reduced.Hub.Register<UnsubscribeRequest>(
                delivery =>
                {
                    reduced.DeliverMessage(delivery);
                    return delivery.Forwarded();
                },
                d => reduced.StreamId.Equals(d.Message.StreamId)
            )
        );

        reduced.RegisterForDisposal(
            new AnonymousDisposable(
                () => hub.Post(new UnsubscribeRequest(reduced.StreamId), o => o.WithTarget(owner))
            )
        );




        return reduced;
    }

    internal static ISynchronizationStream CreateSynchronizationStream<TReduced, TReference>(
        this IWorkspace workspace,
        SubscribeRequest request
)
    where TReference : WorkspaceReference
    {
        var hub = workspace.Hub;
        var logger = workspace.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(JsonSynchronizationStream));

        var fromWorkspace = workspace
            .ReduceManager
            .ReduceStream<TReduced>(
                workspace,
                request.Reference, config => GetJsonConfig(config).WithClientId(request.StreamId)
            );

        var reduced =
            fromWorkspace as ISynchronizationStream<TReduced>
            ?? throw new DataSourceConfigurationException(
                $"No reducer defined for {typeof(TReference).Name} from  {typeof(TReference).Name}"
            );

        // forwarding unsubscribe
        reduced.RegisterForDisposal(
            reduced.Hub.Register<UnsubscribeRequest>(
                delivery =>
                {
                    reduced.DeliverMessage(delivery);
                    return delivery.Forwarded();
                },
                x => reduced.ClientId.Equals(x.Message.StreamId)
            )
        );

        // Incoming data changed register and dispatch to synchronization stream
        reduced.RegisterForDisposal(
            reduced.Hub
                .Register<DataChangedEvent>(
                    delivery =>
                    {
                        reduced.DeliverMessage(delivery);
                        return delivery.Forwarded();
                    },
                    x => reduced.ClientId.Equals(x.Message.StreamId)
                )
        );
        // Incoming data changed register and dispatch to synchronization stream
        reduced.RegisterForDisposal(
            reduced.Hub
                .Register<PatchDataChangeRequest>(
                    delivery =>
                    {
                        reduced.DeliverMessage(delivery);
                        return delivery.Forwarded();
                    },
                    x => reduced.ClientId.Equals(x.Message.ChangedBy)
                )
        );
        // Incoming data changed register and dispatch to synchronization stream
        reduced.RegisterForDisposal(
            reduced.Hub
                .Register<DataChangeRequest>(
                    delivery =>
                    {
                        reduced.DeliverMessage(delivery);
                        return delivery.Forwarded();
                    },
                    x => reduced.ClientId.Equals(x.Message.ChangedBy)
                )
        );

        // outgoing data changed
        reduced.RegisterForDisposal(
            reduced
                .ToDataChanged<TReduced,DataChangedEvent>(c => !reduced.ClientId.Equals(c.ChangedBy))
                .Where(x => x is not null)
                .Select(x => x!)
                .Subscribe(e =>
                {
                    logger.LogDebug("Owner {owner} sending change notification to subscriber {subscriber}", reduced.Owner, request.Subscriber);
                    hub.Post(e, o => o.WithTarget(request.Subscriber));
                })
        );

        // outgoing data changed
        reduced.RegisterForDisposal(
            reduced
                .ToDataChangeRequest(c => reduced.ClientId.Equals(c.ChangedBy))
                .Subscribe(e =>
                {
                    logger.LogDebug("Issuing change request from stream {subscriber} to owner {owner}", reduced.StreamId, reduced.Owner);
                    var activity = new Activity(ActivityCategory.DataUpdate, reduced.Hub);
                    reduced.Hub.GetWorkspace().RequestChange(e, activity, null);
                    activity.Complete(_ =>
                    {
                        /*TODO: Where to save?*/
                    });
                })
        );

        return reduced;
    }
    private static StreamConfiguration<TStream> GetJsonConfig<TStream>(
        StreamConfiguration<TStream> stream) =>
        stream.ConfigureHub(config =>
            config
                .WithHandler<DataChangedEvent>(
                    (hub, delivery) =>
                    {
                        UpdateStream(stream, delivery, hub);
                        return delivery.Processed();
                    }
                ).WithHandler<PatchDataChangeRequest>(
                    (hub, delivery) =>
                    {
                        UpdateStream(stream, delivery, hub);
                        return delivery.Processed();
                    }
                ).WithHandler<DataChangeRequest>(
                    (hub, delivery) =>
                    {
                        throw new NotSupportedException();
                        return delivery.Processed();
                    }
                ).WithHandler<UnsubscribeRequest>(
                (hub, delivery) =>
                {
                    hub.Dispose();
                    return delivery.Processed();
                })
        );

    private static void UpdateStream<TStream, TChange>(this StreamConfiguration<TStream> stream, IMessageDelivery<TChange> delivery, IMessageHub hub)
    where TChange : JsonChange
    {
        var currentJson = stream.Stream.Get<JsonElement?>();
        if (delivery.Message.ChangeType == ChangeType.Full)
        {
            currentJson = JsonSerializer.Deserialize<JsonElement>(delivery.Message.Change.Content);
            stream.Stream.Update(
                _ => new ChangeItem<TStream>(
                    currentJson.Value.Deserialize<TStream>(stream.Stream.Hub.JsonSerializerOptions)!,
                    stream.Stream.StreamId,
                    stream.Stream.Hub.Version), ex => SyncFailed(delivery, stream.Stream, ex)
            );

        }
        else
        {
            (currentJson, var patch) = delivery.Message.UpdateJsonElement(currentJson, hub.JsonSerializerOptions);
            stream.Stream.Update(
                state =>
                    stream.Stream.ToChangeItem(state!,
                        currentJson.Value,
                        patch,
                        delivery.Message.ChangedBy ?? stream.ClientId),
                ex => SyncFailed(delivery, stream.Stream, ex)
            );

        }
        stream.Stream.Set(currentJson);
    }

    private static Task SyncFailed<TStream>(IMessageDelivery delivery, ISynchronizationStream<TStream> stream, Exception exception)
    {
        stream.Hub.Post(new DeliveryFailure(delivery, exception.Message), o => o.ResponseFor(delivery));
        return Task.CompletedTask;
    }


    private static IObservable<TChange?> ToDataChanged<TReduced, TChange>(
        this ISynchronizationStream<TReduced> stream, Func<ChangeItem<TReduced>, bool> predicate) where TChange: JsonChange=>
        stream
            .Where(predicate)
            .Select(x =>
            {
                var logger = stream.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(JsonSynchronizationStream));
                logger.LogDebug("ToDataChanged processing change item: StreamId={StreamId}, ChangeType={ChangeType}, ChangedBy={ChangedBy}, UpdatesCount={UpdatesCount}", 
                    stream.ClientId, x.ChangeType, x.ChangedBy, x.Updates.Count);
                
                var currentJson = stream.Get<JsonElement?>();
                if (currentJson is null || x.ChangeType == ChangeType.Full)
                {
                    logger.LogDebug("Processing full change for stream {StreamId}, currentJson is null: {IsNull}", stream.ClientId, currentJson is null);
                    var previousJson = currentJson;
                    currentJson = JsonSerializer.SerializeToElement(x.Value, x.Value?.GetType() ?? typeof(object), stream.Hub.JsonSerializerOptions);
                    if (Equals(previousJson, currentJson))
                    {
                        logger.LogDebug("Previous JSON equals current JSON for stream {StreamId}, returning null", stream.ClientId);
                        return null;
                    }
                    stream.Set(currentJson);
                    logger.LogDebug("Generated full DataChangedEvent for stream {StreamId}", stream.ClientId);
                    return (TChange?)Activator.CreateInstance(
                        typeof(TChange),
                        stream.ClientId,
                        x.Version,
                        new RawJson(currentJson.ToString() ?? string.Empty),
                        ChangeType.Full,
                        x.ChangedBy ?? string.Empty);
                }
                else
                {
                    if (x.Updates.Count == 0)
                    {
                        logger.LogWarning("No updates in change item for stream {StreamId}, skipping DataChangedEvent generation. ChangeType: {ChangeType}, ChangedBy: {ChangedBy}", 
                            stream.ClientId, x.ChangeType, x.ChangedBy);
                        return null;
                    }
                    var patch = x.Updates.ToJsonPatch(stream.Hub.JsonSerializerOptions, stream.Reference as WorkspaceReference);
                    currentJson = patch.Apply(currentJson.Value);
                    stream.Set(currentJson);
                    return (TChange?)Activator.CreateInstance
                    (
                        typeof(TChange), stream.ClientId, x.Version, new RawJson(JsonSerializer.Serialize(patch, stream.Hub.JsonSerializerOptions)), x.ChangeType, x.ChangedBy ?? string.Empty);
                }


            });





    public static ChangeItem<TReduced>? ToChangeItem<TReduced>(
        this ISynchronizationStream<TReduced> stream,
        TReduced currentState,
        JsonElement currentJson,
        JsonPatch? patch,
        string changedBy)
    {
        return stream.ReduceManager.PatchFunction?.Invoke(stream, currentState, currentJson, patch, changedBy);
    }


    public static IReadOnlyCollection<EntityUpdate> ToEntityUpdates(
        this JsonElement current,
        JsonElement updated,
        JsonPatch patch,
        JsonSerializerOptions options,
        ITypeRegistry? typeRegistry = null)
        => patch.Operations.Select(p =>
            {
                var id = p.Path.Skip(1).FirstOrDefault();
                var rawCollection = p.Path.First();

                // Normalize collection name using TypeRegistry to ensure consistency
                // This fixes the bug where JsonPatch paths contain full type names 
                // but CollectionsReference expects short names from TypeRegistry
                var collection = typeRegistry?.TryGetType(rawCollection, out var typeDefinition) == true
                    ? typeDefinition!.CollectionName
                    : rawCollection;

                var pointer = id == null ? JsonPointer.Create(collection) : JsonPointer.Create(collection, id);
                return new EntityUpdate(
                        collection,
                        id == null ? null : JsonSerializer.Deserialize<object>(id, options)!,
                        pointer.Evaluate(updated)!
                    )
                    { OldValue = pointer.Evaluate(current) };
            })
            .DistinctBy(x => new { x.Id, x.Collection })
            .ToArray();

    internal static (JsonElement, JsonPatch) UpdateJsonElement<TChange>(this TChange request, JsonElement? currentJson, JsonSerializerOptions options) where TChange:JsonChange
    {
        if (request.ChangeType == ChangeType.Full)
        {
            return (JsonDocument.Parse(request.Change.Content).RootElement, null!);
        }

        if (currentJson is null)
            throw new InvalidOperationException("Current state is null, cannot patch.");
        var patch = JsonSerializer.Deserialize<JsonPatch>(request.Change.Content, options)!;
        return (patch.Apply(currentJson.Value), patch);
    }
    public static IReadOnlyCollection<EntityUpdate> ToEntityUpdates(
        this InstanceCollection current,
        CollectionReference reference,
        JsonElement updated,
        JsonPatch patch,
        JsonSerializerOptions options)
        => patch.Operations.Select(p =>
        {
            var id = p.Path.FirstOrDefault();


            var pointer = id == null ? null : JsonPointer.Create(id);
            return new EntityUpdate(
                reference.Name,
                id == null ? null : JsonSerializer.Deserialize<object>(id, options)!,
                pointer?.Evaluate(updated) ?? updated
            )
            { OldValue = id is null ? current.Instances : current.Instances.GetValueOrDefault(id) };
        })
        .DistinctBy(x => new { x.Id, x.Collection })
        .ToArray();

    internal static (InstanceCollection, JsonPatch) UpdateJsonElement(this DataChangedEvent request, InstanceCollection current, JsonSerializerOptions options)
    {
        if (request.ChangeType == ChangeType.Full)
        {
            return (JsonDocument.Parse(request.Change.Content).RootElement.Deserialize<InstanceCollection>()!, null!);
        }

        if (current is null)
            throw new InvalidOperationException("Current state is null, cannot patch.");
        var patch = JsonSerializer.Deserialize<JsonPatch>(request.Change.Content, options)!;
        var updated = patch.Apply(current);
        return (updated!, patch);
    }

    internal static IObservable<DataChangeRequest> ToDataChangeRequest<TStream>(
        this ISynchronizationStream<TStream> stream, Func<ChangeItem<TStream>, bool> predicate)
        => stream
            .Where(predicate)
            .Select(x => x.Updates.ToDataChangeRequest(stream.ClientId));



    internal static DataChangeRequest ToDataChangeRequest(this IEnumerable<EntityUpdate> updates, 
        string clientId)
    {
        return updates
            .GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(new DataChangeRequest(){ChangedBy = clientId}, (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;

                if (last == null && first == null)
                    return e;
                if (first == null)
                    return e.WithCreations(last!);
                if (last == null)
                    return e.WithDeletions(first);

                return e.WithUpdates(last);
            });
    }

    internal static JsonPatch ToJsonPatch(this IEnumerable<EntityUpdate> updates, 
        JsonSerializerOptions options,
        WorkspaceReference? streamReference)
    {
        return streamReference switch
        {
            CollectionReference collection => CreateCollectionPatch(collection, options, updates),
            _ => CreateEntityStorePatch(options, updates)
        };


    }

    private static JsonPatch CreateCollectionPatch(
        CollectionReference collection, 
        JsonSerializerOptions options,
        IEnumerable<EntityUpdate> updates)
    {
        var collectionName = collection.Name;
        return new JsonPatch(updates
            .Where(e => e.Collection == collectionName)
            .GroupBy(x => x.Id)
            .Aggregate(Enumerable.Empty<PatchOperation>(), (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;
                PointerSegment[] pointerSegments = g.Key == null
                    ? []
                    :
                    [
                        JsonSerializer.Serialize(g.Key, options)
                    ];
                var parentPath = JsonPointer.Create(pointerSegments);
                if (last == null && first == null)
                    return e;
                if (first == null)
                    return e.Concat([PatchOperation.Add(parentPath, JsonSerializer.SerializeToNode(last, options))]);
                if (last == null)
                    return e.Concat([PatchOperation.Remove(parentPath)]);
                var patches = first.CreatePatch(last, options).Operations;
                patches = patches.Select(p =>
                {
                    var newPath = parentPath.Combine(p.Path);
                    return CreatePatchOperation(p, newPath);
                }).ToArray();
                return e.Concat(patches);
            }).ToArray());
    }

    private static JsonPatch CreateEntityStorePatch(JsonSerializerOptions options, IEnumerable<EntityUpdate> updates)
    {
        return new JsonPatch(updates
            .GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(Enumerable.Empty<PatchOperation>(), (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;

                PointerSegment[] pointerSegments = g.Key.Id == null
                    ? [g.Key.Collection]
                    :
                    [
                        g.Key.Collection,
                        JsonSerializer.Serialize(g.Key.Id, options)
                    ];
                var parentPath = JsonPointer.Create(pointerSegments);
                if (last == null && first == null)
                    return e;
                if (first == null)
                    return e.Concat([PatchOperation.Add(parentPath, JsonSerializer.SerializeToNode(last, options))]);
                if (last == null)
                    return e.Concat([PatchOperation.Remove(parentPath)]);


                var patches = first.CreatePatch(last, options).Operations;

                patches = patches.Select(p =>
                {
                    var newPath = parentPath.Combine(p.Path);
                    return CreatePatchOperation(p, newPath);
                }).ToArray();

                return e.Concat(patches);
            }).ToArray());
    }


    private static PatchOperation CreatePatchOperation(PatchOperation original, JsonPointer newPath)
    {
        return original.Op switch
        {
            OperationType.Add => PatchOperation.Add(newPath, original.Value),
            OperationType.Remove => PatchOperation.Remove(newPath),
            OperationType.Replace => PatchOperation.Replace(newPath, original.Value),
            OperationType.Move => PatchOperation.Move(newPath, original.From),
            OperationType.Copy => PatchOperation.Copy(newPath, original.From),
            OperationType.Test => PatchOperation.Test(newPath, original.Value),
            _ => throw new InvalidOperationException($"Unsupported operation: {original.Op}")
        };
    }

}
