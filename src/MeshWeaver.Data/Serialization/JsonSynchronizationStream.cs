using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data.Serialization;

public static class JsonSynchronizationStream
{
    private static ILogger GetLogger(IServiceProvider serviceProvider)
    {
        try
        {
            return serviceProvider.GetService<ILoggerFactory>()
                       ?.CreateLogger(typeof(JsonSynchronizationStream))
                   ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger(typeof(JsonSynchronizationStream));
        }
        catch (ObjectDisposedException)
        {
            return Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger(typeof(JsonSynchronizationStream));
        }
    }

    internal static ISynchronizationStream CreateExternalClient<TReduced, TReference>(
        this IWorkspace workspace,
        Address owner,
        TReference reference,
        bool impersonateAsHub = false
    )
    where TReference : WorkspaceReference
    {
        var hub = workspace.Hub;
        if (hub.RunLevel > MessageHubRunLevel.Started)
            throw new ObjectDisposedException($"ParentHub {hub.Address} is disposing, cannot create stream for {reference}.");

        var logger = GetLogger(hub.ServiceProvider);
        // link to deserialized world. Will also potentially link to workspace.
        var partition = reference is IPartitionedWorkspaceReference p ? p.Partition : null;

        var reduced = new SynchronizationStream<TReduced>(
                new(owner, partition),
                hub,
                reference,
                workspace.ReduceManager.ReduceTo<TReduced>(),
                config => config
                    .WithClientId(config.Stream.StreamId)
            );


        if (typeof(TReduced) == typeof(JsonElement))
            reduced.RegisterForDisposal(
                reduced
                    .ToDataChanged<TReduced, PatchDataChangeRequest>(c => reduced.ClientId.Equals(c.ChangedBy))
                    .Synchronize()
                    .Where(x => x is not null)
                    .Subscribe(e =>
                    {
                        logger.LogDebug("Stream {streamId} sending change notification to owner {owner}",
                            reduced.StreamId, reduced.Owner);
                        hub.Post(e, o => o.WithTarget(reduced.Owner));
                    },
                    ex => logger.LogDebug(ex, "Stream {streamId} errored", reduced.StreamId))
            );

        else if (!owner.Equals(hub.Address))
            reduced.RegisterForDisposal(
                reduced
                    .ToDataChangeRequest(c => reduced.ClientId.Equals(c.StreamId))
                    .Synchronize()
                    .Where(x => x.Creations.Any() || x.Deletions.Any() || x.Updates.Any())
                    .Subscribe(e =>
                    {
                        logger.LogDebug("Stream {streamId} sending change notification to owner {owner}",
                            reduced.StreamId, reduced.Owner);
                        e = e with { ClientId = reduced.StreamId };
                        var delivery = hub.Post(e, o => o.WithTarget(reduced.Owner));
                        if (delivery != null)
                        {
                            _ = hub.RegisterCallback(delivery, (response, _) =>
                            {
                                if (response is IMessageDelivery<DataChangeResponse> { Message.Status: DataChangeStatus.Failed } failed)
                                {
                                    logger.LogError("Stream {streamId} DataChangeRequest failed: {Error}",
                                        reduced.StreamId, failed.Message.Log?.Messages
                                            .Where(m => m.LogLevel >= LogLevel.Error)
                                            .Select(m => m.Message)
                                            .FirstOrDefault() ?? "Unknown error");
                                    reduced.OnError(new InvalidOperationException(
                                        $"DataChangeRequest failed for stream {reduced.StreamId}"));
                                }
                                return Task.FromResult(response);
                            }, CancellationToken.None);
                        }
                    },
                    ex => logger.LogDebug(ex, "Stream {streamId} errored", reduced.StreamId))
            );


        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var identity = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        var subscribeDelivery = reduced.Hub.Post(new SubscribeRequest(reduced.StreamId, reference) { Identity = identity },
            o => impersonateAsHub ? o.WithTarget(owner).ImpersonateAsHub(hub.Address) : o.WithTarget(owner));

        // Register callback on parent hub to catch DeliveryFailure responses
        // (e.g., from AccessControlPipeline rejecting the SubscribeRequest).
        // The response routes through the parent hub first; without a callback here,
        // the parent drops it (no matching callback), and the stream hangs forever.
        if (subscribeDelivery != null)
        {
            hub.RegisterCallback(subscribeDelivery,
                (delivery, _) =>
                {
                    if (delivery.Message is DeliveryFailure failure)
                    {
                        logger.LogWarning("SubscribeRequest for stream {StreamId} failed: {Message}",
                            reduced.StreamId, failure.Message);
                        reduced.OnError(new DeliveryFailureException(failure));
                        return Task.FromResult(delivery.Processed());
                    }
                    // Non-failure responses: forward to stream hub
                    reduced.Hub.DeliverMessage(delivery);
                    return Task.FromResult(delivery.Processed());
                }, default);
        }

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
        var logger = GetLogger(hub.ServiceProvider);

        var fromWorkspace = workspace
            .ReduceManager
            .ReduceStream<TReduced>(
                workspace,
                request.Reference, config => config.WithClientId(request.StreamId).WithSubscriber(request.Subscriber)
            );

        var reduced =
            fromWorkspace as ISynchronizationStream<TReduced>
            ?? throw new DataSourceConfigurationException(
                $"No reducer defined for {typeof(TReference).Name} from  {typeof(TReference).Name}"
            );


        // Use single synchronized subscription for both initial data and ongoing changes
        var isFirst = true;
        reduced.RegisterForDisposal(
            reduced
                .ToDataChanged<TReduced, DataChangedEvent>(c => isFirst || !reduced.ClientId.Equals(c.ChangedBy))
                .Synchronize()
                .Where(x => x is not null)
                .Select(x => x!)
                .Subscribe(e =>
                {
                    if (isFirst)
                    {
                        isFirst = false;
                        logger.LogDebug("Owner {owner} sending initial data to subscriber {subscriber}", reduced.Owner, request.Subscriber);
                    }
                    else
                    {
                        logger.LogDebug("Owner {owner} sending change notification to subscriber {subscriber}", reduced.Owner, request.Subscriber);
                    }
                    hub.Post(e, o => o.WithTarget(request.Subscriber));
                },
                ex =>
                {
                    logger.LogWarning(ex, "Workspace stream error for subscriber {Subscriber}, propagating DeliveryFailure", request.Subscriber);
                    hub.Post(new DeliveryFailure(null!, ex.Message)
                    {
                        ErrorType = ErrorType.Failed,
                    }, o => o.WithTarget(request.Subscriber));
                })
        );

        // NOTE: The following subscription was causing an infinite feedback loop.
        // When a client sends a DataChangeRequest, the workspace processes it and updates the stream.
        // The stream emits with ChangedBy = ClientId, matching the predicate below, which calls
        // RequestChange() again, creating an infinite loop.
        // All changes should flow through DataChangeRequest messages, not through stream subscriptions.
        // Removed to fix the feedback loop bug.

        // // outgoing data changed
        // reduced.RegisterForDisposal(
        //     reduced
        //         .ToDataChangeRequest(c => reduced.ClientId.Equals(c.ChangedBy))
        //         .Synchronize()
        //         .Subscribe(e =>
        //         {
        //             logger.LogDebug("Issuing change request from stream {subscriber} to owner {owner}", reduced.StreamId, reduced.Owner);
        //             reduced.Host.GetWorkspace().RequestChange(e, null, null);
        //         })
        // );

        return reduced;
    }
    private static IObservable<TChange?> ToDataChanged<TReduced, TChange>(
        this ISynchronizationStream<TReduced> stream, Func<ChangeItem<TReduced>, bool> predicate) where TChange : JsonChange =>
        stream
            .Synchronize()
            .Where(predicate)
            .Select(x =>
            {
                var logger = GetLogger(stream.Hub.ServiceProvider);
                logger.LogDebug("ToDataChanged processing change item: StreamId={StreamId}, ChangeType={ChangeType}, ChangedBy={ChangedBy}, UpdatesCount={UpdatesCount}",
                    stream.ClientId, x.ChangeType, x.ChangedBy, x.Updates.Count);

                var currentJson = stream.Get<JsonElement?>();
                if (currentJson is null || x.ChangeType == ChangeType.Full)
                {
                    logger.LogDebug("Processing full change for stream {StreamId}, currentJson is null: {IsNull}", stream.ClientId, currentJson is null);
                    var previousJson = currentJson;
                    currentJson = JsonSerializer.SerializeToElement(x.Value, x.Value?.GetType() ?? typeof(object), stream.Host.JsonSerializerOptions);
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
                    var patch = x.Updates.ToJsonPatch(stream.Host.JsonSerializerOptions, stream.Reference as WorkspaceReference);
                    var patchJson = JsonSerializer.Serialize(patch, stream.Host.JsonSerializerOptions);
                    // Apply patch with correct RFC 6901 unescaping
                    // The json-everything library doesn't properly unescape ~1 -> / in property names
                    (currentJson, _) = ApplyPatchWithCorrectUnescaping(patchJson, currentJson.Value, stream.Host.JsonSerializerOptions);
                    stream.Set(currentJson);
                    return (TChange?)Activator.CreateInstance
                    (
                        typeof(TChange),
                        stream.ClientId,
                        x.Version,
                        new RawJson(patchJson),
                        x.ChangeType,
                        x.ChangedBy ?? string.Empty
                    );
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
                var id = p.Path.SegmentCount == 0 ? null : p.Path.GetSegment(1).ToString();
                var rawCollection = p.Path.GetSegment(0).ToString();

                // Normalize collection name using TypeRegistry to ensure consistency
                // This fixes the bug where JsonPatch paths contain full type names 
                // but CollectionsReference expects short names from TypeRegistry
                var collection = typeRegistry?.TryGetType(rawCollection, out var typeDefinition) == true
                    ? typeDefinition!.CollectionName
                    : rawCollection;

                var pointer = id == null ? JsonPointer.Create(collection) : JsonPointer.Create(collection, id);
                return new EntityUpdate(
                        collection,
                        DecodePointerSegment(id, options)!,
                        pointer.Evaluate(updated)!
                    )
                { OldValue = pointer.Evaluate(current) };
            })
            .DistinctBy(x => new { x.Id, x.Collection })
            .ToArray();

    internal static (JsonElement, JsonPatch) UpdateJsonElement<TChange>(this TChange request, JsonElement? currentJson, JsonSerializerOptions options) where TChange : JsonChange
    {
        if (request.ChangeType == ChangeType.Full)
        {
            return (JsonDocument.Parse(request.Change.Content).RootElement, null!);
        }

        if (currentJson is null)
            throw new InvalidOperationException("Current state is null, cannot patch.");

        // Apply patch operations manually with correct RFC 6901 unescaping
        // The json-everything library stores segments in escaped form and Apply uses
        // escaped property names, which is incorrect per RFC 6901
        return ApplyPatchWithCorrectUnescaping(request.Change.Content, currentJson.Value, options);
    }

    private static (JsonElement, JsonPatch) ApplyPatchWithCorrectUnescaping(string patchJson, JsonElement currentJson, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.Parse(patchJson);
        var currentNode = JsonSerializer.SerializeToNode(currentJson, options);
        var operations = new List<PatchOperation>();

        foreach (var opElement in doc.RootElement.EnumerateArray())
        {
            var op = opElement.GetProperty("op").GetString();
            var pathString = opElement.GetProperty("path").GetString()!;

            // Parse path segments with RFC 6901 unescaping
            var segments = ParsePathSegments(pathString);

            JsonNode? value = null;
            if (opElement.TryGetProperty("value", out var valueElement))
            {
                value = JsonSerializer.SerializeToNode(valueElement, options);
            }

            // Apply the operation manually with correct unescaping
            switch (op)
            {
                case "add":
                    ApplyAdd(currentNode!, segments, value);
                    break;
                case "replace":
                    ApplyReplace(currentNode!, segments, value);
                    break;
                case "remove":
                    ApplyRemove(currentNode!, segments);
                    break;
                default:
                    // For other operations, fall back to the library's Apply
                    var parsedPath = JsonPointer.Parse(pathString);
                    var operation = op switch
                    {
                        "move" when opElement.TryGetProperty("from", out var fromEl) =>
                            PatchOperation.Move(parsedPath, JsonPointer.Parse(fromEl.GetString()!)),
                        "copy" when opElement.TryGetProperty("from", out var fromEl) =>
                            PatchOperation.Copy(parsedPath, JsonPointer.Parse(fromEl.GetString()!)),
                        "test" => PatchOperation.Test(parsedPath, value),
                        _ => throw new InvalidOperationException($"Unknown patch operation: {op}")
                    };
                    operations.Add(operation);
                    break;
            }
        }

        // If there were any fallback operations, apply them
        if (operations.Count > 0)
        {
            var fallbackPatch = new JsonPatch(operations);
            var result = fallbackPatch.Apply(currentNode);
            return (JsonSerializer.SerializeToElement(result, options), fallbackPatch);
        }

        // Serialize back to JsonElement
        var resultElement = JsonSerializer.SerializeToElement(currentNode, options);
        // Create a dummy patch for the return value (we don't use it on the receiving end)
        var dummyPatch = JsonSerializer.Deserialize<JsonPatch>(patchJson, options)!;
        return (resultElement, dummyPatch);
    }

    private static string[] ParsePathSegments(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return Array.Empty<string>();

        // Split on / (first char is always /)
        var parts = path[1..].Split('/');
        var segments = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            // RFC 6901 unescape: ~1 -> / and ~0 -> ~ (order matters)
            segments[i] = parts[i].Replace("~1", "/").Replace("~0", "~");
        }
        return segments;
    }

