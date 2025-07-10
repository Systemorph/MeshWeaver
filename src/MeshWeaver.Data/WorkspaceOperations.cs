using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Json.Patch;
using MeshWeaver.Activities;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public static class WorkspaceOperations
{
    public static void Change(this IWorkspace workspace, DataChangeRequest change, Activity activity, IMessageDelivery? request)
    {
        if (change.Creations.Any())
        {
            var (isValid, results) = workspace.ValidateCreation(change.Creations);
            if (!isValid)
            {
                foreach (var validationResult in results.Where(r => r != ValidationResult.Success))
                    activity.LogError("{members} invalid: {error}", validationResult.MemberNames,
                        validationResult.ErrorMessage);
            }

        }

        if (change.Updates.Any())
        {
            var (isValid, results) = workspace.ValidateUpdate(change.Updates);
            if (!isValid)
            {
                foreach (var validationResult in results.Where(r => r != ValidationResult.Success))
                    activity.LogError("{members} invalid: {error}", validationResult.MemberNames,
                        validationResult.ErrorMessage);
            }

        }

        if (change.Deletions.Any())
        {
            var (isValid, results) = workspace.ValidateDeletion(change.Updates);
            if (!isValid)
            {
                foreach (var validationResult in results.Where(r => r != ValidationResult.Success))
                    activity.LogError("{members} invalid: {error}", validationResult.MemberNames,
                        validationResult.ErrorMessage);
            }
        }


        activity.Update(a => a.HasErrors() ? a with { Log = a.Log } : StartUpdate(a, workspace, change, request),
            ex => UpdateFailed(workspace, request, ex)
            );
    }

    private static Task UpdateFailed(IWorkspace workspace, IMessageDelivery? delivery, Exception? exception)
    {
        if (delivery is not null)
            workspace.Hub.Post(new DeliveryFailure(delivery, exception?.ToString()), o => o.ResponseFor(delivery));
        return Task.CompletedTask;
    }

    private static Activity StartUpdate(Activity activity, IWorkspace workspace, DataChangeRequest change, IMessageDelivery? request)
    {
        activity.LogInformation("Starting Update");
        workspace.UpdateStreams(change, activity, request);
        return activity;
    }


    private static void UpdateStreams(this IWorkspace workspace, DataChangeRequest change, Activity activity, IMessageDelivery? request)
    {
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
                activity.LogWarning("Types {types} could not be mapped to data source", string.Join(", ", group.Select(i => i.Instance.GetType().Name).Distinct()));
                continue;
            }

            var stream = group.Key.DataSource.GetStreamForPartition(group.Key.Partition);
            var activityPart = activity.StartSubActivity(ActivityCategory.DataUpdate);
            stream!.Update(store =>
            {
                activityPart.LogInformation("Updating Data Stream {identity}", stream.StreamIdentity);
                try
                {

                    var updates = group.GroupBy(x => (Op: (x.Op == OperationType.Add ? OperationType.Replace : x.Op), x.TypeSource))
                        .Aggregate(new EntityStoreAndUpdates(store!, [], change.ChangedBy),
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
                                        : storeAndUpdates.Store.GetCollection(g.Key.TypeSource.CollectionName)
                                            ?.Merge(instances) ?? instances;
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
                    activityPart.LogInformation("Update of Data Stream {identity} succeeded.", stream.StreamIdentity);
                    activityPart.Complete();
                    return stream.ApplyChanges(updates);
                }
                catch (Exception ex)
                {
                    activityPart.LogError("Error updating Stream {identity}: {exception}", stream.StreamIdentity,
                        ex.Message);
                    activityPart.Complete();
                    return null;
                }

            },
            ex => UpdateFailed(workspace, request, ex)
                );
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

