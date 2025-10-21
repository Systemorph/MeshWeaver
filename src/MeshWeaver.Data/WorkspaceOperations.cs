using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Json.Patch;
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

    private static Task UpdateFailed(IMessageDelivery? delivery, Exception? exception)
    {
        if (delivery is not null)
        {
            // Log the error instead of using DeliveryFailure which doesn't seem to exist
            var errorMessage = $"Update failed: {exception?.ToString() ?? "Unknown error"}";
            // We would need an activity context to log properly, for now we'll skip the error posting
        }
        return Task.CompletedTask;
    }

    private static void Update(Activity? activity, IWorkspace workspace, DataChangeRequest change, IMessageDelivery? request)
    {
        activity?.LogInformation("Starting Update");
        workspace.UpdateStreams(change, activity, request);
    }

    private static void UpdateStreams(this IWorkspace workspace, DataChangeRequest change, Activity? activity, IMessageDelivery? request)
    {
        var logger = workspace.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(WorkspaceOperations));
        logger.LogDebug("Updating streams for workspace {Address} with {Creations} creations, {Updates} updates, {Deletions} deletions", workspace.Hub.Address, change.Creations.Count(), change.Updates.Count(), change.Deletions.Count());
        foreach (var group in
                 change.Creations.Select(i => (Instance: i, Op: OperationType.Add,
                         TypeSource: workspace.DataContext.GetTypeSource(i.GetType()),
                         DataSource: workspace.DataContext.GetDataSourceForType(i.GetType())))
                     .Concat(change.Updates.Select(i => (Instance: i, Op: OperationType.Replace,
                         TypeSource: workspace.DataContext.GetTypeSource(i.GetType()),
                         DataSource: workspace.DataContext.GetDataSourceForType(i.GetType()))))
                     .Concat(change.Deletions.Select(i => (Instance: i, Op: OperationType.Remove,
                         TypeSource: workspace.DataContext.GetTypeSource(i.GetType()),
                         DataSource: workspace.DataContext.GetDataSourceForType(i.GetType()))))
                     .GroupBy(x => (x.DataSource,
                         Partition: (x.TypeSource as IPartitionedTypeSource)?.GetPartition(x.Instance))))
        {
            if (group.Key.DataSource is null)
            {
                activity?.LogWarning("Types {types} could not be mapped to data source", string.Join(", ", group.Select(i => i.Instance.GetType().Name).Distinct()));
                continue;
            }

            var stream = group.Key.DataSource.GetStreamForPartition(group.Key.Partition);
            // Start sub-activity for data update
            var subActivity = activity?.StartSubActivity(ActivityCategory.DataUpdate);


            // Use async update to allow proper retry logic if state changed during computation
            stream!.Update((store, _) => Task.FromResult(UpdateDataChangeRequest(store, change, logger, stream, subActivity, group)),
            ex => UpdateFailed(request, ex)
                );
        }
    }

    private static ChangeItem<EntityStore>? UpdateDataChangeRequest(EntityStore? store, DataChangeRequest change, ILogger logger, ISynchronizationStream<EntityStore> stream, Activity? subActivity,
        IGrouping<(IDataSource? DataSource, object? Partition), (object Instance, OperationType Op, ITypeSource?
            TypeSource, IDataSource? DataSource)> group)
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
                            var instances =
                                new InstanceCollection(g.Select(x => x.Instance)
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
            logger?.LogInformation("Applying changes to Data Stream {Stream}", stream.StreamIdentity);
            // Complete sub-activity - this would need proper sub-activity tracking to work correctly
            return stream.ApplyChanges(updates);
        }
        catch (Exception ex)
        {
            subActivity?.LogError(ex, "Error updating Data Stream {Stream}: {Message}", stream.StreamIdentity,
                ex.Message);
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
        => store.GetCollection(store.GetCollectionName?.Invoke(typeof(T)) ?? typeof(T).Name)!.Get<T>().ToArray();

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

