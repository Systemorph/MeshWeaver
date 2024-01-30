using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using OpenSmc.Scheduling;
using OpenSmc.Collections;
using OpenSmc.DataSource.Api;
using OpenSmc.Partition;
using OpenSmc.Reflection;

namespace OpenSmc.Workspace
{
    public class Workspace : IWorkspace
    {
        private readonly IDataScheduler internalScheduler;
        private readonly IWorkspaceStorage storage;

        private readonly Dictionary<Type, UpdateOptionsBuilder> preCommitAccumulatedUpdateOptions = new();

        public Workspace(IWorkspaceStorage workspaceStorage, IDataScheduler dataScheduler, IPartitionVariable partition)
        {
            storage = workspaceStorage;
            internalScheduler = dataScheduler;
            ((PartitionVariable)partition).InitializeSource(new Lazy<IQuerySource>(this));
            Partition = partition;
        }

        public IPartitionVariable Partition { get;}

        public void Initialize<T>(Func<IAsyncEnumerable<T>> dataFunc)
        {
            storage.Initialize(o => o.FromFunction(dataFunc));
        }

        public void InitializeFrom(IQuerySource querySource)
        {
            storage.Initialize(o=>o.FromSource(querySource));
        }

        public void Initialize(Func<InitializeOptionsBuilder, InitializeOptionsBuilder> options = default)
        {
            storage.Initialize(options);
        }

        private static readonly IGenericMethodCache UpdateEnumerableMethod = GenericCaches.GetMethodCache<IDataSource>(x => x.UpdateAsync(default(IEnumerable<object>), null));

        public async Task UpdateAsync<T>(T instance, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default)
        {
            var enumerableType = typeof(T).GetEnumerableElementType();
            if (enumerableType != null)
                await UpdateEnumerableMethod.MakeGenericMethod(enumerableType).InvokeAsActionAsync(this, instance, options);
            else
                await UpdateAsync(instance.RepeatOnce(), options);
        }

        public async Task UpdateAsync<T>(IEnumerable<T> instances, Func<UpdateOptionsBuilder, UpdateOptionsBuilder> options = default)
        {
            options ??= o => o;

            foreach (var group in instances.Cast<object>().GroupByWithDefaultIfEmpty())
            {
                if (!preCommitAccumulatedUpdateOptions.TryGetValue(group.Key, out var updateOptionsBuilder))
                    updateOptionsBuilder = UpdateOptionsBuilder.Empty;
                preCommitAccumulatedUpdateOptions[group.Key] = options(updateOptionsBuilder);

                //refresh caches of partition if it is needed
                ((PartitionVariable)Partition).RefreshCaches(group.Key, group, null, options(UpdateOptionsBuilder.Empty).GetOptions().SnapshotModeEnabled);
                //put here to collect items for one commit 
                internalScheduler.AddModified(group, Partition, options);
                //put to storage and iterate over base classes
                await storage.Add(group, Partition, options);
            }
        }

        private static readonly IGenericMethodCache DeleteEnumerableMethod = GenericCaches.GetMethodCache<IDataSource>(x => x.DeleteAsync(default(IEnumerable<object>)));

        public async Task DeleteAsync<T>(T instance)
        {
            var enumerableType = typeof(T).GetEnumerableElementType();
            if (enumerableType != null)
                await DeleteEnumerableMethod.MakeGenericMethod(enumerableType).InvokeAsActionAsync(this, instance);
            else
                await DeleteAsync(instance.RepeatOnce());
        }

        public async Task DeleteAsync<T>(IEnumerable<T> instances)
        {
            foreach (var group in instances.Cast<object>().GroupBy(x => x.GetType()))
            {
                //refresh caches of partition if it is needed
                ((PartitionVariable)Partition).RefreshCaches(group.Key, null, group, false);
                //put here to collect items for one commit 
                internalScheduler.AddDeleted(group, Partition);
                //put to storage and iterate over base classes
                await storage.Delete(group, Partition);
            }
        }