    private static void ApplyAdd(JsonNode root, string[] segments, JsonNode? value)
    {
        if (segments.Length == 0)
            throw new InvalidOperationException("Cannot add at root path");

        var parent = NavigateToParent(root, segments);
        var key = segments[^1];

        if (parent is JsonObject obj)
        {
            obj[key] = value;
        }
        else if (parent is JsonArray arr)
        {
            if (key == "-")
                arr.Add(value);
            else if (int.TryParse(key, out var index))
                arr.Insert(index, value);
            else
                throw new InvalidOperationException($"Invalid array index: {key}");
        }
        else
        {
            throw new InvalidOperationException($"Cannot add property to {parent?.GetType().Name}");
        }
    }

    private static void ApplyReplace(JsonNode root, string[] segments, JsonNode? value)
    {
        if (segments.Length == 0)
            throw new InvalidOperationException("Cannot replace at root path");

        var parent = NavigateToParent(root, segments);
        var key = segments[^1];

        if (parent is JsonObject obj)
        {
            obj[key] = value;
        }
        else if (parent is JsonArray arr && int.TryParse(key, out var index))
        {
            arr[index] = value;
        }
        else
        {
            throw new InvalidOperationException($"Cannot replace in {parent?.GetType().Name}");
        }
    }

    private static void ApplyRemove(JsonNode root, string[] segments)
    {
        if (segments.Length == 0)
            throw new InvalidOperationException("Cannot remove at root path");

        var parent = NavigateToParent(root, segments);
        var key = segments[^1];

        if (parent is JsonObject obj)
        {
            obj.Remove(key);
        }
        else if (parent is JsonArray arr && int.TryParse(key, out var index))
        {
            arr.RemoveAt(index);
        }
        else
        {
            throw new InvalidOperationException($"Cannot remove from {parent?.GetType().Name}");
        }
    }

