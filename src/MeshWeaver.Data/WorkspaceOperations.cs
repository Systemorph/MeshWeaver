using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Data;
using Json.Patch;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public static class WorkspaceOperations
{
    public static void Change(this IWorkspace workspace, DataChangeRequest change, Activity? activity, IMessageDelivery? request)
    {
        var allValid = true;
        var logger = workspace.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(WorkspaceOperations));
        logger.LogDebug("Updating workstream for workspace {Address} with {Creations} creations, {Updates} updates, {Deletions} deletions", workspace.Hub.Address, change.Creations.Count(), change.Updates.Count(), change.Deletions.Count());

        if (change.Creations.Any())
        {
            var (isValid, results) = workspace.ValidateCreation(change.Creations);
            if (!isValid)
            {
                allValid = false;
                foreach (var validationResult in results.Where(r => r != ValidationResult.Success))
                {
                    var scopes = new List<KeyValuePair<string, object>>
                    {
                        new("members", validationResult.MemberNames.ToArray()),
                        new("error", validationResult.ErrorMessage!)
                    };
                    activity?.LogError($"{validationResult.ErrorMessage}", scopes);
                    var message =
                        $"{string.Join(", ", validationResult.MemberNames)} invalid: {validationResult.ErrorMessage!}";

                    // Log validation errors (activityId: {activityId})
                    workspace.Hub.ServiceProvider.GetService<ILogger>()?.LogWarning("Validation error in activityId {ActivityId}: {Message}", activity?.Id, message);
                }
            }

        }

        if (change.Updates.Any())
        {
            var (isValid, results) = workspace.ValidateUpdate(change.Updates);
            if (!isValid)
            {
                allValid = false;
                foreach (var validationResult in results.Where(r => r != ValidationResult.Success))
                {
                    var scopes = new List<KeyValuePair<string, object>>
                    {
                        new("members", validationResult.MemberNames.ToArray())
                    };
                    var message =
                        $"{string.Join(", ", validationResult.MemberNames)} invalid: {validationResult.ErrorMessage!}";

                    // Log validation errors (activityId: {activityId})
                    activity?.LogError($"Validation error in {message}", scopes);
                }
            }

        }

        if (change.Deletions.Any())
        {
            var (isValid, results) = workspace.ValidateDeletion(change.Deletions);
            if (!isValid)
            {
                allValid = false;
                foreach (var validationResult in results.Where(r => r != ValidationResult.Success))
                {
                    var scopes = new List<KeyValuePair<string, object>>
                    {
                        new("members", validationResult.MemberNames.ToArray())
                    };
                    var message = string.Format("{0} invalid: {1}", string.Join(", ", validationResult.MemberNames), validationResult.ErrorMessage!);

                    // Log validation errors (activityId: {activityId})
                    activity?.LogError($"Validation error in activityId {message}", scopes);
                }
            }
        }

        if (allValid)
        {
            Update(activity, workspace, change, request);
        }
    }

    private static void UpdateFailed(IMessageDelivery? delivery, Exception? exception)
    {
        if (exception != null)
            throw new DataException($"Data update failed: {exception.Message}", exception);
    }

    private static void Update(Activity? activity, IWorkspace workspace, DataChangeRequest change, IMessageDelivery? request)
    {
        activity?.LogInformation("Starting Update");
        var logger = workspace.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(WorkspaceOperations));
        logger.LogDebug("Update called: Creations={Creations}, Updates={Updates}, Deletions={Deletions}",
            change.Creations.Count(), change.Updates.Count(), change.Deletions.Count());
        workspace.UpdateStreams(change, activity, request);
    }

    private static void UpdateStreams(this IWorkspace workspace, DataChangeRequest change, Activity? activity, IMessageDelivery? request)
    {
        var logger = workspace.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(WorkspaceOperations));
        logger.LogDebug("Updating streams for workspace {Address} with {Creations} creations, {Updates} updates, {Deletions} deletions", workspace.Hub.Address, change.Creations.Count(), change.Updates.Count(), change.Deletions.Count());
        foreach (var group in
                 change.Creations.Select(i => ClassifyForRouting(workspace, i, OperationType.Add))
                     .Concat(change.Updates.Select(i => ClassifyForRouting(workspace, i, OperationType.Replace)))
                     .Concat(change.Deletions.Select(i => ClassifyForRouting(workspace, i, OperationType.Remove)))
                     .GroupBy(x => (x.DataSource, x.Partition)))
        {
            if (group.Key.DataSource is null)
            {
                activity?.LogWarning("Types {types} could not be mapped to data source", string.Join(", ", group.Select(i => i.Instance.GetType().Name).Distinct()));
                continue;
            }

            var stream = group.Key.DataSource.GetStreamForPartition(group.Key.Partition);
            if (stream is null)
                throw new DataException($"Data source {group.Key.DataSource.Reference} does not have a stream for partition {group.Key.Partition}");
            if (!stream.Hub.Started.IsCompleted)
                throw new DataException($"Data source {group.Key.DataSource.Reference} for partition {group.Key.Partition} is not initialized.");

            // Start sub-activity for data update
            var subActivity = activity?.StartSubActivity(ActivityCategory.DataUpdate);


            // Synchronous update — the transform is pure in-memory; the stream's
            // handler serializes UpdateStreamRequests, so no retry logic is needed.
            stream!.Update(store =>
                {
                    var result = UpdateDataChangeRequest(store, change, logger, stream, subActivity, group);
                    subActivity?.Complete();
                    return result;
                },
                ex =>
                {
                    subActivity?.Complete();
                    UpdateFailed(request, ex);
                }
            );
        }
    }

    // Maps an instance to its routing tuple. An EntityDeltaUpdate (a minimal-bytes
    // string-delta carrying no CLR entity) is routed by its declared Collection +
    // Partition; a full entity by its CLR type + computed partition.
    private static (object Instance, OperationType Op, ITypeSource? TypeSource, IDataSource? DataSource, object? Partition)
        ClassifyForRouting(IWorkspace workspace, object instance, OperationType op)
    {
        if (instance is EntityDeltaUpdate d)
        {
            var ts = workspace.DataContext.GetTypeSource(d.Collection);
            return (instance, op, ts,
                ts is null ? null : workspace.DataContext.GetDataSourceForType(ts.TypeDefinition.Type),
                d.Partition);
        }
        var typeSource = workspace.DataContext.GetTypeSource(instance.GetType());
        return (instance, op, typeSource,
            workspace.DataContext.GetDataSourceForType(instance.GetType()),
            (typeSource as IPartitionedTypeSource)?.GetPartition(instance));
    }

    // Reconstructs the full entity from a minimal-bytes EntityDeltaUpdate by replaying the
    // splice onto the owner's CURRENT value (so a disjoint concurrent edit on the owner
    // survives). A full entity passes through untouched. A delta whose target no longer
    // exists can't be applied — log + drop (the subscriber reconciles on next sync).
    private static object? ResolveDelta(object instance, EntityStore currentStore,
        ISynchronizationStream<EntityStore> stream, ILogger logger)
    {
        if (instance is not EntityDeltaUpdate d)
            return instance;
        var current = currentStore.GetCollection(d.Collection)?.Instances.GetValueOrDefault(d.Id);
        if (current is null)
        {
            logger.LogWarning("[Delta] update for missing entity {Collection}/{Id} — dropping (no base to apply onto)",
                d.Collection, d.Id);
            return null;
        }
        return EntityDelta.Apply(current, d, stream.Host.JsonSerializerOptions);
    }

    private static ChangeItem<EntityStore>? UpdateDataChangeRequest(EntityStore? store, DataChangeRequest change, ILogger logger, ISynchronizationStream<EntityStore> stream, Activity? subActivity,
        IGrouping<(IDataSource? DataSource, object? Partition), (object Instance, OperationType Op, ITypeSource?
            TypeSource, IDataSource? DataSource, object? Partition)> group)
    {
        logger.LogDebug("Starting update of {Stream} with {StreamId}", stream.StreamIdentity, stream.StreamId);
        // For sub-activity logging, we use the main activity as we don't have direct access to sub-activity
        subActivity?.LogInformation("Updating Data Stream {Stream}", stream!.StreamIdentity);
        try
        {
            // Get the current store state (might be different from initial 'store' parameter if updates occurred)
            var currentStore = store ?? new EntityStore();

            var updates = group.GroupBy(x =>
                    (Op: (x.Op == OperationType.Add ? OperationType.Replace : x.Op), x.TypeSource))
                .Aggregate(new EntityStoreAndUpdates(currentStore, [], change.ChangedBy),
                    (storeAndUpdates, g) =>
                    {
                        if (g.Key.Op == OperationType.Add || g.Key.Op == OperationType.Replace)
                        {
                            var allInstances = g.Select(x => ResolveDelta(x.Instance, currentStore, stream, logger))
                                .Where(x => x is not null).Select(x => x!).ToList();
                            var invalidInstances = allInstances
                                .Where(x => g.Key.TypeSource!.TypeDefinition.GetKey(x) == null)
                                .ToList();
                            if (invalidInstances.Count > 0)
                            {
                                logger.LogError("Skipping {Count} instances with null key in collection {Collection}",
                                    invalidInstances.Count, g.Key.TypeSource!.CollectionName);
                                subActivity?.LogError("Skipping {Count} instances with null key in collection {Collection}",
                                    invalidInstances.Count, g.Key.TypeSource!.CollectionName);
                            }
                            var instances =
                                new InstanceCollection(allInstances
                                    .Where(x => g.Key.TypeSource!.TypeDefinition.GetKey(x) != null)
                                    .ToDictionary(g.Key.TypeSource!.TypeDefinition.GetKey))
                                {
                                    GetKey = g.Key.TypeSource!.TypeDefinition.GetKey
                                };
                            var updated = change.Options?.Snapshot == true
                                ? instances
                                : (storeAndUpdates.Store.GetCollection(g.Key.TypeSource.CollectionName) ?? new())
                                .Merge(instances);
                            var updates =
                                storeAndUpdates.Store.ComputeChanges(g.Key.TypeSource.CollectionName, updated)
                                    .ToArray();
                            return new EntityStoreAndUpdates(
                                storeAndUpdates.Store.WithCollection(g.Key.TypeSource.CollectionName, updated),
                                storeAndUpdates.Updates.Concat(updates), change.ChangedBy);

                        }

                        if (g.Key.Op == OperationType.Remove)
                        {
                            var instances = g.Select(i => (i.Instance,
                                Key: g.Key.TypeSource!.TypeDefinition.GetKey(i.Instance))).ToArray();
                            var newStore = storeAndUpdates.Store.Update(g.Key.TypeSource!.CollectionName,
                                c => c.Remove(instances.Select(x => x.Key)));
                            return new EntityStoreAndUpdates(newStore,
                                storeAndUpdates.Updates.Concat(instances.Select(i =>
                                    new EntityUpdate(g.Key.TypeSource!.CollectionName, i.Key, null)
                                    {
                                        OldValue = i.Instance
                                    })), change.ChangedBy ?? stream.StreamId);
                        }

                        throw new NotSupportedException($"Operation {g.Key.Op} not supported");
                    });
            subActivity?.LogInformation("Applying changes to Data Stream {Stream}", stream.StreamIdentity);
            logger.LogDebug("Applying changes to Data Stream {Stream}", stream.StreamIdentity);
            // Complete sub-activity - this would need proper sub-activity tracking to work correctly
            return stream.ApplyChanges(updates);
        }
        catch (Exception ex)
        {
            subActivity?.LogError(ex, "Error updating Data Stream {Stream}: {Message}", stream.StreamIdentity,
                ex.Message);
            logger.LogError(ex, "Error updating Data Stream {Stream}: {Message}", stream.StreamIdentity, ex.Message);
            stream.OnError(ex);
            return null;
        }
        finally
        {
            subActivity?.Complete();
        }
    }

    public static EntityStore Merge(this EntityStore store, EntityStore updated) =>
        store.Merge(updated, UpdateOptions.Default);

    public static EntityStore Merge(this EntityStore store, EntityStore updated,
        Func<UpdateOptions, UpdateOptions> options) =>
        store with
        {
            Collections = store.Collections.SetItems(
                options.Invoke(new()).Snapshot
                    ? updated.Collections
                    : updated.Collections.Select(c => new KeyValuePair<string, InstanceCollection>(
                        c.Key,
                        store.Collections.GetValueOrDefault(c.Key)?.Merge(c.Value) ?? c.Value
                    ))
            )
        };

    public static EntityStore Merge(this EntityStore store, EntityStore updated, UpdateOptions options) =>
        store with
        {
            Collections = store.Collections.SetItems(
                options.Snapshot
                    ? updated.Collections
                    : updated.Collections.Select(c => new KeyValuePair<string, InstanceCollection>(
                        c.Key,
                        store.Collections.GetValueOrDefault(c.Key)?.Merge(c.Value) ?? c.Value
                    ))
            )
        };

    public static EntityStore Update(
        this EntityStore store,
        string collection,
        Func<InstanceCollection, InstanceCollection> update
    ) =>
        store.WithCollection(collection,
            update.Invoke
            (
                store.Collections.GetValueOrDefault(collection)
                ?? new InstanceCollection()
            )
        );

    public static EntityStore Update(this EntityStore store, WorkspaceReference reference, object value) =>
        store.Update(reference, value, x => x);

    public static EntityStore Update(
        this EntityStore store,
        WorkspaceReference reference,
        object value,
        Func<UpdateOptions, UpdateOptions> options
    )
    {
        return reference switch
        {
            EntityReference entityReference
                => store.Update(entityReference.Collection, c => c.Update(entityReference.Id, value)),
            CollectionReference collectionReference
                => store.Update(collectionReference.Name, _ => (InstanceCollection)value),
            CollectionsReference
                => store with { Collections = store.Collections.SetItems(((EntityStore)value).Collections) },
            IPartitionedWorkspaceReference partitioned
                => store.Update(partitioned.Reference, value, options),
            WorkspaceReference<EntityStore>
                => store.Merge((EntityStore)value, options),

            _
                => throw new NotSupportedException(
                    $"reducer type {reference.GetType().FullName} not supported"
                )
        };
    }

    public static IReadOnlyCollection<T> GetData<T>(this EntityStore store)
        => store.GetCollection(store.GetCollectionName?.Invoke(typeof(T)) ?? typeof(T).Name)?.Get<T>().ToArray() ?? [];

    public static T? GetData<T>(this EntityStore store, object id)
        => (T?)store.GetCollection(store.GetCollectionName?.Invoke(typeof(T))
                                   ?? typeof(T).Name)?.Instances.GetValueOrDefault(id)
           ?? default;



    private static (bool IsValid, List<ValidationResult> Results) ValidateUpdate(
        this IWorkspace workspace,
        IReadOnlyCollection<object> instances
    )
    {
        var validationResults = new List<ValidationResult>();
        var isValid = true;
        foreach (var instance in instances)
        {

            var context = new ValidationContext(instance, serviceProvider: workspace.Hub.ServiceProvider, items: null);
            isValid = isValid && Validator.TryValidateObject(instance, context, validationResults);
        }

        return (isValid, validationResults);
    }
    private static (bool IsValid, List<ValidationResult> Results) ValidateCreation(
        this IWorkspace workspace,
        IReadOnlyCollection<object> instances
    )
    {
        //TODO: Validate that instances can be created.
        return workspace.ValidateUpdate(instances);
    }

    public static EntityStoreAndUpdates MergeWithUpdates(this EntityStore store, EntityStore updated, string changedBy,
        UpdateOptions? options = null)
    {
        options ??= UpdateOptions.Default;
        var newStore = store.Merge(updated, options);
        return new EntityStoreAndUpdates(newStore,
            newStore
            .Collections.SelectMany(u =>
                store.ComputeChanges(u.Key, u.Value)), changedBy);
    }

#pragma warning disable IDE0060
    private static (bool IsValid, List<ValidationResult> Results) ValidateDeletion(
        this IWorkspace workspace,
        IReadOnlyCollection<object> instances)
    {
        // TODO V10: Implement proper validation logic. (14.10.2024, Roland Bürgi)
        return (true, new());
    }
#pragma warning restore IDE0060

}