        public IQueryable<T> Query<T>()
        {
            return storage.Query<T>(Partition).AsQueryable();
        }

        public Task CommitAsync(Func<CommitOptionsBuilder, CommitOptionsBuilder> options = default)
        {
            return Task.CompletedTask;
        }

        public async Task CommitToTargetAsync(IDataSource target, Func<CommitOptionsBuilder, CommitOptionsBuilder> options = default)
        {
            if (target.Equals(this))
                return;//means that CURRENT Workspace is set as default DataSource and putting same again to itself makes no sense!
            //another workspace can be set as target for current
            
            foreach (var partitionChunk in internalScheduler.GetModified())
            {
                Func<UpdateOptionsBuilder, UpdateOptionsBuilder> updateOptions =
                    preCommitAccumulatedUpdateOptions.TryGetValue(partitionChunk.Type, out var updateOptionsBuilder)
                        ? _ => updateOptionsBuilder
                        : null;

                var partitionType = target.Partition.GetAssociatedPartition(partitionChunk.Type).PartitionType;
                if (partitionType == null)
                {
                    await target.UpdateAsync(partitionChunk.Items, updateOptions);
                }
                else
                {
                    Func<IEnumerable<object>, IDataSource, Task> func = (items, source) => source.UpdateAsync(items, updateOptions);
                    await DataSourceCallAsyncMethod.MakeGenericMethod(partitionType).InvokeAsActionAsync(this, partitionChunk.PartitionId, partitionChunk.Name, partitionChunk.Items, target, func);
                }
                
            }
            foreach (var partitionChunk in internalScheduler.GetDeleted())
            {
                var partitionType = target.Partition.GetAssociatedPartition(partitionChunk.Type).PartitionType;
                if (partitionType == null)
                {
                    await target.DeleteAsync(partitionChunk.Items);
                }
                else
                {
                    Func<IEnumerable<object>, IDataSource, Task> func = (items, source) => source.DeleteAsync(items);
                    await DataSourceCallAsyncMethod.MakeGenericMethod(partitionType).InvokeAsActionAsync(this, partitionChunk.PartitionId, partitionChunk.Name, partitionChunk.Items, target, func);
                }
            }

            await target.CommitAsync(options);
            preCommitAccumulatedUpdateOptions.Clear();

            internalScheduler.Reset();
        }

#pragma warning disable 4014
        private static readonly MethodInfo DataSourceCallAsyncMethod = ReflectionHelper.GetMethodGeneric<Workspace>(x => x.DataSourceCallAsync<object>(null, null, null, null, null));
#pragma warning restore 4014
        // ReSharper disable once UnusedMethodReturnValue.Local
        private async Task DataSourceCallAsync<TPartition>(object partitionId, string name, IEnumerable<object> items, IDataSource dataSource, Func<IEnumerable<object>, IDataSource, Task> func)
        {
            //since user will expect to be same partitions after commit we return them back
            var value = name == null ? null : dataSource.Partition.GetCurrent(name);
            var partitionObject = await Partition.GetInstanceByKeyAsync<TPartition>(partitionId);
            await dataSource.Partition.SetAsync<TPartition>(partitionObject, name);
            await func(items, dataSource);
            await dataSource.Partition.SetAsync<TPartition>(value, name);
        }

        public void Reset(Func<ResetOptionsBuilder, ResetOptionsBuilder> options = default)
        {
            options ??= o => o;
            var resetOptions = options(ResetOptionsBuilder.Empty).GetOptions();

            if (resetOptions.TypesToReset.IsEmpty)
                preCommitAccumulatedUpdateOptions.Clear();
            else
            {
                foreach (var type in resetOptions.TypesToReset)
                    preCommitAccumulatedUpdateOptions.Remove(type);
            }

            storage.Reset(resetOptions);
            internalScheduler.Reset(resetOptions);
            ((IResetable)Partition).Reset(resetOptions);
        }
    }
}
