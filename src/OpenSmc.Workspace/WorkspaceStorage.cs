using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using OpenSmc.Collections;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.Domain.Abstractions.Attributes;
using OpenSmc.Equality;
using OpenSmc.Partition;
using OpenSmc.Reflection;

// ReSharper disable UnusedMethodReturnValue.Local

namespace OpenSmc.Workspace;

public class WorkspaceStorage : IWorkspaceStorage
{
    private readonly Dictionary<Type, Dictionary<(string, object), IHashSetWrapper>> partitionedInstancesByType = new();
    private InitializeOptionsBuilder initializeOptions = InitializeOptionsBuilder.Empty;

    public Task Add(IEnumerable<object> items, IPartitionVariable partitionVariable, Func<UpdateOptions, UpdateOptions> options = default)
        => ProcessItems(items, partitionVariable, (group, pv) => Add(group, pv, options));
    public Task Add(IGrouping<Type, object> group, IPartitionVariable partitionVariable, Func<UpdateOptions, UpdateOptions> options = default) 
        => ProcessItemsByGroup(group, partitionVariable, AddThroughPartitionsMethod, options != null && options(UpdateOptions.Empty).GetOptions().SnapshotModeEnabled);
    
    public Task Delete(IEnumerable<object> items, IPartitionVariable partitionVariable)
        => ProcessItems(items, partitionVariable, Delete);
    public Task Delete(IGrouping<Type, object> group, IPartitionVariable partitionVariable) 
        => ProcessItemsByGroup(group, partitionVariable, RemoveThroughPartitionsMethod, false);

    private async Task ProcessItems(IEnumerable<object> items, IPartitionVariable partitionVariable, Func<IGrouping<Type, object>, IPartitionVariable, Task> func)
    {
        foreach (var group in items.GroupByWithDefaultIfEmpty())
            await func(group, partitionVariable);
    }

    private async Task ProcessItemsByGroup(IGrouping<Type, object> group, IPartitionVariable partitionVariable, MethodInfo processMethodInfo, bool snapshot)
    {
        foreach (var type in GetBaseTypes(group.Key))
        {
            var targetType = type.GetSingleCustomAttribute<TargetTypeAttribute>()?.Type ?? type;
            await processMethodInfo.MakeGenericMethod(type, targetType).InvokeAsActionAsync(this, partitionVariable, group, snapshot && type == group.Key); //snapshot only original type
        }
    }

    private static readonly MethodInfo AddThroughPartitionsMethod = ReflectionHelper.GetMethodGeneric<WorkspaceStorage>(x => x.AddThroughPartitions<object, object>(null, null, false));
    private static readonly MethodInfo RemoveThroughPartitionsMethod = ReflectionHelper.GetMethodGeneric<WorkspaceStorage>(x => x.RemoveThroughPartitions<object, object>(null, null, false));

    private Task AddThroughPartitions<T, TEntity>(IPartitionVariable partitionVariable, IEnumerable<object> items, bool snapshot) 
        => ProcessThroughPartitions<T, TEntity>(partitionVariable, items, snapshot, (bucket, localItems) => bucket.AddRange(localItems));

    private Task RemoveThroughPartitions<T, TEntity>(IPartitionVariable partitionVariable, IEnumerable<object> items, bool snapshot) 
        => ProcessThroughPartitions<T, TEntity>(partitionVariable, items, snapshot, (bucket, localItems) => bucket.Remove(localItems));

    private async Task ProcessThroughPartitions<T, TEntity>(IPartitionVariable partitionVariable, IEnumerable<object> items, bool snapshot, Action<IHashSetWrapper<T>, IEnumerable<T>> processAction)
    {
        var partitionName = partitionVariable.GetAssociatedPartition(typeof(T)).Name;
        var partition = partitionName == null ? null : partitionVariable.GetCurrent(partitionName);

        //with this we select correct partition from instance.PropertyKey property
        foreach (var byPartition in items.GroupBy(x => PartitionHelper.GetPartitionKeyByFromProperty(x, typeof(T)) ?? partition))
        {
            var listByType = await GetCollectionAsync<T, TEntity>(partitionName, byPartition.Key, partitionVariable, snapshot);
            processAction(listByType, byPartition.OfType<T>());//this will kick out nulls and cast to T
        }
    }

