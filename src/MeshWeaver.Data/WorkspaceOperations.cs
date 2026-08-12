using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Json;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// Workspace mutation primitives: validates and routes <see cref="DataChangeRequest"/>s to the
/// owning data-source streams, plus pure <see cref="EntityStore"/> merge/update/read helpers used
/// by the change pipeline.
/// </summary>
public static class WorkspaceOperations
{
    /// <summary>
    /// Validates the creations, updates and deletions in a change request and, if all are valid,
    /// applies them to the matching data-source streams. Validation failures are reported on the
    /// returned <see cref="ActivityLog"/> and the change is not applied.
    ///
    /// <para>🚨 The write is issued EAGERLY — on call, exactly as before — so a caller that ignores
    /// the result still writes. The returned observable REPORTS the outcome: it emits ONE
    /// <see cref="ActivityLog"/> once every affected data-source stream has applied its part, then
    /// completes (replayed to late subscribers). Subscribe when you need the log — which validations
    /// failed, which stream errored; ignore it for fire-and-run writes.</para>
    ///
    /// <para>This replaces the former <c>Activity</c> parameter. An <c>Activity</c> is a hosted HUB;
    /// creating one per data change spun a hub (plus one per data source) purely to latch completion
    /// and accumulate messages — which is what this observable expresses natively. <c>Activity</c>
    /// stays for INTENT-level work (import, GitSync, compile) where the activity is a persisted node
    /// with live progress.</para>
    /// </summary>
    /// <param name="workspace">The workspace to apply the change to.</param>
    /// <param name="change">The change request to validate and apply.</param>
    /// <returns>A single-emission observable carrying the finished <see cref="ActivityLog"/>.</returns>
    public static IObservable<ActivityLog> Change(this IWorkspace workspace, DataChangeRequest change)
    {
        var logger = workspace.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(WorkspaceOperations));
        logger.LogDebug("Updating workstream for workspace {Address} with {Creations} creations, {Updates} updates, {Deletions} deletions", workspace.Hub.Address, change.Creations.Count(), change.Updates.Count(), change.Deletions.Count());

        var (isValid, messages) = workspace.Validate(change, logger);
        if (!isValid)
            return Observable.Return(Finish(workspace.Hub, messages));

