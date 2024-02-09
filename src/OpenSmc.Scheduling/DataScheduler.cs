using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using OpenSmc.Collections;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.Domain.Abstractions.Attributes;
using OpenSmc.Equality;
using OpenSmc.Partition;
using OpenSmc.Reflection;

namespace OpenSmc.Scheduling
{
    public class DataScheduler : IDataScheduler
    {
        //Type is entity type, inner dictionary is dictionary of Dictionary<(string partitionName ,TPartition partition), Dictionary<T,T>>(new PartitionComparer<TPartition>())
        //where T same as type and Dictionary<T,T> has IdentityEqualityComparer<T, TEntity> in it
        private readonly ConcurrentDictionary<Type, Dictionary<(string,object), IDictionary>> savesByPartition = new();
        private readonly ConcurrentDictionary<Type, Dictionary<(string, object), IDictionary>> deletesByPartition = new();

        public IEnumerable<PartitionChunk> GetModified()
        {
            foreach (var savesByPartitionItem in savesByPartition)
                foreach ((string, object) partitionKey in savesByPartitionItem.Value.Keys)
                    yield return new PartitionChunk(partitionKey.Item1,
                                                    partitionKey.Item2,
                                                    (IEnumerable<object>)savesByPartitionItem.Value[partitionKey].Values,
                                                    savesByPartitionItem.Key);
        }

        public IEnumerable<PartitionChunk> GetDeleted()
        {
            foreach (var deletesByPartitionItem in deletesByPartition)
                foreach ((string, object) partitionKey in deletesByPartitionItem.Value.Keys)
                    yield return new PartitionChunk(partitionKey.Item1,
                                                    partitionKey.Item2,
                                                    (IEnumerable<object>)deletesByPartitionItem.Value[partitionKey].Values,
                                                    deletesByPartitionItem.Key);
        }

        public void Reset(ResetOptions options = default)
        {
            var typesToReset = (options ?? new ResetOptions()).TypesToReset;
            var typesToDelete = deletesByPartition.Keys;
            var typesToSave = savesByPartition.Keys;
            if (typesToReset.Any())
            {
                //this code finds inheritors and resets them also
                var typesResetFromDelete = typesToReset.SelectMany(x=>typesToDelete.Where(x.IsAssignableFrom));
                foreach (var key in typesResetFromDelete)
                    deletesByPartition.TryRemove(key, out _);

                //this code finds inheritors and resets them also
                var typesResetFromSave = typesToReset.SelectMany(x => typesToSave.Where(x.IsAssignableFrom));
                foreach (var key in typesResetFromSave)
                    savesByPartition.TryRemove(key, out _);

                return;
            }

            deletesByPartition.Clear();
            savesByPartition.Clear();
        }

        //aggregation is savesByPartition and exclusion is deletesByPartition 
        public void AddModified(IGrouping<Type, object> group, IPartitionVariable partitionVariable, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default) =>
            ModifyInner(savesByPartition, deletesByPartition, group, partitionVariable, options != null && options(UpdateOptionsBuilder.Empty).GetOptions().SnapshotModeEnabled);

        public void AddModified(IEnumerable<object> entities, IPartitionVariable partitionVariable, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default) => 
            Modify((objects, variable) => AddModified(objects, variable, options), entities, partitionVariable);

        //aggregation is deletesByPartition and exclusion is savesByPartition
        public void AddDeleted(IGrouping<Type, object> group, IPartitionVariable partitionVariable) =>
            ModifyInner(deletesByPartition, savesByPartition, group, partitionVariable, false);

        public void AddDeleted(IEnumerable<object> entities, IPartitionVariable partitionVariable) => 
            Modify((objects, variable) => AddDeleted(objects, variable), entities, partitionVariable);

        private void Modify(Action<IGrouping<Type, object>, IPartitionVariable> modificationFunc, IEnumerable<object> entities, IPartitionVariable partitionVariable)
        {
            foreach (var group in entities.GroupByWithDefaultIfEmpty())
                modificationFunc(group, partitionVariable);
        }

        private void ModifyInner(ConcurrentDictionary<Type, Dictionary<(string, object), IDictionary>> aggregationDictionary,
                                 ConcurrentDictionary<Type, Dictionary<(string, object), IDictionary>> exclusionDictionary,
                                 IGrouping<Type, object> group, IPartitionVariable partitionVariable, bool snapshotMode)
        {
            var partitionName = partitionVariable?.GetAssociatedPartition(group.Key).Name;
            var partitionKey = partitionName == null ? null : partitionVariable.GetCurrent(partitionName);

            foreach (var byPartition in group.GroupBy(x => PartitionHelper.GetPartitionKeyByFromProperty(x, group.Key) ?? partitionKey))
            {
                var aggregation = GetPartitionedDictionary(aggregationDictionary, group.Key, partitionName, byPartition.Key);
                IDictionary exclusion = null;
                //if no exclusionDictionary was present => not create it
                if (exclusionDictionary.TryGetValue(group.Key, out var perType) && perType.ContainsKey((partitionName, byPartition.Key)))
                    exclusion = GetPartitionedDictionary(exclusionDictionary, group.Key, partitionName, byPartition.Key);

                if (snapshotMode)
                    foreach (var key in aggregationDictionary.Keys.Where(group.Key.IsAssignableFrom))
                        GetPartitionedDictionary(aggregationDictionary, key, partitionName, byPartition.Key).Clear();

                foreach (var instance in byPartition.Where(x => x != null))
                {
                    aggregation[instance] = instance;
                    exclusion?.Remove(instance);
                }
            }
        }

        private IDictionary GetPartitionedDictionary(ConcurrentDictionary<Type, Dictionary<(string, object), IDictionary>> outerDictionary,
                                                     Type type,
                                                     string partitionName,
                                                     object partitionKey)
        {
            if (partitionName != null && PartitionHelper.IsDefaultValue(partitionKey))
                throw new PartitionException(PartitionErrorMessages.PartitionMustBeSet);
            var instancesByPartitions = outerDictionary.GetOrAdd(type, _ => new Dictionary<(string, object), IDictionary>());
            return instancesByPartitions.GetOrAdd((partitionName, partitionKey), _ => (IDictionary)CreateInnerDictionaryMethod
                                                                                                                 .MakeGenericMethod(type, type.GetSingleCustomAttribute<TargetTypeAttribute>()?.Type ?? type)
                                                                                                                 .InvokeAsFunction(this));
        }

        private static readonly MethodInfo CreateInnerDictionaryMethod = ReflectionHelper.GetMethodGeneric<DataScheduler>(x => x.CreateInnerDictionary<object, object>());
        // ReSharper disable once UnusedMethodReturnValue.Local
        private IDictionary CreateInnerDictionary<T, TEntity>()
        {
            return new Dictionary<T, T>(IdentityEqualityComparer<T, TEntity>.Instance);
        }
    }
}