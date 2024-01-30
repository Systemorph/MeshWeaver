using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenSmc.DataSource.Api;

namespace OpenSmc.Partition
{
    public class PartitionVariable: IPartitionVariable, IResetable // this is done on demand, since it should not be part of api
    {
        private Lazy<IQuerySource> querySource;

        public void InitializeSource(Lazy<IQuerySource> source)
        {
            querySource = source;
        }

        private readonly Dictionary<Type, Dictionary<object, object>> partitionCachePerType = new();
        private readonly Dictionary<string, object> currentPartition = new();

        public async Task SetAsync<TPartition>(object partition, string name = default)
        {
            name ??= typeof(TPartition).Name;
            if (PartitionHelper.IsDefaultValue(partition))
            {
                currentPartition.Remove(name);
                return;
            }

            var key = await GetKeyForInstanceAsync<TPartition>(partition);
            if (key != null)
            {
                if (currentPartition.TryGetValue(name, out var current) && current != null && current.GetType() != key.GetType())
                    throw new PartitionException(string.Format(PartitionErrorMessages.DifferentPartitionKeyTypes, name, current, current.GetType(), key, key.GetType()));
                if (!PartitionHelper.IsDefaultValue(key))
                    currentPartition[name] = key;
                return;
            }

            throw new PartitionException(string.Format(PartitionErrorMessages.PartitionIdIsNotFoundAndNotCreatable, PartitionHelper.PartitionIdProperties.GetInstance(typeof(TPartition)).PropertyType.Name));
        }

        public object GetCurrent(string name = default)
        {
            if (string.IsNullOrEmpty(name))
                //to reduce user efforts, we can return partition with no name specified if only one instance in partitionInner exists
                return currentPartition.Count == 1 ? currentPartition.Single().Value : null;

            currentPartition.TryGetValue(name, out var ret);
            return ret;
        }

        public IEnumerable<(string Name, object Partition)> GetCurrentPartitions()
        {
            return currentPartition.Select(x => (x.Key, x.Value));
        }

        public async Task<object> GetKeyForInstanceAsync<TPartition>(object partition)
        {
            //TPartition will be only of original type
            if (partition == null)
                return null;
            if (PartitionHelper.IsValueTypePartition(typeof(TPartition)))
            {
                if (typeof(TPartition) != partition.GetType())
                    throw new PartitionException(string.Format(PartitionErrorMessages.PartitionTypeMismatch, partition, partition.GetType().Name, typeof(TPartition).Name));
                return partition;
            }

            var partitionIdProperty = PartitionHelper.PartitionIdProperties.GetInstance(typeof(TPartition));
            if (!partitionCachePerType.TryGetValue(typeof(TPartition), out var partitionsPerType))
                partitionsPerType = await LoadPartitions<TPartition>();

            if (PartitionHelper.IsValueTypePartition(partition.GetType()))
            {
                if (partitionIdProperty.PropertyType != partition.GetType())
                    throw new PartitionException(string.Format(PartitionErrorMessages.PartitionIdTypeMismatch, partition, partition.GetType().Name, partitionIdProperty.PropertyType.Name, typeof(TPartition).Name));
                return partitionsPerType.ContainsKey(partition) ? partition : null;
            }

            //IMPORTANT! here we have all partitions stored in data base of internal type, plus those, who was added in this session.
            //new partitions may be of anonymous type, original or internal. 
            //That's why below we get compare func for each instance, it will not harm performance, since functions are cached per type.
            //Also we will not have many partition objects in data base or in workspace. If ou do so, then your models are incorrect!
            var ret = partitionsPerType.FirstOrDefault(x => PartitionHelper.PartitionIdentityPropertyComparers.GetInstance(partition.GetType(), x.Value.GetType(), typeof(TPartition))(partition, x.Value)).Key;
            if (ret != null)
                return ret;

            //trying to get partition key out of partition object
            // in anonymous type, or internal type we need to get property, we dont need to seek for partitionIdAttribute, but rather find partitionIdProperty.Name
            var partitionObjectProperty = partition.GetType().GetProperty(partitionIdProperty.Name);
            if (partitionObjectProperty != null)
            {
                //rare case
                var value = partitionObjectProperty.GetValue(partition);
                if (!PartitionHelper.IsDefaultValue(value))
                {
                    // ReSharper disable once AssignNullToNotNullAttribute
                    if (partitionsPerType.ContainsKey(value))//we asked for partition with same identity properties, so this will be illegal!
                        throw new PartitionException(string.Format(PartitionErrorMessages.AttemptToOverridePartitionIdentity, typeof(TPartition).Name, value));
                    partitionsPerType[value] = partition;
                    return value;
                }
            }

            //trying to create a new key and pass it to UIS to create it there 
            if (partitionIdProperty.PropertyType == typeof(Guid))
            {
                var value = Guid.NewGuid();
                var dictionary = partitionCachePerType[typeof(TPartition)];
                if (!dictionary.ContainsKey(value))
                    dictionary[value] = partition;
                return value;
            }

            return null;
        }

