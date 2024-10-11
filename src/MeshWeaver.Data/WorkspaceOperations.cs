using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Activities;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data
{
    public static class WorkspaceOperations
    {
        public static Activity Change(this IWorkspace workspace, DataChangedRequest dataChange)
        {
            if (dataChange.Elements == null)
                throw new ArgumentException($"No elements provided in the request");

            if (dataChange is UpdateDataRequest update)
                return workspace.MergeUpdate(update);

            if (dataChange is DeleteDataRequest delete)
                return workspace.MergeDelete(delete);
            throw new InvalidOperationException(
                $"No implementation for update request of type {dataChange.GetType().FullName}"
                );
        }

        public static Activity MergeUpdate(
            this IWorkspace workspace,
            UpdateDataRequest update
        )
        {
            var hub = workspace.Hub;
            var activity = GetActivity(hub);
            var (isValid, results) = workspace.Validate(update.Elements);
            if (!isValid)
            {
                foreach (var validationResult in results.Where(r => r != ValidationResult.Success))
                    activity.LogError("{members} invalid: {error}", validationResult.MemberNames, validationResult.ErrorMessage);
                activity.ChangeStatus(ActivityStatus.Failed);
                return activity;
            }

            foreach (var e in update
                         .Elements.GroupBy(e => e.GetType())
                         .SelectMany(e =>
                             workspace.DataContext.MapToIdAndAddress(e, e.Key))
                         .GroupBy(e => e.Stream)
                    )
            {

                var activityPart = activity.StartSubActivity(ActivityCategory.DataUpdate);
                e.Key.Update(s =>
                {
                    try
                    {
                        var entityStore = new EntityStore(
                            e.Select(y => new KeyValuePair<string, InstanceCollection>(
                                    y.Collection,
                                    new(y.Elements)
                                ))
                                .ToImmutableDictionary()
                        ) { GetCollectionName = workspace.DataContext.GetCollectionName };
                        var storesAndUpdates = s.MergeWithUpdates(
                            entityStore,
                            update.Options ?? UpdateOptions.Default);
                        var ret = e.Key.ApplyChanges(storesAndUpdates);
                        activityPart.Complete();

                        return ret;
                    }
                    catch (Exception ex)
                    {
                        activityPart.LogError(ex.Message);
                        activityPart.Complete();
                        return null;
                    }


                });
            }

            activity.CompleteOnSubActivities();

            return activity;

        }

        private static Activity GetActivity(IMessageHub hub) => 
            new(ActivityCategory.DataUpdate, hub);

        private static IEnumerable<(ISynchronizationStream<EntityStore> Stream, object Reference, ImmutableDictionary<object, object> Elements, string Collection, ITypeSource TypeSource)> MapToIdAndAddress(this DataContext dataContext, IEnumerable<object> e, Type type)
        {
            if (
                !dataContext.DataSourcesByType.TryGetValue(type, out var dataSource)
                || !dataSource.TypeSources.TryGetValue(type, out var ts)
            )
                throw new InvalidOperationException(
                    $"Type {type.FullName} is not mapped to data source."
                );

            if (ts is not IPartitionedTypeSource partitioned)
                yield return (
                    dataSource.Streams.GetValueOrDefault(new StreamReference(dataSource.Id, null)),
                    dataSource.Reference,
                    e.ToImmutableDictionary(x => ts.TypeDefinition.GetKey(x)),
                    dataContext.GetCollectionName(type),
                    ts
                );
            else
                foreach (var partition in e.GroupBy(x => partitioned.GetPartition(x)))
                {
                    yield return (
                        dataSource.Streams.GetValueOrDefault(new(dataSource.Id,partition.Key)),
                        new PartitionedCollectionsReference(partition.Key, dataSource.Reference),
                        partition.ToImmutableDictionary(x => ts.TypeDefinition.GetKey(x)),
                        dataContext.GetCollectionName(type),
                        ts
                    );

                }
        }

        private static Activity MergeDelete(
            this IWorkspace workspace,
            DeleteDataRequest deletion
        )
        {
            
            var activity = GetActivity(workspace.Hub);

            // TODO V10: Add delete validation here (10.10.2024, Roland Bürgi)

            deletion
                .Elements.GroupBy(e => e.GetType())
                .SelectMany(e => workspace.DataContext.MapToIdAndAddress(e, e.Key))
                .GroupBy(e => e.Stream)
                .ForEach(e =>
                {
                    var activityPart = activity.StartSubActivity(ActivityCategory.DataUpdate);
                    e.Key.Update(s =>
                    {
                        try
                        {
                            var entityStoreAndUpdates = s.DeleteWithUpdates(
                                new EntityStore(
                                    e.Select(y => new KeyValuePair<string, InstanceCollection>(
                                            y.Collection,
                                            new(y.Elements)
                                        ))
                                        .ToImmutableDictionary()
                                ) { GetCollectionName = workspace.DataContext.GetCollectionName });
                            var ret = e.Key.ApplyChanges(
                                entityStoreAndUpdates
                            );
                            activityPart.Complete();
                            activity.Complete();
                            return ret;
                        }
                        catch (Exception ex)
                        {
                            activityPart.LogError(ex.Message);
                            activityPart.Complete();
                            return null;
                        }
                    });
                });
            return activity;
        }


        private static (bool IsValid, List<ValidationResult> Results) Validate(
            this IWorkspace workspace,
            IReadOnlyCollection<object> instances
        )
        {
            var validationResults = new List<ValidationResult>();
            var isValid = true;
            foreach (var instance in instances)
            {

                var context = new ValidationContext(instance);
                isValid = isValid && Validator.TryValidateObject(instance, context, validationResults);
            }
            return (isValid, validationResults);
        }


    }
}