    public IQueryable<T> Query<T>(IPartitionVariable partitionVariable)
    {
        var partitionName = partitionVariable.GetAssociatedPartition(typeof(T)).Name;
        var partition = partitionName == null ? null : partitionVariable.GetCurrent(partitionName);

        if (partitionedInstancesByType.TryGetValue(typeof(T), out var partDict))
        {
            //we return what we have collected, without getting other partitions from init querySource
            if (partitionName != null && PartitionHelper.IsDefaultValue(partition))
                return partDict.Values.SelectMany(x => x as IEnumerable<T>).AsQueryable();

            if (partDict.TryGetValue((partitionName, partition), out var ret) && ret is IEnumerable<T> enumerable)
                return enumerable.AsQueryable();
        }
        
        var options = initializeOptions.GetOptions();

        //nothing found, but init disabled
        if (options.DisabledInitialization.Contains(typeof(T)))
            return Enumerable.Empty<T>().AsQueryable();

        if (options.InitFunctions.TryGetValue(typeof(T), out var initFunc))
        {
            var asyncEnumerable = ((Func<IAsyncEnumerable<T>>)initFunc)();
            if (!string.IsNullOrEmpty(partitionName) && !PartitionHelper.IsDefaultValue(partition))
                asyncEnumerable = asyncEnumerable.WithPartitionKey(partition);

            Expression<Func<IEnumerable<T>>> expression = () => GetQueryableFromInit(asyncEnumerable);
            return new EnumerableQuery<T>(expression.Body);
        }

        if (options.QuerySource != null)
            return GetQuery<T>(partitionName, partition, options.QuerySource, partitionVariable).Result;

        //nothing found
        return Enumerable.Empty<T>().AsQueryable();
    }

    public void Initialize(Func<InitializeOptionsBuilder, InitializeOptionsBuilder> options = default)
    {
        if (options != null)
            initializeOptions = options(initializeOptions);
    }

    private static IQueryable<T> GetQueryableFromInit<T>(IAsyncEnumerable<T> initCollection)
    {
        var listAsync = initCollection.ToListAsync().Result;
        return listAsync.AsQueryable();
    }

    public void Reset(ResetOptions resetOptions = default)
    {
        resetOptions ??= new ResetOptions();

        if (resetOptions.InitializationRulesReset)
            initializeOptions = InitializeOptionsBuilder.Empty;

        if (!resetOptions.TypesToReset.Any())
            partitionedInstancesByType.Clear();
        else
        {
            //clean base types
            ClearBaseTypes(resetOptions);

            //also clears inheritors
            var typesReset = resetOptions.TypesToReset.SelectMany(x => partitionedInstancesByType.Keys.Where(x.IsAssignableFrom));
            foreach (var type in typesReset)
                partitionedInstancesByType.Remove(type);
        }
    }

    private void ClearBaseTypes(ResetOptions resetOptions)
    {
        foreach (var currentType in resetOptions.TypesToReset)
        {
            //foreach items we have to clear
            if (partitionedInstancesByType.TryGetValue(currentType, out var currentDictionary))
            {
                //we find base types for currentType, excluding current type from this loop
                ClearBaseTypesInner(GetBaseTypes(currentType, false), currentDictionary);
            }
        }
    }

    private void ClearBaseTypesInner(IEnumerable<Type> baseTypes, ICollection<KeyValuePair<(string, object), IHashSetWrapper>> currentDictionary)
    {
        foreach (var baseType in baseTypes)
        {
            //which contains instances from currentType
            if (partitionedInstancesByType.TryGetValue(baseType, out var baseDictionary))
            {
                //we check partition for base type, since base type can contain no partition
                var partitionName = PartitionHelper.AssociatedPartitionPerType.GetInstance(baseType).Name;
                //each chunk per partition which we have in currentDictionary we remove from baseDictionary
                foreach (var perPartition in currentDictionary)
                {
                    //since base type can not contain second partitionKey(due to restrictions of partitionKeyAttribute),
                    //then partition value must be same
                    //but partition name can be different, so we recheck it
                    var key = string.IsNullOrEmpty(partitionName)
                                  ? (null, null)
                                  : (partitionName, perPartition.Key.Item2);
                    if (baseDictionary.TryGetValue(key, out var hashSetWrapper))
                        RemoveFromHashSetWrapperMethod.MakeGenericMethod(baseType).InvokeAsAction(this, perPartition.Value, hashSetWrapper);
                }
            }
        }
    }

    private static readonly MethodInfo RemoveFromHashSetWrapperMethod = ReflectionHelper.GetMethodGeneric<WorkspaceStorage>(x => x.RemoveFromHashSetWrapper<object>(null, null));

    private void RemoveFromHashSetWrapper<T>(IHashSetWrapper sourceHashSetWrapper, HashSetWrapper<T> targetHashSetWrapper)
    {
        var items = sourceHashSetWrapper as IEnumerable<T>;
        targetHashSetWrapper.Remove(items);
    }

