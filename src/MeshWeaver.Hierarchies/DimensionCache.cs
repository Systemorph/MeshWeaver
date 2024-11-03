using System.Collections.Concurrent;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;

namespace MeshWeaver.Hierarchies;

public class DimensionCache(IWorkspace workspace, EntityStore store)
{
    public bool Has(Type type)
    {
        return workspace.DataContext.MappedTypes.Contains(type);
    }



    public T Get<T>(object id)
        where T : class =>
        (T)Get(typeof(T))?.GetValueOrDefault(id);

    public IReadOnlyDictionary<object,object> Get(Type type)
    {
        var collection = workspace.DataContext.GetCollectionName(type);
        return collection == null ? null : store.GetCollection(collection)?.Instances;
    }

    public HierarchyNode<T> GetHierarchy<T>(object id)
        where T : class, IHierarchicalDimension
    {
        return GetHierarchy<T>()?.GetNode(id);
    }

    private readonly ConcurrentDictionary<Type, object> hierarchies = new();

    private static readonly MethodInfo GetHierarchyMethod =
        ReflectionHelper.GetMethodGeneric<DimensionCache>(x => x.GetHierarchy<IHierarchicalDimension>());
    public IHierarchy<T> GetHierarchy<T>()
        where T : class, IHierarchicalDimension
    {
        return (IHierarchy<T>)hierarchies.GetOrAdd(typeof(T), _ => new Hierarchy<T>(Get(typeof(T))));
    }

    public IHierarchy GetHierarchy(Type type)
        => (IHierarchy)GetHierarchyMethod.MakeGenericMethod(type).InvokeAsFunction(this);

    public T Parent<T>(string dim) where T : class, IHierarchicalDimension => GetHierarchy<T>(dim)?.Parent;

    private readonly ConcurrentDictionary<Type, int> maxHierarchyDataLevels = new();

    public int GetMaxHierarchyDataLevel(Type type) => maxHierarchyDataLevels.GetValueOrDefault(type);
    public int SetMaxHierarchyDataLevel(Type type, int maxDataLevel) => maxHierarchyDataLevels[type] = maxDataLevel;

}