        logger.LogDebug("Update called: Creations={Creations}, Updates={Updates}, Deletions={Deletions}",
            change.Creations.Count(), change.Updates.Count(), change.Deletions.Count());
        return workspace.UpdateStreams(change, messages, logger);
    }

    /// <summary>
    /// Runs the creation / update / deletion validators and folds every failure into log messages.
    /// Validation is pure and synchronous — it never touches a stream.
    /// </summary>
    private static (bool IsValid, ImmutableList<LogMessage> Messages) Validate(
        this IWorkspace workspace, DataChangeRequest change, ILogger logger)
    {
        var messages = ImmutableList<LogMessage>.Empty;
        var isValid = true;

        void Collect((bool IsValid, List<ValidationResult> Results) outcome)
        {
            if (outcome.IsValid)
                return;
            isValid = false;
            foreach (var validationResult in outcome.Results.Where(r => r != ValidationResult.Success))
            {
                var message = $"{string.Join(", ", validationResult.MemberNames)} invalid: {validationResult.ErrorMessage}";
                messages = messages.Add(new LogMessage(message, LogLevel.Error)
                {
                    Scopes =
                    [
                        new("members", validationResult.MemberNames.ToArray()),
                        new("error", validationResult.ErrorMessage!)
                    ]
                });
                logger.LogWarning("Validation error on {Address}: {Message}", workspace.Hub.Address, message);
            }
        }

        if (change.Creations.Any())
            Collect(workspace.ValidateCreation(change.Creations));
        if (change.Updates.Any())
            Collect(workspace.ValidateUpdate(change.Updates));
        if (change.Deletions.Any())
            Collect(workspace.ValidateDeletion(change.Deletions));

        return (isValid, messages);
    }

    private static IObservable<ActivityLog> UpdateStreams(this IWorkspace workspace, DataChangeRequest change,
        ImmutableList<LogMessage> messages, ILogger logger)
    {
        logger.LogDebug("Updating streams for workspace {Address} with {Creations} creations, {Updates} updates, {Deletions} deletions", workspace.Hub.Address, change.Creations.Count(), change.Updates.Count(), change.Deletions.Count());

        var groups = change.Creations.Select(i => ClassifyForRouting(workspace, i, OperationType.Add))
            .Concat(change.Updates.Select(i => ClassifyForRouting(workspace, i, OperationType.Replace)))
            .Concat(change.Deletions.Select(i => ClassifyForRouting(workspace, i, OperationType.Remove)))
            .GroupBy(x => (x.DataSource, x.Partition))
            .ToArray();

        messages = groups.Where(g => g.Key.DataSource is null)
            .Aggregate(messages, (acc, g) => acc.Add(new LogMessage(
                $"Types {string.Join(", ", g.Select(i => i.Instance.GetType().Name).Distinct())} could not be mapped to data source",
                LogLevel.Warning)));

        // ToArray() forces the writes NOW — Change is eager by contract; the observables only
        // report when each stream has applied its part.
        var applied = groups.Where(g => g.Key.DataSource is not null)
            .Select(group => UpdateStream(change, group, logger))
            .ToArray();

        if (applied.Length == 0)
            return Observable.Return(Finish(workspace.Hub, messages));

        return applied.Merge()
            .Aggregate(messages, (acc, streamMessages) => acc.AddRange(streamMessages))
            .Select(all => Finish(workspace.Hub, all));
    }

    /// <summary>
    /// Issues one data-source stream's part of the change and reports the messages it produced.
    /// The <see cref="AsyncSubject{T}"/> replays, so a subscriber that arrives after the write
    /// already committed still gets the log.
    ///
    /// <para>🚨 Completion rides the stream's post-apply seam (<c>applied</c>), NOT the transform
    /// itself: the transform's result is applied by the same turn that runs it, so completing from
    /// inside it would report "committed" to a subscriber (and post the DataChangeResponse) while the
    /// store still holds the pre-change state — ack-on-accept, the shape that raced read-after-write.
    /// The seam runs after the apply, in that same turn, so it costs no extra hub message.</para>
    /// </summary>
    private static IObservable<ImmutableList<LogMessage>> UpdateStream(
        DataChangeRequest change,
        IGrouping<(IDataSource? DataSource, object? Partition), (object Instance, OperationType Op, ITypeSource?
            TypeSource, IDataSource? DataSource, object? Partition)> group,
        ILogger logger)
    {
        var stream = group.Key.DataSource!.GetStreamForPartition(group.Key.Partition);
        if (stream is null)
            throw new DataException($"Data source {group.Key.DataSource.Reference} does not have a stream for partition {group.Key.Partition}");
        if (!stream.Hub.Started.IsCompleted)
            throw new DataException($"Data source {group.Key.DataSource.Reference} for partition {group.Key.Partition} is not initialized.");

        var applied = new AsyncSubject<ImmutableList<LogMessage>>();

        // Synchronous update — the transform is pure in-memory; the stream's
        // handler serializes UpdateStreamRequests, so no retry logic is needed.
        var streamMessages = ImmutableList<LogMessage>.Empty;
        stream.Update(store =>
            {
                var (result, messages) = UpdateDataChangeRequest(store, change, logger, stream, group);
                streamMessages = messages;
                return result;
            },
            ex =>
            {
                // The failure is REPORTED, never rethrown: every invocation of this callback is
                // wrapped by the stream in a log-only try/catch, so a throw here could reach no
                // caller — it only produced a secondary "exceptionCallback threw" ERROR. The log
                // below is what the caller actually sees.
                //
                // A DISPOSED stream is the benign teardown marker the rest of the stream classifies
                // as Debug-only ("stop the source"), so it stays a WARNING — which still commits
                // (DataChangeResponse maps Warning → Committed) and keeps shutdown quiet. Any other
                // failure is a real one and must surface as Failed rather than the silent
                // "Succeeded" the sub-activity used to report.
                logger.LogError(ex, "Update of {Stream} failed", stream.StreamIdentity);
                applied.OnNext([new LogMessage(
                    $"Update of {stream.StreamIdentity} failed: {ex.Message}",
                    ex is ObjectDisposedException ? LogLevel.Warning : LogLevel.Error)]);
                applied.OnCompleted();
            },
            () =>
            {
                applied.OnNext(streamMessages);
                applied.OnCompleted();
            }
        );
        return applied;
    }

    /// <summary>Finishes a data-update log: status is rolled up from the message levels.</summary>
    private static ActivityLog Finish(IMessageHub hub, ImmutableList<LogMessage> messages) =>
        new ActivityLog(ActivityCategory.DataUpdate) { Messages = messages }
            .Finish((int)hub.Version, null);

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

    private static (ChangeItem<EntityStore>? Result, ImmutableList<LogMessage> Messages) UpdateDataChangeRequest(
        EntityStore? store, DataChangeRequest change, ILogger logger, ISynchronizationStream<EntityStore> stream,
        IGrouping<(IDataSource? DataSource, object? Partition), (object Instance, OperationType Op, ITypeSource?
            TypeSource, IDataSource? DataSource, object? Partition)> group)
    {
        // Only what a CALLER needs to act on lands in the log — the play-by-play is Debug on the
        // logger. An empty message list rolls up to Succeeded.
        var messages = ImmutableList<LogMessage>.Empty;
        logger.LogDebug("Starting update of {Stream} with {StreamId}", stream.StreamIdentity, stream.StreamId);
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
                                messages = messages.Add(new LogMessage(
                                    $"Skipping {invalidInstances.Count} instances with null key in collection {g.Key.TypeSource!.CollectionName}",
                                    LogLevel.Error));
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
            logger.LogDebug("Applying changes to Data Stream {Stream}", stream.StreamIdentity);
            return (stream.ApplyChanges(updates), messages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating Data Stream {Stream}: {Message}", stream.StreamIdentity, ex.Message);
            stream.OnError(ex);
            return (null, messages.Add(new LogMessage(
                $"Error updating Data Stream {stream.StreamIdentity}: {ex.Message}", LogLevel.Error)));
        }
    }

    /// <summary>Merges <paramref name="updated"/> into <paramref name="store"/> using default update options.</summary>
    /// <param name="store">The base store.</param>
    /// <param name="updated">The store whose collections are merged in.</param>
    /// <returns>A new store with the collections merged.</returns>
    public static EntityStore Merge(this EntityStore store, EntityStore updated) =>
        store.Merge(updated, UpdateOptions.Default);

    /// <summary>Merges <paramref name="updated"/> into <paramref name="store"/> using options built from a configurator.</summary>
    /// <param name="store">The base store.</param>
    /// <param name="updated">The store whose collections are merged in.</param>
    /// <param name="options">Configures the update options; when snapshot is set, collections are replaced rather than merged.</param>
    /// <returns>A new store with the collections merged (or replaced under snapshot semantics).</returns>
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

    /// <summary>Merges <paramref name="updated"/> into <paramref name="store"/> using the supplied update options.</summary>
    /// <param name="store">The base store.</param>
    /// <param name="updated">The store whose collections are merged in.</param>
    /// <param name="options">The update options; when snapshot is set, collections are replaced rather than merged.</param>
    /// <returns>A new store with the collections merged (or replaced under snapshot semantics).</returns>
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

    /// <summary>Applies an in-place transform to a named collection, creating it if absent.</summary>
    /// <param name="store">The store to update.</param>
    /// <param name="collection">The collection name to transform.</param>
    /// <param name="update">The transform applied to the (possibly empty) collection.</param>
    /// <returns>A new store with the transformed collection.</returns>
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

    /// <summary>Updates the part of the store addressed by a workspace reference with a new value, using default options.</summary>
    /// <param name="store">The store to update.</param>
    /// <param name="reference">The reference (entity, collection, collections or store) identifying what to update.</param>
    /// <param name="value">The new value to write at the referenced location.</param>
    /// <returns>A new store with the referenced location updated.</returns>
    public static EntityStore Update(this EntityStore store, WorkspaceReference reference, object value) =>
        store.Update(reference, value, x => x);

    /// <summary>Updates the part of the store addressed by a workspace reference, dispatching on the reference type.</summary>
    /// <param name="store">The store to update.</param>
    /// <param name="reference">The reference identifying what to update; supported kinds are entity, collection, collections, partitioned and whole-store references.</param>
    /// <param name="value">The new value to write at the referenced location.</param>
    /// <param name="options">Configures the update options (e.g. merge vs. snapshot) for whole-store merges.</param>
    /// <returns>A new store with the referenced location updated.</returns>
    /// <exception cref="NotSupportedException">Thrown for an unsupported reference type.</exception>
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

    /// <summary>Reads all entities of type <typeparamref name="T"/> from the store.</summary>
    /// <typeparam name="T">The entity type to read.</typeparam>
    /// <param name="store">The store to read from.</param>
    /// <returns>The entities of the type, or an empty collection if none.</returns>
    public static IReadOnlyCollection<T> GetData<T>(this EntityStore store)
        => store.GetCollection(store.GetCollectionName?.Invoke(typeof(T)) ?? typeof(T).Name)?.Get<T>().ToArray() ?? [];

    /// <summary>Reads a single entity of type <typeparamref name="T"/> from the store by id.</summary>
    /// <typeparam name="T">The entity type to read.</typeparam>
    /// <param name="store">The store to read from.</param>
    /// <param name="id">The id of the entity.</param>
    /// <returns>The entity, or default if not present.</returns>
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

    /// <summary>
    /// Merges <paramref name="updated"/> into <paramref name="store"/> and computes the resulting
    /// per-collection entity updates, returning both the new store and the change set.
    /// </summary>
    /// <param name="store">The base store.</param>
    /// <param name="updated">The store whose collections are merged in.</param>
    /// <param name="changedBy">Identifier of the writer, recorded on the change set.</param>
    /// <param name="options">Optional update options; defaults to <see cref="UpdateOptions.Default"/>.</param>
    /// <returns>The merged store together with the computed entity updates.</returns>
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