    private static IEnumerable<Type> GetBaseTypes(Type type, bool includeSelf = true)
        => type.GetBaseTypes(includeSelf).Where(x => x != typeof(object) && !x.IsAbstract);

    private async Task<IHashSetWrapper<T>> GetCollectionAsync<T, TEntity>(string partitionName, object partitionKey, IPartitionVariable partitionVariable, bool snapshot)
    {
        if (!partitionedInstancesByType.TryGetValue(typeof(T), out var partDict))
            //in case if we have initializationFunctions => instances from AsyncEnumerable might be located in any partition, so we execute this function
            //once and on each partition change we don't reevaluate this
            partitionedInstancesByType[typeof(T)] = partDict = await InitializeTypeDictionaryAsync<T, TEntity>(partitionVariable, snapshot);

        //if no data was found and no initFunction for this type is defined => we call load from specified source, if it is set
        return await GetOrAddCollectionAsync(partDict, partitionName, partitionKey, snapshot, async x => await CreateCollectionAsync<T, TEntity>(partitionName, partitionKey, x, partitionVariable));
    }

    private async Task<IHashSetWrapper<T>> GetOrAddCollectionAsync<T>(Dictionary<(string, object), IHashSetWrapper> partDict, string partitionName, object partitionKey, bool snapshot, Func<bool, Task<IHashSetWrapper<T>>> asyncFactory)
    {
        var partition = (partitionName, partitionKey);
        if (!partDict.TryGetValue(partition, out var ret))
        {
            //we can not create dictionary for null partition
            if (partitionName != null && PartitionHelper.IsDefaultValue(partitionKey))
                throw new PartitionException(PartitionErrorMessages.PartitionMustBeSet);
            partDict[partition] = ret = await asyncFactory(snapshot);
        }

        if (snapshot)
        {
            //clean base types
            //since current dictionary also will be cleaned by this call, this means it should be cleaned at last
            //to not clean it at beginning and then clean zero items from each base type
            var baseTypes = GetBaseTypes(typeof(T)).Reverse();
            ClearBaseTypesInner(baseTypes, new[] { new KeyValuePair<(string, object), IHashSetWrapper>(partition, ret) });

            //also clears inheritors
            var inheritors = partitionedInstancesByType.Keys.Where(typeof(T).IsAssignableFrom).Except(typeof(T).RepeatOnce());
            foreach (var inheritor in inheritors)
                if (partitionedInstancesByType.TryGetValue(inheritor, out var baseDictionary))
                    baseDictionary.Remove(partition);
        }
       

        return (IHashSetWrapper<T>)ret;
    }

    private async Task<IHashSetWrapper<T>> CreateCollectionAsync<T, TEntity>(string partitionName, object partitionKey, bool snapshot, IPartitionVariable partitionVariable)
    {
        var options = GetInitOptions<T>(snapshot);

        var hashSetWrapper = new HashSetWrapper<T>(IdentityEqualityComparer<T, TEntity>.Instance);
        if (options.QuerySource != null && !options.InitFunctions.ContainsKey(typeof(T)) && !options.DisabledInitialization.Contains(typeof(T)))
        {
            var query = await GetQuery<T>(partitionName, partitionKey, options.QuerySource, partitionVariable);

            var items = await query.ToArrayAsync();
            hashSetWrapper.AddRange(items);
        }

        return hashSetWrapper;
    }

    private InitializeOptions GetInitOptions<T>(bool snapshot)
    {
        //here we disable initialization for given type just for this call, that other calls
        //(for example another partition for this type, which is not involved in this operation) does not suffer
        var options = snapshot ? initializeOptions.DisableInitialization<T>().GetOptions() : initializeOptions.GetOptions();
        return options;
    }

