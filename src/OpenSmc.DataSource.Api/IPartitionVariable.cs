using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenSmc.DataSource.Api
{
    public interface IPartitionVariable
    {
        /// <summary>
        /// Sets partition as current
        /// </summary>
        /// <typeparam name="TPartition"></typeparam>
        /// <param name="partition">Value of partition, can be either key or anonymous type with all identity properties
        /// or instance of exact partition type without partition key or partition instance with key</param>
        /// <param name="name">Name of given partition.
        /// If null passed then type name will be used as value</param>
        /// <returns></returns>
        Task SetAsync<TPartition>(object partition, string name = default);
        /// <summary>
        /// Returns back Id of current partition.
        /// </summary>
        /// <param name="name">Name of given partition.
        /// If null passed and if there is only one partition used, it will give this single current partition key, otherwise will return null.</param>
        /// <returns></returns>
        object GetCurrent(string name = default);

        /// <summary>
        /// Returns back all partitions and its values which currently set
        /// </summary>
        IEnumerable<(string Name, object Partition)> GetCurrentPartitions();
        /// <summary>
        /// Returns partition key for given partition instance from caches
        /// </summary>
        /// <typeparam name="TPartition"></typeparam>
        /// <param name="partition">object from which partition identity properties are taken to match to instances in cache</param>
        /// <returns></returns>
        public Task<object> GetKeyForInstanceAsync<TPartition>(object partition);
        //can return any object - anonymous, original or internal
        /// <summary>
        /// Returns partition instance by given key from caches
        /// </summary>
        /// <param name="partitionKey"></param>
        /// <returns></returns>
        Task<object> GetInstanceByKeyAsync<TPartition>(object partitionKey);
        /// <summary>
        /// Gets partition info related to given type
        /// </summary>
        /// <param name="type">Type of partitionedType</param>
        /// <returns></returns>
        (string Name, Type PartitionType) GetAssociatedPartition(Type type);
    }
}