    private static JsonNode? NavigateToParent(JsonNode root, string[] segments)
    {
        JsonNode? current = root;
        // Navigate to parent (all segments except last)
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current is JsonObject obj)
            {
                current = obj[segment];
            }
            else if (current is JsonArray arr && int.TryParse(segment, out var index))
            {
                current = arr[index];
            }
            else
            {
                throw new InvalidOperationException($"Cannot navigate through {current?.GetType().Name} with segment {segment}");
            }
        }
        return current;
    }
    public static IReadOnlyCollection<EntityUpdate> ToEntityUpdates(
        this InstanceCollection current,
        CollectionReference reference,
        JsonElement updated,
        JsonPatch patch,
        JsonSerializerOptions options)
        => patch.Operations.Select(p =>
        {
            var id = p.Path.GetSegment(0);


            JsonPointer? pointer = id == string.Empty ? null : CreatePointerFromSegments(id.ToString());
            var idSegment = id == string.Empty ? null : JsonSerializer.Deserialize<object>(id.ToString(), options)!;
            return new EntityUpdate(
                reference.Name,
                idSegment,
                pointer?.Evaluate(updated) ?? updated
            )
            { OldValue = idSegment == null ? current.Instances : current.Instances.GetValueOrDefault(idSegment) };
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
        // Serialize InstanceCollection to JsonElement, apply patch with correct unescaping, then deserialize back
        var currentJson = JsonSerializer.SerializeToElement(current, options);
        var (updatedJson, patch) = ApplyPatchWithCorrectUnescaping(request.Change.Content, currentJson, options);
        var updated = updatedJson.Deserialize<InstanceCollection>(options);
        return (updated!, patch);
    }

    internal static IObservable<DataChangeRequest> ToDataChangeRequest<TStream>(
        this ISynchronizationStream<TStream> stream, Func<ChangeItem<TStream>, bool> predicate)
        => stream
            .Synchronize()
            .Where(predicate)
            .Select(x => x.Updates.ToDataChangeRequest(stream.ClientId));



    internal static DataChangeRequest ToDataChangeRequest(this IEnumerable<EntityUpdate> updates,
        string clientId)
    {
        return updates
            .GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(new DataChangeRequest() { ChangedBy = clientId }, (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;

                if (last == null && first == null)
                    return e;
                if (last == null)
                    return e.WithDeletions(first!);

                // Treat as update regardless of OldValue — OldValue may be null
                // when the change was deserialized from a remote stream (not serialized).
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
            WorkspaceReference<EntityStore> => CreateEntityStorePatch(options, updates),
            null => CreateEntityStorePatch(options, updates),
            // Single-object references (e.g. MeshNodeReference) — patch at root level
            _ => CreateSingleObjectPatch(options, updates)
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
                string[] pointerSegments = g.Key == null
                    ? []
                    :
                    [
                        JsonSerializer.Serialize(g.Key, options)
                    ];
                var parentPath = CreatePointerFromSegments(pointerSegments);
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

    private static JsonPatch CreateSingleObjectPatch(JsonSerializerOptions options, IEnumerable<EntityUpdate> updates)
    {
        // For single-object streams (e.g. MeshNodeReference), generate root-level patches
        // without collection/id path segments
        return new JsonPatch(updates
            .Aggregate(Enumerable.Empty<PatchOperation>(), (e, u) =>
            {
                var first = u.OldValue;
                var last = u.Value;
                if (last == null && first == null)
                    return e;
                if (first == null)
                    return e.Concat([PatchOperation.Add(JsonPointer.Empty, JsonSerializer.SerializeToNode(last, options))]);
                if (last == null)
                    return e.Concat([PatchOperation.Remove(JsonPointer.Empty)]);
                var patches = first.CreatePatch(last, options).Operations;
                return e.Concat(patches);
            }).ToArray());
    }

    private static JsonPointer CreatePointerFromSegments(params string[] pointerSegments)
    {
        // Manually build RFC 6901 pointer with proper escaping:
        // ~ → ~0, / → ~1 within each segment
        var escaped = string.Concat(pointerSegments.Select(s =>
            "/" + s.Replace("~", "~0").Replace("/", "~1")));
        return JsonPointer.Parse(escaped);
    }

    private static JsonPatch CreateEntityStorePatch(JsonSerializerOptions options, IEnumerable<EntityUpdate> updates)
    {
        return new JsonPatch(updates
            .GroupBy(x => new { x.Collection, x.Id })
            .Aggregate(Enumerable.Empty<PatchOperation>(), (e, g) =>
            {
                var first = g.First().OldValue;
                var last = g.Last().Value;

                string[] pointerSegments = g.Key.Id == null
                    ? [g.Key.Collection]
                    :
                    [
                        g.Key.Collection,
                        JsonSerializer.Serialize(g.Key.Id, options)
                    ];
                var parentPath = CreatePointerFromSegments(pointerSegments);
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
    public static string EncodePointerSegment(string? segment, JsonSerializerOptions options)
    {
        if (segment is null) return string.Empty;

        var ret = JsonSerializer.Serialize(segment, options);
        // RFC 6901: escape ~ as ~0 and / as ~1
        return ret;
    }
    public static object? DecodePointerSegment(string? segment, JsonSerializerOptions options)
    {
        if (segment is null) return null;
        // RFC 6901: escape ~ as ~0 and / as ~1

        segment = segment.Replace("~0", "~").Replace("~1", "/");
        var ret = JsonSerializer.Deserialize<object>(segment, options);
        return ret;
    }

}