    private async Task<IQueryable<T>> GetQuery<T>(string partitionName, object partitionKey, IQuerySource source, IPartitionVariable partitionVariable)
    {
        if (partitionName == null)
            return source.Query<T>();

        partitionKey = PartitionHelper.IsDefaultValue(partitionKey) ? null : partitionKey;
            
        if (source is not IQuerySourceWithPartition sourceWithPartition)
            return partitionKey == null ? source.Query<T>() : source.Query<T>().WithPartitionKey(partitionKey);

        var partitionType = partitionVariable.GetAssociatedPartition(typeof(T)).PartitionType;
        return (IQueryable<T>)await GetPartitionedQueryMethod.MakeGenericMethod(typeof(T), partitionType)
                                                             .InvokeAsFunctionAsync(partitionName, partitionKey, sourceWithPartition, partitionVariable);
    }

#pragma warning disable 4014
    private static readonly MethodInfo GetPartitionedQueryMethod = ReflectionHelper.GetStaticMethodGeneric(() => GetPartitionedQuery<object, object>(null, null, null, null));
#pragma warning restore 4014
    private static async Task<IQueryable<T>> GetPartitionedQuery<T, TPartition>(string partitionName, object partitionKey, IQuerySourceWithPartition source, IPartitionVariable partitionVariable)
    {
        var value = source.Partition.GetCurrent(partitionName);

        var partitionObject = await partitionVariable.GetInstanceByKeyAsync<TPartition>(partitionKey);

        if (partitionKey != null)
        {
            if (partitionObject == null)
                throw new PartitionException(string.Format(PartitionErrorMessages.PartitionIdIsNotFoundAndNotCreatable, PartitionHelper.PartitionIdProperties.GetInstance(typeof(TPartition)).PropertyType.Name));
            partitionKey = await source.Partition.GetKeyForInstanceAsync<TPartition>(partitionObject);
        }

        await source.Partition.SetAsync<TPartition>(partitionObject, partitionName);
        //this will be simply source.Query<T>() when all DataSources will support Partition.SetAsync
        var ret = partitionKey == null ? source.Query<T>() : source.Query<T>().WithPartitionKey(partitionKey);
        await source.Partition.SetAsync<TPartition>(value, partitionName);

        return ret;
    }

    private async Task<Dictionary<(string, object), IHashSetWrapper>> InitializeTypeDictionaryAsync(Type t, IPartitionVariable partitionVariable, bool snapshot, IEnumerable inputCollection = null)
    {
        return (Dictionary<(string, object), IHashSetWrapper>) await InitializeTypeDictionaryAsyncMethod.MakeGenericMethod(t, t.GetSingleCustomAttribute<TargetTypeAttribute>()?.Type ?? t)
                                                                                                        .InvokeAsFunctionAsync(this, partitionVariable, snapshot, inputCollection);
    }
#pragma warning disable 4014
    private static readonly MethodInfo InitializeTypeDictionaryAsyncMethod = ReflectionHelper.GetMethodGeneric<WorkspaceStorage>(x => x.InitializeTypeDictionaryAsync<object, object>(null, false, null));
#pragma warning restore 4014
    
    private async Task<Dictionary<(string, object), IHashSetWrapper>> InitializeTypeDictionaryAsync<T, TEntity>(IPartitionVariable partitionVariable, bool snapshot, IEnumerable<T> inputCollection = null)
    {
        var inputCollectionList = inputCollection != null ? inputCollection.ToList() : new List<T>();
        var options = GetInitOptions<T>(snapshot);
        if (!partitionedInstancesByType.TryGetValue(typeof(T), out var ret))
        {
            partitionedInstancesByType[typeof(T)] = ret = new Dictionary<(string, object), IHashSetWrapper>();
            //if not present, we have to seek for init functions
            
            if (options.InitFunctions.TryGetValue(typeof(T), out var func) && !options.DisabledInitialization.Contains(typeof(T)))
            {
                var listAsync = await ((Func<IAsyncEnumerable<T>>)func)().ToArrayAsync();
                inputCollectionList = inputCollectionList.Union(listAsync).ToList();
            }
        }

        var partitionName = partitionVariable.GetAssociatedPartition(typeof(T)).Name;
        
        foreach (var initItem in inputCollectionList)
        {
            var partitionKey = partitionName == null
                                   ? null //unPartitioned
                                   //we get it either from instance, or from current partition for given name
                                   : PartitionHelper.GetPartitionKeyByFromProperty(initItem, typeof(T)) ?? partitionVariable.GetCurrent(partitionName);
            var collection = await GetOrAddCollectionAsync(ret, partitionName, partitionKey, snapshot, _ => Task.FromResult<IHashSetWrapper<T>>(new HashSetWrapper<T>(IdentityEqualityComparer<T, TEntity>.Instance)));
            collection.Add(initItem); // TODO V10: seems this also has to be inside lock (2022/03/14, Dmitry Kalabin)
        }
        //find next base type and execute it
        var baseType = GetBaseTypes(typeof(T), false).FirstOrDefault();
        if (baseType!=null && !options.DisabledInitialization.Contains(typeof(T)))//here we stop initialization for base types if initialization is disabled
            //since we have information that only current type is snapshot, but base type is not marked as snapshot, thats why we pass here false
            await InitializeTypeDictionaryAsync(baseType, partitionVariable, false, inputCollectionList);

        return ret;
    }
}