        public async Task<object> GetInstanceByKeyAsync<TPartition>(object partitionKey)
        {
            if (partitionKey == null)
                return null;
            if (PartitionHelper.IsValueTypePartition(typeof(TPartition)))
                return partitionKey;

            if (!partitionCachePerType.TryGetValue(typeof(TPartition), out var ret))
                ret = await LoadPartitions<TPartition>();

            ret.TryGetValue(partitionKey, out var partition);
            return partition;
        }

        public (string Name, Type PartitionType) GetAssociatedPartition(Type type)
        {
            return PartitionHelper.AssociatedPartitionPerType.GetInstance(type);
        }
        
        public void RefreshCaches(Type type , IEnumerable<object> toBeUpdated, IEnumerable<object> toBeDeleted, bool snapshot)
        {
            if (toBeUpdated == null && toBeDeleted == null || !partitionCachePerType.TryGetValue(type, out var dict))
                return;

            if (snapshot)
                dict.Clear();

            var partitionIdSelector = PartitionHelper.PartitionIdSelectors.GetInstance(type);
            if (toBeUpdated != null)
                foreach (var item in toBeUpdated)
                {
                    var partitionId = partitionIdSelector(item);

                    if (PartitionHelper.IsDefaultValue(partitionId))
                        throw new PartitionException(string.Format(PartitionErrorMessages.PartitionIdIsNotFoundAndNotCreatable, PartitionHelper.PartitionIdProperties.GetInstance(type).PropertyType.Name));

                    var matched = dict.FirstOrDefault(x => PartitionHelper.PartitionIdentityPropertyComparers.GetInstance(item.GetType(), x.Value.GetType(), type)(item, x.Value)).Key;
                    if (matched != null && !matched.Equals(partitionId))
                        throw new PartitionException(string.Format(PartitionErrorMessages.AttemptToOverridePartitionKey, type.Name, matched, partitionId));

                    dict.TryAdd(partitionId, item);
                }

            if (toBeDeleted != null)
                foreach (var item in toBeDeleted)
                {
                    var partitionId = partitionIdSelector(item);

                    if (PartitionHelper.IsDefaultValue(partitionId))
                        throw new PartitionException(string.Format(PartitionErrorMessages.PartitionIdIsNotFoundAndNotCreatable, PartitionHelper.PartitionIdProperties.GetInstance(type).PropertyType.Name));

                    dict.Remove(partitionId);
                }
        }

        private async Task<Dictionary<object, object>> LoadPartitions<T>()
        {
            var partitionIdSelector = PartitionHelper.PartitionIdSelectors.GetInstance(typeof(T));
            var queryable = querySource == null ? Enumerable.Empty<T>() : querySource.Value.Query<T>();
            var dictionaryAsync = await queryable.ToAsyncEnumerable().ToDictionaryAsync(x => partitionIdSelector(x), x => (object)x);
            partitionCachePerType[typeof(T)] = dictionaryAsync;
            return dictionaryAsync;
        }

        public void Reset(ResetOptions options = default)
        {
            options ??= new ResetOptions();

            if (options.CurrentPartitionsReset)
                currentPartition.Clear();

            if (!options.TypesToReset.Any())
                partitionCachePerType.Clear();
            else
                foreach (var type in options.TypesToReset)
                    partitionCachePerType.Remove(type);
        }
    }
}
